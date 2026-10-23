using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Application;
using Nexora.Application.Services;

namespace Nexora.Application.Tests;

/// <summary>F3: which profiles are listed at all, who may be deleted, where "last used" comes from, and the bounded reader.</summary>
public sealed class ProfileRulesTests
{
    private const string UserSid = "S-1-5-21-1-2-3-4001";

    private static ProfileInfo P(string sid = UserSid, string? account = @"CORP\old.user", string path = @"C:\Users\old.user", bool loaded = false, bool special = false) =>
        new(sid, account, path, DateTime.UtcNow.AddDays(-90), loaded, special, false, null);

    private static readonly SignedInUsers Nobody = new([], []);

    // ---------- system-profile filter

    [Theory]
    [InlineData("S-1-5-18", @"C:\Windows\system32\config\systemprofile", false, true)]       // SYSTEM
    [InlineData("S-1-5-19", @"C:\Windows\ServiceProfiles\LocalService", false, true)]         // LOCAL SERVICE
    [InlineData("S-1-5-20", @"C:\Windows\ServiceProfiles\NetworkService", false, true)]       // NETWORK SERVICE
    [InlineData(UserSid, @"C:\Users\old.user", true, true)]                                   // the Special flag alone is enough
    [InlineData("S-1-5-80-1-2-3-4-5", @"C:\Users\svc", false, true)]                          // a service SID is not a person
    [InlineData("S-1-5-21-1-2-3", @"C:\Users\short", false, true)]                            // too few sub-authorities
    [InlineData("S-1-5-21-1-2-3-4001-9", @"C:\Users\long", false, true)]                      // too many
    [InlineData("S-1-5-21-1-2-3-4001\n", @"C:\Users\newline", false, true)]                   // anchored: a trailing newline does not pass
    [InlineData("S-1-12-1-1-2-3", @"C:\Users\aad.short", false, true)]                        // Azure AD needs four sub-authorities
    [InlineData(UserSid, @"C:\Users\Default", false, true)]
    [InlineData(UserSid, @"C:\Users\Default User", false, true)]
    [InlineData(UserSid, @"C:\Users\Public", false, true)]
    [InlineData(UserSid, @"c:\users\public", false, true)]
    [InlineData(UserSid, @"C:\Users\All Users", false, true)]
    [InlineData(UserSid, @"C:\Users\defaultuser0", false, true)]
    [InlineData(UserSid, @"C:\Windows\Temp\x", false, true)]
    [InlineData(UserSid, @"C:\Users\old.user", false, false)]
    [InlineData("S-1-12-1-1111-2222-3333-4444", @"C:\Users\aad.user", false, false)]           // Azure AD user
    [InlineData("S-1-5-21-1-2-3-500", @"C:\Users\Administrator", false, false)]                // listed (not deletable, see below)
    public void System_default_and_non_user_profiles_are_filtered_out_of_the_list(string sid, string path, bool special, bool system) =>
        ProfileRules.IsSystemProfile(P(sid, path: path, special: special)).Should().Be(system);

    [Theory]
    [InlineData(@"C:\Users\Public ")]
    [InlineData(@"C:\Users\Public.")]
    [InlineData(@"C:\Users\Default.")]
    [InlineData(@"C:\Users\Default User . ")]
    [InlineData(@"C:\Users\All Users.")]
    public void Windows_ignores_trailing_dots_and_spaces_so_those_names_are_still_system_profiles(string path) =>
        ProfileRules.IsSystemProfile(P(path: path)).Should().BeTrue();

    [Theory]
    [InlineData(@"C:\Users\.. ")]
    [InlineData(@"C:\Users\..")]
    [InlineData(@"C:\Users\x.")]
    [InlineData(@"C:\Users\x ")]
    [InlineData(@"C:\Users\ x")]
    [InlineData(@"C:\Users\.hidden")]
    [InlineData(@"C:\Users\ADMINI~1")]
    [InlineData(@"C:\Users\DEFAUL~1")]
    [InlineData(@"C:\Users\%USERPROFILE%")]
    [InlineData(@"C:\Users\%USERNAME%")]
    [InlineData("C:\\Users\\\u00dcnal")]
    [InlineData("C:\\Users\\\u0561\u0580\u0561")]
    [InlineData("C:\\Users\\x\u0001y")]
    [InlineData("C:\\Users\\x\0")]
    [InlineData("C:\\Users\\x\n")]
    public void Folder_names_Windows_would_normalise_or_that_hold_control_characters_are_never_deletable(string path) =>
        ProfileRules.BlockReason(P(path: path), Nobody, "S-1-5-21-1-2-3-1000", null, true).Should().NotBeNull();

    // ---------- who may be deleted

    [Fact]
    public void A_non_ASCII_name_says_the_name_is_not_supported()
    {
        ProfileRules.BlockReason(P(path: "C:\\Users\\\u00dcnal"), Nobody, "S-1-5-21-1-2-3-1000", null, true).Should().Be("profile folder name not supported");
        ProfileRules.BlockReason(P(path: @"C:\Users\ADMINI~1"), Nobody, "S-1-5-21-1-2-3-1000", null, true).Should().Be("profile folder name not supported");
        ProfileRules.BlockReason(P(path: @"C:\Users\Jo-hn_2.x y"), Nobody, "S-1-5-21-1-2-3-1000", null, true).Should().BeNull("ASCII letters, digits, . _ - and inner spaces are fine");
    }

    [Fact]
    public void Two_profiles_on_one_folder_are_both_not_deletable_even_when_one_is_a_system_profile()
    {
        var bob = P("S-1-5-21-1-2-3-5", "bob", @"C:\Users\bob");
        var alias = P("S-1-5-21-1-2-3-6", "alice", @"C:\Users\BOB");
        var system = new ProfileInfo("S-1-5-18", null, @"C:\Users\bob", null, false, false, false, null);
        var me = "S-1-5-21-1-2-3-1000";

        ProfileRules.BlockReason(bob, Nobody, me, null, true, [bob, alias]).Should().Contain("same folder");
        ProfileRules.BlockReason(alias, Nobody, me, null, true, [bob, alias]).Should().Contain("same folder");
        ProfileRules.BlockReason(bob, Nobody, me, null, true, [bob, system]).Should().Contain("same folder");
        ProfileRules.BlockReason(bob, Nobody, me, null, true, [bob, P("S-1-5-21-1-2-3-7", "carol", @"C:\Users\carol")]).Should().BeNull();
    }

    [Fact]
    public void An_ordinary_idle_profile_is_deletable_by_an_admin()
    {
        ProfileRules.BlockReason(P(), Nobody, "S-1-5-21-1-2-3-1000", null, isAdmin: true).Should().BeNull();
    }

    [Fact]
    public void Each_reason_blocks_and_the_signed_in_unknown_case_fails_safe()
    {
        var me = "S-1-5-21-1-2-3-1000";
        ProfileRules.BlockReason(P(loaded: true), Nobody, me, null, true).Should().Be(ProfileRules.SignedInNow);
        ProfileRules.BlockReason(P(), null, me, null, true).Should().Be(ProfileRules.SignedInUnknown, "undeterminable means not deletable");
        ProfileRules.BlockReason(P(), new SignedInUsers([UserSid.ToLowerInvariant()], []), me, null, true).Should().Be(ProfileRules.SignedInNow, "SIDs are compared case-insensitively");
        ProfileRules.BlockReason(P(), new SignedInUsers([], [@"corp\OLD.user"]), me, null, true).Should().Be(ProfileRules.SignedInNow, "the console user is matched by name");
        ProfileRules.BlockReason(P(), Nobody, UserSid, null, true).Should().Be("This is your own profile.");
        ProfileRules.BlockReason(P("S-1-5-21-1-2-3-500", path: @"C:\Users\Administrator"), Nobody, me, null, true).Should().Contain("Administrator");
        ProfileRules.BlockReason(P(account: null), Nobody, me, null, true).Should().Contain("could not be verified");
        ProfileRules.BlockReason(P(account: "  "), Nobody, me, null, true).Should().Contain("could not be verified");
        ProfileRules.BlockReason(P(path: @"D:\Profiles\old.user"), Nobody, me, null, true).Should().Contain("not directly under");
        ProfileRules.BlockReason(P(), Nobody, me, "protected list", true).Should().Contain("Protected workstation").And.Contain("protected list");
        ProfileRules.BlockReason(P(), Nobody, me, null, isAdmin: false).Should().Contain("Admin role");
        ProfileRules.BlockReason(P(special: true), Nobody, me, null, true).Should().Contain("System profiles");
    }

    [Fact]
    public void A_state_reason_wins_over_the_role_reason()
    {
        ProfileRules.BlockReason(P(loaded: true), Nobody, "S-1-5-21-1-2-3-1000", null, isAdmin: false).Should().Be(ProfileRules.SignedInNow);
    }

    // ---------- last used

    [Fact]
    public void A_FILETIME_in_two_DWORDs_decodes_to_the_right_instant()
    {
        var moment = new DateTime(2025, 3, 14, 15, 9, 26, DateTimeKind.Utc);
        var ft = moment.ToFileTimeUtc();

        ProfileLastUsed.FromFileTime((uint)(ft >> 32), (uint)(ft & 0xFFFFFFFF)).Should().Be(moment);
        ProfileLastUsed.FromFileTime(0, 0).Should().BeNull("zero means never");
        ProfileLastUsed.FromFileTime(0, 1000).Should().BeNull("a 1601 date is not a real use");
        ProfileLastUsed.FromFileTime(uint.MaxValue, uint.MaxValue).Should().BeNull("beyond the representable range");
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    [InlineData("hy-AM")]
    public void Decoding_does_not_depend_on_the_machines_culture(string culture)
    {
        var moment = new DateTime(2025, 12, 31, 23, 59, 58, DateTimeKind.Utc);
        var ft = moment.ToFileTimeUtc();
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            ProfileLastUsed.FromFileTime((uint)(ft >> 32), (uint)(ft & 0xFFFFFFFF)).Should().Be(moment);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    private static readonly DateTime Jan = new(2025, 1, 10, 8, 0, 0, DateTimeKind.Utc), Feb = new(2025, 2, 10, 8, 0, 0, DateTimeKind.Utc),
        Mar = new(2025, 3, 10, 8, 0, 0, DateTimeKind.Utc), Apr = new(2025, 4, 10, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ProfileList_beats_Wmi_and_the_newer_of_load_and_unload_is_used()
    {
        // ProfileList present: it wins even though WMI is newer, and the larger of load/unload is taken.
        ProfileLastUsed.Choose(Jan, Feb, Mar).Should().Be((Feb, "ProfileList"));
        ProfileLastUsed.Choose(Feb, Jan, Mar).Should().Be((Feb, "ProfileList"));
        ProfileLastUsed.Choose(Jan, null, Mar).Should().Be((Jan, "ProfileList"));
        ProfileLastUsed.Choose(null, Jan, Mar).Should().Be((Jan, "ProfileList"));
        // Neither: WMI. Nothing at all: unknown.
        ProfileLastUsed.Choose(null, null, Mar).Should().Be((Mar, "Wmi"));
        ProfileLastUsed.Choose(null, null, null).Should().Be(((DateTime?)null, (string?)null));
    }

    // ---------- size output

    [Fact]
    public void Only_well_formed_complete_user_measurements_are_read_from_the_helper_output()
    {
        var lines = new[]
        {
            "Measuring C:\\Users\\a (1 of 4)",                                                            // a log line, not a measurement
            """{"sid":"S-1-5-21-1-2-3-4001","path":"C:\\Users\\a","bytes":1234567,"files":10,"errors":0,"partial":false}""",
            """{"sid":"S-1-5-21-1-2-3-4002","path":"C:\\Users\\b","bytes":99,"files":1,"errors":2,"partial":true}""",   // partial: understated, skipped
            """{"sid":"S-1-5-18","path":"C:\\x","bytes":5,"files":1,"errors":0,"partial":false}""",                      // system SID
            """{"sid":"S-1-5-21-1-2-3-4003","path":"C:\\Users\\c","bytes":-1,"files":1,"errors":0,"partial":false}""",   // negative
            """{"sid":"S-1-5-21-1-2-3-4004","path":"C:\\Users\\d","bytes":"12","files":1,"errors":0,"partial":false}""", // not a number
            "{ not json",
            """{"sid":"S-1-5-21-1-2-3-4005","path":"C:\\Users\\e","bytes":0,"files":0,"errors":0,"partial":false}""",
        };

        ProfileSizeOutput.Parse(lines, null).Select(m => (m.Sid, m.Bytes)).Should().Equal(("S-1-5-21-1-2-3-4001", 1234567L), ("S-1-5-21-1-2-3-4005", 0L));
        ProfileSizeOutput.Parse(lines, "s-1-5-21-1-2-3-4005").Select(m => m.Sid).Should().Equal("S-1-5-21-1-2-3-4005");
    }

    // ---------- the bounded reader

    private sealed class Clock : ISystemClock { public DateTime UtcNow { get; set; } = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc); }

    private sealed class Pc(Func<string, CancellationToken, Task<IReadOnlyList<ProfileInfo>>> profiles, Func<string, CancellationToken, Task<SignedInUsers>> signedIn) : IEndpointCollector, IProfileCollector
    {
        public int ProfileCalls, SignedInCalls;
        public Task<IReadOnlyList<ProfileInfo>> CollectProfilesAsync(string host, CancellationToken ct) { Interlocked.Increment(ref ProfileCalls); return profiles(host, ct); }
        public Task<SignedInUsers> CollectSignedInAsync(string host, CancellationToken ct) { Interlocked.Increment(ref SignedInCalls); return signedIn(host, ct); }
        public Task<EndpointSample> CollectSampleAsync(string host, CancellationToken ct) => throw new NotSupportedException();
        public Task<EndpointDetailDto> CollectDetailAsync(string host, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DiskRaw>> CollectDisksAsync(string host, CancellationToken ct) => throw new NotSupportedException();
    }

    private static ProfileReader Reader(Pc pc, Clock? clock = null, int timeoutSeconds = 45) =>
        new(pc, pc, Options.Create(new NexoraOptions { Monitoring = { ProfileReadTimeoutSeconds = timeoutSeconds } }), clock ?? new Clock(), NullLogger<ProfileReader>.Instance);

    private static Pc Healthy() => new((_, _) => Task.FromResult<IReadOnlyList<ProfileInfo>>([P()]), (_, _) => Task.FromResult(Nobody));

    [Fact]
    public async Task A_read_returns_the_profiles_and_who_is_signed_in()
    {
        var read = await Reader(Healthy()).ReadAsync("PC-1", TimeSpan.FromSeconds(5), CancellationToken.None);

        read.Ok.Should().BeTrue();
        read.Value!.Profiles.Should().ContainSingle();
        read.Value.SignedIn.Should().NotBeNull();
    }

    [Fact]
    public async Task When_who_is_signed_in_cannot_be_read_the_snapshot_says_unknown_instead_of_nobody()
    {
        var pc = new Pc((_, _) => Task.FromResult<IReadOnlyList<ProfileInfo>>([P()]), (_, _) => throw new InvalidOperationException("RPC server unavailable"));

        var read = await Reader(pc).ReadAsync("PC-1", TimeSpan.Zero, CancellationToken.None);

        read.Ok.Should().BeTrue();
        read.Value!.SignedIn.Should().BeNull();
        ProfileRules.BlockReason(read.Value.Profiles[0], read.Value.SignedIn, "S-1-5-21-1-2-3-1000", null, true).Should().Be(ProfileRules.SignedInUnknown);
    }

    [Fact]
    public async Task A_host_that_cannot_be_read_gives_a_typed_result_with_no_raw_exception_text()
    {
        var pc = new Pc((_, _) => throw new InvalidOperationException("RPC server 10.99.99.99 \\\\internal-share secret"), (_, _) => Task.FromResult(Nobody));

        var read = await Reader(pc).ReadAsync("PC-1", TimeSpan.FromSeconds(5), CancellationToken.None);

        read.Ok.Should().BeFalse();
        read.Reason.Should().Be(ProfileReader.ReadFailed).And.NotContain("secret");
    }

    [Fact]
    public async Task Repeated_reads_come_from_the_short_cache_and_a_zero_age_always_reads_again()
    {
        var pc = Healthy();
        var reader = Reader(pc);

        await reader.ReadAsync("PC-1", TimeSpan.FromSeconds(5), CancellationToken.None);
        await reader.ReadAsync("PC-1", TimeSpan.FromSeconds(5), CancellationToken.None);
        pc.ProfileCalls.Should().Be(1);

        await reader.ReadAsync("PC-1", TimeSpan.Zero, CancellationToken.None);
        await reader.ReadAsync("PC-1", TimeSpan.Zero, CancellationToken.None);
        pc.ProfileCalls.Should().Be(3, "before a deletion is accepted the PC is always asked again");
    }

    [Fact]
    public async Task A_failed_host_is_remembered_briefly_so_a_refresh_loop_does_not_hammer_it()
    {
        var clock = new Clock();
        var fail = true;
        var pc = new Pc((_, _) => fail ? throw new InvalidOperationException("down") : Task.FromResult<IReadOnlyList<ProfileInfo>>([P()]), (_, _) => Task.FromResult(Nobody));
        var reader = Reader(pc, clock);

        (await reader.ReadAsync("PC-1", TimeSpan.FromSeconds(5), CancellationToken.None)).Ok.Should().BeFalse();
        (await reader.ReadAsync("PC-1", TimeSpan.FromSeconds(5), CancellationToken.None)).Ok.Should().BeFalse();
        pc.ProfileCalls.Should().Be(1);

        fail = false;
        clock.UtcNow = clock.UtcNow.AddSeconds(6);
        (await reader.ReadAsync("PC-1", TimeSpan.FromSeconds(5), CancellationToken.None)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task The_last_good_read_is_kept_for_showing_an_offline_workstation()
    {
        var clock = new Clock();
        var fail = false;
        var pc = new Pc((_, _) => fail ? throw new InvalidOperationException("down") : Task.FromResult<IReadOnlyList<ProfileInfo>>([P()]), (_, _) => Task.FromResult(Nobody));
        var reader = Reader(pc, clock);

        reader.LastKnown("PC-1").Should().BeNull();
        await reader.ReadAsync("PC-1", TimeSpan.Zero, CancellationToken.None);
        fail = true;
        (await reader.ReadAsync("PC-1", TimeSpan.Zero, CancellationToken.None)).Ok.Should().BeFalse();

        reader.LastKnown("PC-1")!.Profiles.Should().ContainSingle();
        reader.LastKnown("PC-2").Should().BeNull();
    }

    [Fact]
    public async Task At_most_four_reads_run_at_once_whatever_the_number_of_callers()
    {
        var running = 0;
        var peak = 0;
        var release = new TaskCompletionSource();
        var pc = new Pc(async (_, _) =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            do { seen = Volatile.Read(ref peak); } while (now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen);
            await release.Task;
            Interlocked.Decrement(ref running);
            return [P()];
        }, (_, _) => Task.FromResult(Nobody));
        var reader = Reader(pc);

        var reads = Enumerable.Range(0, 10).Select(i => reader.ReadAsync("PC-" + i, TimeSpan.Zero, CancellationToken.None)).ToList();
        await Task.Delay(300);
        peak.Should().BeLessThanOrEqualTo(ProfileReader.MaxConcurrent);
        release.SetResult();
        var results = await Task.WhenAll(reads);

        results.Count(r => r.Ok).Should().BeGreaterThanOrEqualTo(ProfileReader.MaxConcurrent);
        peak.Should().BeLessThanOrEqualTo(ProfileReader.MaxConcurrent);
    }

    [Fact]
    public async Task A_read_that_never_finishes_ends_at_the_deadline_with_a_typed_result()
    {
        var pc = new Pc(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return []; }, (_, _) => Task.FromResult(Nobody));

        var read = await Reader(pc, timeoutSeconds: 2).ReadAsync("PC-1", TimeSpan.Zero, CancellationToken.None);

        read.Ok.Should().BeFalse();
        read.Reason.Should().Be(ProfileReader.ReadFailed);
    }
}
