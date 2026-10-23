using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Api.Controllers;
using Nexora.Api.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public sealed class HelperCoverageTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public void Each_configured_PC_is_connected_not_connected_or_never_seen()
    {
        var d = new HelperDiagnostics();
        d.PcConnected("c1", "wks-44", "S-1-5-21-1-1-1-1001", @"CONTOSO\arman", true, "10.0.0.44");
        d.PcConnected("c2", "wks-50", "S-1-5-21-1-1-1-1002", @"CONTOSO\ani", true, "10.0.0.50");
        d.PcDisconnected("c2");

        var c = HelperCoverageController.Build(d, ["WKS-44", "wks-50", "wks-25", "wks-44"]);

        c.Total.Should().Be(3, "a name listed twice or in another case counts once");
        (c.Connected, c.NotConnected, c.NeverSeen).Should().Be((1, 1, 1));
        c.Pcs.Single(p => p.Pc == "wks-44").Sid.Should().Be("S-1-5-21-1-1-1-1001");
        c.Pcs.Single(p => p.Pc == "wks-50").LastSeenUtc.Should().NotBeNull();
        c.Pcs.Single(p => p.Pc == "wks-25").State.Should().Be("never seen");
    }

    [Fact]
    public void A_helper_from_a_PC_not_on_the_list_is_shown_but_not_counted()
    {
        var d = new HelperDiagnostics();
        d.PcConnected("c1", "wks-stranger", "S-1-5-21-9", null, false, null);

        var c = HelperCoverageController.Build(d, ["wks-44"]);

        c.Total.Should().Be(1);
        c.Pcs.Should().Contain(p => p.Pc == "wks-stranger" && !p.Configured && !p.NameVerified);
    }

    [Fact]
    public void A_PC_with_two_connections_stays_connected_until_the_last_one_drops()
    {
        var d = new HelperDiagnostics();
        d.PcConnected("a", "wks-44", "S", null, true, null);
        d.PcConnected("b", "wks-44", "S", null, true, null);
        d.PcDisconnected("a");
        d.Pc("wks-44")!.Connected.Should().BeTrue();
        d.PcDisconnected("b");
        d.Pc("wks-44")!.Connected.Should().BeFalse();
    }

    [Theory]
    [InlineData("WKS-44", "wks-44")]
    [InlineData("  wks-007 ", "wks-007")]
    [InlineData("a/b", null)]
    [InlineData("a b", null)]
    [InlineData("", null)]
    public void The_self_reported_machine_name_is_reduced_to_a_plain_host_name(string raw, string? expected) =>
        HelperDiagnostics.CleanMachine(raw).Should().Be(expected);

    [Fact]
    public async Task The_coverage_list_is_for_admins_only_and_comes_from_configuration()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        (await op.GetAsync("/api/helper-coverage")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var body = await admin.GetFromJsonAsync<HelperCoverageController.Coverage>("/api/helper-coverage");
        body!.Pcs.Select(p => p.Pc).Should().Contain(["wks-50", "wks-111", "wks-25"], "the 11 operator PCs are seeded in appsettings.json");
        body.Total.Should().Be(11);
    }
}

public sealed class PublicUrlTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task The_install_address_comes_from_configuration_never_from_the_Host_header()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/helper/status");
        request.Headers.Host = "evil.example.com:6666";

        var json = await (await op.SendAsync(request)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        var url = json.GetProperty("serverUrl").GetString()!;
        url.Should().NotContain("evil").And.StartWith("http://").And.Contain(":5090");
    }

    [Theory]
    [InlineData("http://wks-111:5090/", "http://wks-111:5090")]
    [InlineData("https://nexora.contoso.example/some/path", "https://nexora.contoso.example")]
    public void A_configured_public_url_is_normalised_to_its_origin(string configured, string expected) =>
        new Nexora.Application.NexoraOptions { PublicUrl = configured }.ResolvePublicUrl().Should().Be(expected);

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    [InlineData("ftp://x")]
    public void A_bad_public_url_falls_back_to_this_PC_and_the_listening_port(string configured)
    {
        var o = new Nexora.Application.NexoraOptions { PublicUrl = configured, Url = "http://+:5090/" };
        o.ResolvePublicUrl().Should().Be($"http://{Environment.MachineName.ToLowerInvariant()}:5090");
    }
}

public sealed class UnansweredChallengeTests
{
    [Fact]
    public void Many_unanswered_challenges_from_one_address_are_reported_once_and_a_success_clears_them()
    {
        var d = new Nexora.Api.Services.HelperDiagnostics();
        var t = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var reported = 0;
        for (var i = 0; i < 12; i++) if (d.Challenge("10.0.0.7", t.AddSeconds(i))) reported++;
        reported.Should().Be(1, "reported once, not on every attempt");
        d.Unanswered401s("10.0.0.7").Should().Be(12);

        d.Challenge("10.0.0.7", t.AddMinutes(11)).Should().BeTrue("and again after ten minutes if it is still failing");

        d.ChallengesAnswered("10.0.0.7");
        d.Unanswered401s("10.0.0.7").Should().Be(0);
        for (var i = 0; i < 2; i++) d.Challenge("10.0.0.8", t).Should().BeFalse("the one or two challenges of a normal sign-in are not a problem");
    }
}
