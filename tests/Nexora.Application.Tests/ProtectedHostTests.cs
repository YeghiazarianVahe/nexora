using FluentAssertions;
using Nexora.Application.Jobs;
using Nexora.Domain;

namespace Nexora.Application.Tests;

/// <summary>M3: the Nexora server and protected hosts are recognised by name, FQDN, short name, IP and unknown OS, not only by the typed host name.</summary>
public sealed class ProtectedHostTests
{
    private static ManagedEndpoint Pc(string host, string? ip = null, string? os = "Microsoft Windows 11 Pro", string? reported = null, string? display = null) =>
        new() { Id = Guid.NewGuid(), Hostname = host, IpAddress = ip, OsCaption = os, ReportedHostname = reported, DisplayName = display };

    private static PlatformSettings Settings(params string[] protectedHosts) => new() { ProtectedHosts = protectedHosts.ToList() };

    [Fact]
    public void An_ordinary_workstation_is_not_protected()
    {
        JobService.ProtectedReason(Pc("wks-44", "10.20.30.44"), Settings()).Should().BeNull();
    }

    [Fact]
    public void The_server_is_recognised_by_fqdn_short_name_display_name_and_reported_name()
    {
        var me = Environment.MachineName;
        JobService.ProtectedReason(Pc(me + ".contoso.example"), Settings()).Should().Be("Nexora server");
        JobService.ProtectedReason(Pc(me.ToLowerInvariant()), Settings()).Should().Be("Nexora server");
        JobService.ProtectedReason(Pc("10.9.9.9", reported: me), Settings()).Should().Be("Nexora server");
        JobService.ProtectedReason(Pc("some-alias", display: me), Settings()).Should().Be("Nexora server");
    }

    [Fact]
    public void The_server_is_recognised_by_ip_address_even_when_listed_under_another_name()
    {
        JobService.ProtectedReason(Pc("127.0.0.1"), Settings()).Should().Be("Nexora server", "an inventory entry made from the loopback address is this PC");
        JobService.ProtectedReason(Pc("alias", ip: "::1"), Settings()).Should().Be("Nexora server");
    }

    [Fact]
    public void The_protected_list_matches_short_name_fqdn_and_ip_either_way_round()
    {
        JobService.ProtectedReason(Pc("dc-01.contoso.example"), Settings("dc-01")).Should().Be("protected list");
        JobService.ProtectedReason(Pc("dc-01"), Settings("DC-01.contoso.example")).Should().Be("protected list");
        JobService.ProtectedReason(Pc("pc-x", ip: "10.1.1.5"), Settings("10.1.1.5")).Should().Be("protected list");
        JobService.ProtectedReason(Pc("other"), Settings("dc-01")).Should().BeNull();
    }

    [Fact]
    public void A_server_os_and_an_unknown_os_are_protected()
    {
        JobService.ProtectedReason(Pc("fs-01", os: "Microsoft Windows Server 2022"), Settings()).Should().Be("server OS");
        JobService.ProtectedReason(Pc("never-seen", os: null), Settings()).Should().Be("operating system not known yet");
        JobService.ProtectedReason(Pc("blank", os: "  "), Settings()).Should().Be("operating system not known yet");
    }

    [Fact]
    public void The_pinned_server_is_always_protected()
    {
        var e = Pc("whatever");
        e.IsPinned = true;
        JobService.ProtectedReason(e, Settings()).Should().Be("Nexora server");
    }
}
