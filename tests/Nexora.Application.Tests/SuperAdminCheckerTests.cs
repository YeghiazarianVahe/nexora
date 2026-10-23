using FluentAssertions;
using Microsoft.Extensions.Options;
using Nexora.Application;

namespace Nexora.Application.Tests;

public sealed class SuperAdminCheckerTests
{
    private static SuperAdminChecker Create(string sid = "S-1-5-21-1-2-3-46118", string account = "CONTOSO\\demo.admin") =>
        new(Options.Create(new NexoraOptions { SuperAdmin = new SuperAdminOptions { Sid = sid, Account = account } }));

    [Theory]
    [InlineData("S-1-5-21-1-2-3-46118", true)]
    [InlineData("s-1-5-21-1-2-3-46118", true)]
    [InlineData(" S-1-5-21-1-2-3-46118 ", true)]
    [InlineData("S-1-5-21-1-2-3-46119", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Matches_by_sid_ordinal_ignore_case(string? sid, bool expected) =>
        Create().IsSuperAdmin(sid).Should().Be(expected);

    [Fact]
    public void Unconfigured_sid_never_matches_empty_input() =>
        Create(sid: "").IsSuperAdmin("").Should().BeFalse();

    [Fact]
    public void Account_label_compare_is_case_insensitive() =>
        Create().AccountLabelMismatch("S-1-5-21-1-2-3-46118", "contoso\\DEMO.ADMIN").Should().BeFalse();

    [Fact]
    public void Account_label_mismatch_is_reported_only_for_super_admin()
    {
        var checker = Create();
        checker.AccountLabelMismatch("S-1-5-21-1-2-3-46118", "CONTOSO\\other").Should().BeTrue();
        checker.AccountLabelMismatch("S-1-5-21-1-2-3-99999", "CONTOSO\\other").Should().BeFalse();
    }
}
