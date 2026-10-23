using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public sealed class WorkstationTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private static object One(string? name, string address, params Guid[] groups) =>
        new { items = new[] { new { name, address } }, groupIds = groups };

    [Fact]
    public async Task A_workstation_can_be_added_by_ip_with_a_friendly_name()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var ip = $"10.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}";

        var response = await admin.PostAsJsonAsync("/api/endpoints", One("Cashier desk 3", ip));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var added = (await response.Content.ReadFromJsonAsync<List<EndpointSummaryDto>>())!.Single();
        added.Name.Should().Be("Cashier desk 3");
        added.Hostname.Should().Be(ip, "the address is what the server connects to");
    }

    [Fact]
    public async Task A_workstation_can_be_added_by_hostname_and_the_name_defaults_to_it()
    {
        var host = "PC-" + Guid.NewGuid().ToString("N")[..8];
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);

        var added = (await (await admin.PostAsJsonAsync("/api/endpoints", One(null, host))).Content.ReadFromJsonAsync<List<EndpointSummaryDto>>())!.Single();
        added.Name.Should().Be(host);
    }

    [Theory]
    [InlineData("10.0.0.1; calc")]
    [InlineData("bad host")]
    [InlineData("http://x")]
    public async Task Bad_addresses_and_names_are_rejected(string address)
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        (await admin.PostAsJsonAsync("/api/endpoints", One("ok", address))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/endpoints", One("<script>", "PC-OK-1"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Duplicate_addresses_are_not_added_twice()
    {
        var host = "DUP-" + Guid.NewGuid().ToString("N")[..8];
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        await admin.PostAsJsonAsync("/api/endpoints", One("A", host));
        var again = (await (await admin.PostAsJsonAsync("/api/endpoints", One("B", host.ToLower()))).Content.ReadFromJsonAsync<List<EndpointSummaryDto>>())!;
        again.Should().BeEmpty();
    }

    [Fact]
    public async Task Admin_can_rename_readdress_and_regroup_a_workstation()
    {
        var host = "UPD-" + Guid.NewGuid().ToString("N")[..8];
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var group = (await (await admin.PostAsJsonAsync("/api/groups", new { name = "G-" + Guid.NewGuid().ToString("N")[..6], description = "" })).Content.ReadFromJsonAsync<GroupDto>())!;
        var e = (await (await admin.PostAsJsonAsync("/api/endpoints", One("Old", host))).Content.ReadFromJsonAsync<List<EndpointSummaryDto>>())!.Single();

        var response = await admin.PutAsJsonAsync($"/api/endpoints/{e.Id}", new { name = "New name", address = "10.9.8.7", groupIds = new[] { group.Id } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await response.Content.ReadFromJsonAsync<EndpointSummaryDto>())!;
        (updated.Name, updated.Hostname).Should().Be(("New name", "10.9.8.7"));
        updated.GroupIds.Should().Equal(group.Id);
        factory.Query(db => db.AuditEntries.Any(a => a.Action == "endpoint.update")).Should().BeTrue();
    }

    [Fact]
    public async Task Only_admins_manage_workstations()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var id = await factory.SeedEndpointAsync();
        (await op.PostAsJsonAsync("/api/endpoints", One("x", "PC-X-1"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await op.PutAsJsonAsync($"/api/endpoints/{id}", new { name = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await op.DeleteAsync($"/api/endpoints/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Removing_a_workstation_removes_its_history_and_memberships()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var id = await factory.SeedEndpointAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            db.MetricSamples.Add(new MetricSample { EndpointId = id, TimestampUtc = DateTime.UtcNow, Cpu = 1 });
            await db.SaveChangesAsync();
        }

        (await admin.DeleteAsync($"/api/endpoints/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        factory.Query(db => db.MetricSamples.Count(m => m.EndpointId == id)).Should().Be(0);
    }
}

public sealed class GroupTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private static string UniqueName() => "Group " + Guid.NewGuid().ToString("N")[..8];

    private async Task<GroupDto> CreateGroupAsync(HttpClient admin, params Guid[] members)
    {
        var g = (await (await admin.PostAsJsonAsync("/api/groups", new { name = UniqueName(), description = "d" })).Content.ReadFromJsonAsync<GroupDto>())!;
        if (members.Length > 0)
            g = (await (await admin.PutAsJsonAsync($"/api/groups/{g.Id}/members", new { endpointIds = members })).Content.ReadFromJsonAsync<GroupDto>())!;
        return g;
    }

    [Fact]
    public async Task Admins_manage_groups_and_everyone_with_a_role_can_list_them()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var name = UniqueName();

        (await viewer.PostAsJsonAsync("/api/groups", new { name, description = "" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var created = await admin.PostAsJsonAsync("/api/groups", new { name, description = "Cashiers" });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PostAsJsonAsync("/api/groups", new { name = name.ToUpper(), description = "" })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await viewer.GetFromJsonAsync<List<GroupDto>>("/api/groups"))!.Should().Contain(g => g.Name == name);
    }

    [Fact]
    public async Task Membership_is_replaced_and_audited()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var a = await factory.SeedEndpointAsync();
        var b = await factory.SeedEndpointAsync();
        var g = await CreateGroupAsync(admin, a, b);
        g.MemberIds.Should().BeEquivalentTo([a, b]);

        var shrunk = (await (await admin.PutAsJsonAsync($"/api/groups/{g.Id}/members", new { endpointIds = new[] { a } })).Content.ReadFromJsonAsync<GroupDto>())!;
        shrunk.MemberIds.Should().Equal(a);

        (await admin.PutAsJsonAsync($"/api/groups/{g.Id}/members", new { endpointIds = new[] { Guid.NewGuid() } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Query(db => db.AuditEntries.Count(x => x.Action == "group.members" && x.TargetId == g.Id.ToString())).Should().Be(2);
    }

    [Fact]
    public async Task The_endpoint_list_can_be_filtered_by_group()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var inGroup = await factory.SeedEndpointAsync();
        var outside = await factory.SeedEndpointAsync();
        var g = await CreateGroupAsync(admin, inGroup);

        var list = (await admin.GetFromJsonAsync<List<EndpointSummaryDto>>($"/api/endpoints?groupId={g.Id}"))!;
        list.Select(e => e.Id).Should().Equal(inGroup);
        list.Should().NotContain(e => e.Id == outside);
        list.Single().GroupIds.Should().Equal(g.Id);
    }

    [Fact]
    public async Task Deleting_a_group_keeps_the_workstations()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var e = await factory.SeedEndpointAsync();
        var g = await CreateGroupAsync(admin, e);

        (await admin.DeleteAsync($"/api/groups/{g.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        factory.Query(db => db.Endpoints.Any(x => x.Id == e)).Should().BeTrue();
        factory.Query(db => db.GroupMembers.Any(m => m.GroupId == g.Id)).Should().BeFalse();
    }

}

public sealed class MonitoringDataTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task Endpoint_history_returns_bucketed_points_for_the_charts()
    {
        var id = await factory.SeedEndpointAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            for (var i = 0; i < 90; i++)
                db.MetricSamples.Add(new MetricSample { EndpointId = id, TimestampUtc = DateTime.UtcNow.AddMinutes(-i), Cpu = 20 + i % 10, Ram = 50, Disk = 40 });
            db.MetricSamples.Add(new MetricSample { EndpointId = id, TimestampUtc = DateTime.UtcNow.AddDays(-3), Cpu = 99, Ram = 99 });
            await db.SaveChangesAsync();
        }
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        var hour = (await viewer.GetFromJsonAsync<List<MetricPoint>>($"/api/endpoints/{id}/metrics?range=1h"))!;
        hour.Should().NotBeEmpty();
        hour.Should().OnlyContain(p => p.Cpu < 40, "the 3-day-old spike is outside the range");
        hour.Select(p => p.TimeUtc).Should().BeInAscendingOrder();

        var week = (await viewer.GetFromJsonAsync<List<MetricPoint>>($"/api/endpoints/{id}/metrics?range=7d"))!;
        week.Should().Contain(p => p.Cpu > 90);

        (await viewer.GetAsync($"/api/endpoints/{Guid.NewGuid()}/metrics")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Fleet_history_is_available_to_viewers()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            db.FleetSamples.Add(new FleetSample { TimestampUtc = DateTime.UtcNow.AddMinutes(-5), Total = 10, Online = 8, AvgCpu = 30, AvgRam = 60 });
            await db.SaveChangesAsync();
        }
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var points = (await viewer.GetFromJsonAsync<List<FleetPoint>>("/api/dashboard/history?range=1h"))!;
        points.Should().NotBeEmpty();
        points[0].Online.Should().Be(8);
    }

    [Fact]
    public async Task Disk_health_is_read_stored_and_served_with_temperature_history()
    {
        var id = await factory.SeedEndpointAsync(online: true);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        (await viewer.PostAsync($"/api/endpoints/{id}/disks/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var refreshed = await op.PostAsync($"/api/endpoints/{id}/disks/refresh", null);
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);

        var disks = (await viewer.GetFromJsonAsync<List<DiskHealthDto>>($"/api/endpoints/{id}/disks?range=24h"))!;
        var d = disks.Should().ContainSingle().Subject;
        d.Model.Should().Be("Test SSD 512GB");
        d.Status.Should().Be("OK");
        d.HealthPercent.Should().Be(97);
        d.TemperatureC.Should().Be(41);
        d.TemperatureHistory.Should().NotBeEmpty();

        // A second read updates the same drive instead of duplicating it.
        await op.PostAsync($"/api/endpoints/{id}/disks/refresh", null);
        factory.Query(db => db.DiskHealth.Count(x => x.EndpointId == id)).Should().Be(1);
    }

    [Fact]
    public async Task A_failing_drive_raises_a_disk_health_alert_on_the_dashboard()
    {
        var id = await factory.SeedEndpointAsync(online: true);
        using (var scope = factory.Services.CreateScope())
        {
            var disks = scope.ServiceProvider.GetRequiredService<IDiskHealthService>();
            await disks.StoreAsync(id, [new DiskRaw("BAD1", "Old HDD", "BAD1", "HDD", "SATA", 1_000_000_000_000, "Healthy", true, 40, null, 50000, null, null, null, null, 200, 8, null, null, [])]);
        }
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        var dashboard = (await viewer.GetFromJsonAsync<DashboardDto>("/api/dashboard"))!;
        dashboard.DiskHealthAlerts.Should().BeGreaterThan(0);
        dashboard.AlertEndpoints.Should().Contain(e => e.Id == id && e.Alerts.Contains("diskhealth") && e.DiskHealth == "Critical");
    }
}

public sealed class ProfileTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private sealed record ListEnvelope(bool? Unavailable, string? Reason, List<ProfileRowDto> Items);

    [Fact]
    public async Task Profiles_are_listed_for_operators_only()
    {
        var id = await factory.SeedEndpointAsync(online: true);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        (await viewer.GetAsync($"/api/endpoints/{id}/profiles")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var envelope = (await op.GetFromJsonAsync<ListEnvelope>($"/api/endpoints/{id}/profiles"))!;
        envelope.Unavailable.Should().BeNull("a live read carries no unavailable flag");
        envelope.Items.Should().ContainSingle().Which.LocalPath.Should().Be(@"C:\Users\old.user");
    }
}
