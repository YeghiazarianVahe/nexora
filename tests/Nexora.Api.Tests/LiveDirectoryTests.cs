using System.Security.Principal;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Infrastructure.Ad;

namespace Nexora.Api.Tests;

/// <summary>
/// Opt-in checks against the real domain (set NEXORA_LIVE_AD=1 on a domain-joined PC). They only read AD.
/// Without the variable they return immediately, so CI and non-domain machines stay green.
/// </summary>
public sealed class LiveDirectoryTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("NEXORA_LIVE_AD") == "1";
    private static readonly AdDirectoryLookup Directory = new(NullLogger<AdDirectoryLookup>.Instance);
    private static string MySid => WindowsIdentity.GetCurrent().User!.Value;

    [Fact]
    public void Current_user_resolves_with_a_display_name()
    {
        if (!Enabled) return;
        var user = Directory.FindUserBySid(MySid);
        user.Should().NotBeNull();
        user!.DisplayName.Should().NotBeNullOrWhiteSpace();
        user.Account.Should().Contain("\\");
    }

    [Fact]
    public void Access_group_resolves_and_membership_includes_distribution_groups()
    {
        if (!Enabled) return;
        Directory.ResolveGroupSid("IT Service Desk").Should().StartWith("S-1-5-21-");
        Directory.IsUserInGroup(MySid, "IT Service Desk").Should().BeTrue("the developer account is in the group");
        Directory.IsUserInGroup("S-1-5-21-1-2-3-999999", "IT Service Desk").Should().BeFalse();
        Directory.IsUserInGroup(MySid, "No Such Group Nexora").Should().BeFalse();
    }

    [Fact]
    public void Directory_searches_return_results()
    {
        if (!Enabled) return;
        var me = Directory.FindUserBySid(MySid)!;
        Directory.SearchUsers(me.DisplayName.Split(' ')[0], 25).Should().Contain(u => u.Sid == MySid);
        Directory.SearchComputers(Environment.MachineName.Substring(0, 4), 50).Should().NotBeEmpty();
    }

    [Fact]
    public void Hostile_search_text_cannot_widen_the_query()
    {
        if (!Enabled) return;
        Directory.SearchUsers("*)(objectClass=*", 25).Should().BeEmpty();
        Directory.SearchComputers("*)(cn=*", 50).Should().BeEmpty();
    }
}
