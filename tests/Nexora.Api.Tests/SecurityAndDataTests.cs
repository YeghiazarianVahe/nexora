using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public sealed class CsrfTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task State_changing_calls_without_an_antiforgery_token_are_rejected()
    {
        var client = factory.ClientFor(TestIdentities.NewSid());
        var response = await client.PostAsync("/api/access/request", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cross_origin_state_changing_calls_are_rejected_even_with_a_token()
    {
        var client = await factory.UserClientAsync(TestIdentities.NewSid(), role: null);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/access/request");
        request.Headers.Add("Origin", "http://evil.example");

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Same_origin_calls_are_allowed()
    {
        var client = await factory.UserClientAsync(TestIdentities.NewSid(), role: null);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/access/request");
        request.Headers.Add("Origin", "http://localhost");

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cross_origin_hub_negotiation_is_rejected()
    {
        var client = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var request = new HttpRequestMessage(HttpMethod.Post, "/hubs/helper/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", "http://evil.example");

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Shutdown_endpoint_is_not_discoverable_without_the_token()
    {
        var client = factory.ClientFor(null);
        (await client.PostAsync("/internal/shutdown", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var wrong = new HttpRequestMessage(HttpMethod.Post, "/internal/shutdown");
        wrong.Headers.Add("X-Nexora-Shutdown", "wrong");
        (await client.SendAsync(wrong)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

public sealed class AuditVisibilityTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task Viewers_see_only_their_own_entries_and_admins_see_all()
    {
        var viewerSid = TestIdentities.NewSid();
        var adminSid = TestIdentities.NewSid();
        var viewer = await factory.UserClientAsync(viewerSid, Role.Viewer, "TEST\\viewer");
        var admin = await factory.UserClientAsync(adminSid, Role.Admin, "TEST\\admin");

        // Each writes one audit entry of their own (an endpoint add is Admin-only; a settings write too).
        (await admin.PostAsJsonAsync("/api/endpoints", new { items = new[] { new { name = (string?)null, address = "AUD-" + Guid.NewGuid().ToString("N")[..8] } } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            db.AuditEntries.Add(new AuditEntry { TimestampUtc = DateTime.UtcNow, ActorSid = viewerSid, ActorAccount = "TEST\\viewer", Action = "test.viewer-entry" });
            await db.SaveChangesAsync();
        }

        var mine = (await viewer.GetFromJsonAsync<PagedResult<AuditEntryDto>>("/api/audit?pageSize=200"))!;
        mine.Items.Should().NotBeEmpty().And.OnlyContain(e => e.ActorSid == viewerSid);

        // A viewer asking for someone else's entries still only gets their own.
        var spoof = (await viewer.GetFromJsonAsync<PagedResult<AuditEntryDto>>($"/api/audit?actorSid={adminSid}"))!;
        spoof.Items.Should().OnlyContain(e => e.ActorSid == viewerSid);

        var all = (await admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>("/api/audit?pageSize=200"))!;
        all.Items.Select(e => e.ActorSid).Should().Contain([adminSid, viewerSid]);
    }
}

public sealed class EndpointAndSettingsTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task Admin_adds_endpoints_and_viewers_can_list_but_not_add()
    {
        var host = "PC-" + Guid.NewGuid().ToString("N")[..8];
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        (await viewer.PostAsJsonAsync("/api/endpoints", new { items = new[] { new { name = "Desk " + host, address = host } } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync("/api/endpoints", new { items = new[] { new { name = "Desk " + host, address = host } } })).StatusCode.Should().Be(HttpStatusCode.OK);

        var list = (await viewer.GetFromJsonAsync<List<EndpointSummaryDto>>($"/api/endpoints?search={host}"))!;
        list.Should().ContainSingle(e => e.Hostname == host);
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("pc;calc")]
    [InlineData("-leading-dash")]
    public async Task Invalid_hostnames_are_rejected(string host)
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        (await admin.PostAsJsonAsync("/api/endpoints", new { items = new[] { new { name = "Desk " + host, address = host } } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Process_contract_preserves_owner_and_optional_cpu_without_inventing_zero()
    {
        var id = await factory.SeedEndpointAsync(online: true);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        try
        {
            FakeCollector.DetailOverride = new EndpointDetailDto([], [
                new ProcessInfo(1234, "notepad.exe", 4096, @"TEST\owner", 12.5),
                new ProcessInfo(1235, "restricted.exe", 1024, null)
            ], [], [], [], []);
            using var body = System.Text.Json.JsonDocument.Parse(await op.GetStringAsync($"/api/endpoints/{id}/detail"));
            var rows = body.RootElement.GetProperty("processes");
            rows[0].GetProperty("pid").GetInt32().Should().Be(1234);
            rows[0].GetProperty("owner").GetString().Should().Be(@"TEST\owner");
            rows[0].GetProperty("cpuPercent").GetDouble().Should().Be(12.5);
            rows[0].GetProperty("workingSetBytes").GetInt64().Should().Be(4096);
            rows[1].GetProperty("cpuPercent").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
            rows[1].GetProperty("owner").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        }
        finally { FakeCollector.DetailOverride = null; }
    }

    [Fact]
    public async Task Endpoint_detail_is_collected_on_demand()
    {
        var id = await factory.SeedEndpointAsync();
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var before = FakeServiceCollector.Calls;
        (await viewer.GetAsync($"/api/endpoints/{id}/detail")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "live WMI collection is Operator and above");
        (await op.GetAsync($"/api/endpoints/{id}/detail")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await op.GetAsync($"/api/endpoints/{Guid.NewGuid()}/detail")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Dashboard_counts_endpoints()
    {
        await factory.SeedEndpointAsync();
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var dashboard = (await viewer.GetFromJsonAsync<DashboardDto>("/api/dashboard"))!;
        dashboard.Total.Should().BeGreaterThan(0);
        dashboard.Total.Should().Be(dashboard.Online + dashboard.Offline);
    }

    [Fact]
    public async Task Settings_are_admin_only_validated_and_audited()
    {
        var adminSid = TestIdentities.NewSid();
        var admin = await factory.UserClientAsync(adminSid, Role.Admin);
        var operatorClient = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

        (await operatorClient.GetAsync("/api/settings")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PutAsJsonAsync("/api/settings", new PlatformSettings { PollIntervalSeconds = 5 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var ok = await admin.PutAsJsonAsync("/api/settings", new PlatformSettings { PollIntervalSeconds = 120, CpuAlertPercent = 80 });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Query(db => db.AuditEntries.Any(a => a.ActorSid == adminSid && a.Action == "settings.update")).Should().BeTrue();
    }
}
