using FluentAssertions;
using Nexora.Helper;
using Nexora.Helper.Actions;

namespace Nexora.Application.Tests;

public sealed class ProfileSizeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexora-size-" + Guid.NewGuid().ToString("N"));

    public ProfileSizeTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Theory]
    [InlineData("PC-1", @"C:\Users\jdoe", @"\\PC-1\C$\Users\jdoe")]
    [InlineData("LAB-2", @"D:\Profiles\old.user", @"\\LAB-2\D$\Profiles\old.user")]
    public void Remote_profiles_are_read_through_the_admin_share(string host, string local, string expected) =>
        ProfileSizesAction.AdminSharePath(host, local).Should().Be(expected);

    [Fact]
    public void The_helpers_own_pc_is_read_locally()
    {
        ProfileSizesAction.AdminSharePath(Environment.MachineName, @"C:\Users\me").Should().Be(@"C:\Users\me");
        ProfileSizesAction.AdminSharePath("localhost", @"C:\Users\me").Should().Be(@"C:\Users\me");
    }

    [Fact]
    public void Sizes_add_up_across_nested_folders()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a", "b"));
        File.WriteAllBytes(Path.Combine(_root, "one.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(_root, "a", "two.bin"), new byte[2500]);
        File.WriteAllBytes(Path.Combine(_root, "a", "b", "three.bin"), new byte[500]);

        var (bytes, files, errors, partial) = ProfileSizesAction.Measure(_root, CancellationToken.None, () => { });

        (bytes, files, errors, partial).Should().Be((4000, 3, 0, false));
    }

    [Fact]
    public void A_missing_folder_is_reported_as_partial_not_a_crash()
    {
        var result = ProfileSizesAction.Measure(Path.Combine(_root, "nope"), CancellationToken.None, () => { });
        result.Bytes.Should().Be(0);
        result.Partial.Should().BeTrue();
    }

    [Fact]
    public void Links_are_not_followed_so_nothing_is_counted_twice_or_outside_the_profile()
    {
        var outside = Path.Combine(_root, "outside");
        var profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(profile);
        File.WriteAllBytes(Path.Combine(outside, "big.bin"), new byte[10_000]);
        File.WriteAllBytes(Path.Combine(profile, "mine.bin"), new byte[100]);
        try { Directory.CreateSymbolicLink(Path.Combine(profile, "link"), outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; } // no symlink right on this machine

        ProfileSizesAction.Measure(profile, CancellationToken.None, () => { }).Bytes.Should().Be(100);
    }

    [Fact]
    public void Measuring_can_be_cancelled()
    {
        for (var i = 0; i < 50; i++) File.WriteAllBytes(Path.Combine(_root, $"f{i}.bin"), new byte[10]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => ProfileSizesAction.Measure(_root, cts.Token, () => { });
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void The_helper_knows_the_profile_action_and_refuses_unknown_codes()
    {
        ActionRegistry.IsKnown("profile.sizes").Should().BeTrue();
        ActionRegistry.IsKnown("format.disk").Should().BeFalse();
        HelperJobs.IsKnown("demo.simulate").Should().BeTrue();
    }
}
