using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public sealed class BulkRemoveTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task Selected_workstations_are_removed_with_their_history_and_the_rest_are_kept()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var a = await factory.SeedEndpointAsync();
        var b = await factory.SeedEndpointAsync();
        var keep = await factory.SeedEndpointAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            db.MetricSamples.Add(new MetricSample { EndpointId = a, TimestampUtc = DateTime.UtcNow, Cpu = 1 });
            await db.SaveChangesAsync();
        }

        var response = await admin.PostAsJsonAsync("/api/endpoints/remove", new { ids = new[] { a, b }, all = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RemovedResponse>())!.Removed.Should().Be(2);
        factory.Query(db => db.Endpoints.Any(e => e.Id == a || e.Id == b)).Should().BeFalse();
        factory.Query(db => db.Endpoints.Any(e => e.Id == keep)).Should().BeTrue();
        factory.Query(db => db.MetricSamples.Any(m => m.EndpointId == a)).Should().BeFalse();
        factory.Query(db => db.AuditEntries.Any(x => x.Action == "endpoint.remove_many")).Should().BeTrue();
    }

    [Fact]
    public async Task Only_admins_can_bulk_remove_and_the_request_must_pick_exactly_one_mode()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var id = await factory.SeedEndpointAsync();

        (await op.PostAsJsonAsync("/api/endpoints/remove", new { ids = new[] { id }, all = false })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync("/api/endpoints/remove", new { ids = new[] { id }, all = true })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/endpoints/remove", new { ids = Array.Empty<Guid>(), all = false })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Query(db => db.Endpoints.Any(e => e.Id == id)).Should().BeTrue();
    }

    private sealed record RemovedResponse(int Removed);
}

public sealed class RemoveAllTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task Remove_all_deletes_every_workstation_and_group_membership_but_keeps_groups_and_audit()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var e1 = await factory.SeedEndpointAsync();
        await factory.SeedEndpointAsync();
        var group = (await (await admin.PostAsJsonAsync("/api/groups", new { name = "Keep " + Guid.NewGuid().ToString("N")[..6], description = "" })).Content.ReadFromJsonAsync<GroupDto>())!;
        await admin.PutAsJsonAsync($"/api/groups/{group.Id}/members", new { endpointIds = new[] { e1 } });

        var response = await DeleteAll.RunAsync(admin);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Query(db => db.Endpoints.Count(e => !e.IsPinned)).Should().Be(0);
        factory.Query(db => db.Endpoints.Any(e => e.IsPinned && e.Hostname == Environment.MachineName)).Should().BeTrue("the Nexora server is never removed");
        factory.Query(db => db.GroupMembers.Count()).Should().Be(0);
        factory.Query(db => db.Groups.Any(g => g.Id == group.Id)).Should().BeTrue();
        factory.Query(db => db.AuditEntries.Any(a => a.Action == "endpoint.remove_all")).Should().BeTrue();
        (await admin.GetFromJsonAsync<List<EndpointSummaryDto>>("/api/endpoints"))!.Should().ContainSingle(e => e.IsPinned);
    }
}
