using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public sealed class BulkUpdateTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private async Task<GroupDto> NewGroupAsync(HttpClient admin) =>
        (await (await admin.PostAsJsonAsync("/api/groups", new { name = "G " + Guid.NewGuid().ToString("N")[..8], description = "" })).Content.ReadFromJsonAsync<GroupDto>())!;

    [Fact]
    public async Task Selected_workstations_can_be_added_to_and_removed_from_groups_in_one_step()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var a = await factory.SeedEndpointAsync();
        var b = await factory.SeedEndpointAsync();
        var g1 = await NewGroupAsync(admin);
        var g2 = await NewGroupAsync(admin);
        await admin.PutAsJsonAsync($"/api/groups/{g2.Id}/members", new { endpointIds = new[] { a, b } });

        var response = await admin.PostAsJsonAsync("/api/endpoints/bulk-groups",
            new { ids = new[] { a, b }, addGroupIds = new[] { g1.Id }, removeGroupIds = new[] { g2.Id } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Query(db => db.GroupMembers.Where(m => m.GroupId == g1.Id).Select(m => m.EndpointId).ToList()).Should().BeEquivalentTo([a, b]);
        factory.Query(db => db.GroupMembers.Count(m => m.GroupId == g2.Id)).Should().Be(0);
        factory.Query(db => db.AuditEntries.Any(x => x.Action == "endpoint.bulk_groups")).Should().BeTrue();
    }

    [Fact]
    public async Task Bulk_group_updates_are_admin_only_and_validated()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var id = await factory.SeedEndpointAsync();
        var g = await NewGroupAsync(admin);

        (await op.PostAsJsonAsync("/api/endpoints/bulk-groups", new { ids = new[] { id }, addGroupIds = new[] { g.Id } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync("/api/endpoints/bulk-groups", new { ids = new[] { id } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/endpoints/bulk-groups", new { ids = new[] { id }, addGroupIds = new[] { g.Id }, removeGroupIds = new[] { g.Id } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/endpoints/bulk-groups", new { ids = new[] { id }, addGroupIds = new[] { Guid.NewGuid() } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

public sealed class InventoryFieldTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task A_pcs_drive_health_is_its_weakest_drive()
    {
        var id = await factory.SeedEndpointAsync(online: true);
        using (var scope = factory.Services.CreateScope())
        {
            var disks = scope.ServiceProvider.GetRequiredService<IDiskHealthService>();
            DiskRaw Drive(string key, int life) => new(key, key, key, "SSD", "SATA", 1, "Healthy", false, 30, null, 100, null, life, null, null, null, null, null, null, []);
            await disks.StoreAsync(id, [Drive("A", 100), Drive("B", 96)]);
        }
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        var e = (await viewer.GetFromJsonAsync<EndpointSummaryDto>($"/api/endpoints/{id}"))!;
        e.DiskHealthPercent.Should().Be(96);
        e.DiskHealth.Should().Be("OK");
    }

    [Fact]
    public async Task Importing_from_ad_keeps_each_computers_organizational_unit()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);

        var response = await admin.PostAsJsonAsync("/api/endpoints/import-ad", new { prefix = "PC-AD" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = (await admin.GetFromJsonAsync<List<EndpointSummaryDto>>("/api/endpoints?search=PC-AD-1"))!;
        list.Should().ContainSingle(e => e.Hostname == "PC-AD-1" && e.OrganizationalUnit == "Branch A/Workstations");
    }

    [Fact]
    public async Task Manufacturer_model_and_ou_are_returned_with_the_workstation()
    {
        var id = await factory.SeedEndpointAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            var e = db.Endpoints.Single(x => x.Id == id);
            e.Manufacturer = "Dell Inc.";
            e.Model = "Vostro 3030";
            e.OrganizationalUnit = "Head Office/IT";
            await db.SaveChangesAsync();
        }
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        var dto = (await viewer.GetFromJsonAsync<EndpointSummaryDto>($"/api/endpoints/{id}"))!;
        (dto.Manufacturer, dto.Model, dto.OrganizationalUnit).Should().Be(("Dell Inc.", "Vostro 3030", "Head Office/IT"));
    }
}

public sealed class PinnedServerTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task The_nexora_server_is_always_in_the_inventory_and_listed_first()
    {
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        // Names that sort after the server's, and far more rows than any page would hold.
        for (var i = 0; i < 30; i++) await factory.SeedEndpointAsync("AAA");

        var list = (await viewer.GetFromJsonAsync<List<EndpointSummaryDto>>("/api/endpoints"))!;

        list[0].IsPinned.Should().BeTrue();
        list[0].Hostname.Should().BeEquivalentTo(Environment.MachineName);
    }

    [Fact]
    public async Task The_pinned_server_ignores_sort_order_by_display_name()
    {
        // The reported bug: a display name that sorts last used to push the server out of a capped list.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            db.Endpoints.Single(e => e.IsPinned).DisplayName = "Zzz last by name";
            await db.SaveChangesAsync();
        }
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        (await viewer.GetFromJsonAsync<List<EndpointSummaryDto>>("/api/endpoints"))![0].IsPinned.Should().BeTrue();
        EndpointFilters.ListLimit.Should().BeGreaterThanOrEqualTo(5000);
    }

    [Fact]
    public async Task The_pinned_server_cannot_be_removed_one_by_one_in_bulk_or_by_delete_all()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var pinned = factory.Query(db => db.Endpoints.Single(e => e.IsPinned).Id);

        (await admin.DeleteAsync($"/api/endpoints/{pinned}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await admin.PostAsJsonAsync("/api/endpoints/remove", new { ids = new[] { pinned }, all = false });
        await DeleteAll.RunAsync(admin);

        factory.Query(db => db.Endpoints.Any(e => e.Id == pinned && e.IsPinned)).Should().BeTrue();
    }

    [Fact]
    public async Task The_server_is_re_added_at_startup_if_it_was_deleted_directly()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            db.Endpoints.RemoveRange(db.Endpoints.Where(e => e.IsPinned));
            await db.SaveChangesAsync();
        }
        factory.Query(db => db.Endpoints.Any(e => e.IsPinned)).Should().BeFalse();

        await Nexora.Infrastructure.DependencyInjection.EnsureServerEndpointAsync(factory.Services);

        factory.Query(db => db.Endpoints.Count(e => e.IsPinned && e.Hostname == Environment.MachineName)).Should().Be(1);
        await Nexora.Infrastructure.DependencyInjection.EnsureServerEndpointAsync(factory.Services); // idempotent
        factory.Query(db => db.Endpoints.Count(e => e.IsPinned)).Should().Be(1);
    }

    [Fact]
    public async Task Profile_size_measuring_is_a_low_risk_operator_action_that_needs_no_confirmation()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var catalog = (await op.GetFromJsonAsync<List<Nexora.Application.Jobs.CatalogEntry>>("/api/jobs/catalog"))!;
        var entry = catalog.Single(c => c.Code == "profile.sizes");
        (entry.Risk, entry.Implemented, entry.Allowed, entry.NeedsConfirmation).Should().Be(("Low", true, true, false));

        var created = await op.PostAsJsonAsync("/api/jobs", new { actionCode = "profile.sizes", endpointIds = new[] { ep }, parameters = new { sid = "S-1-5-21-1-2-3-4001" } });
        created.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await op.PostAsJsonAsync("/api/jobs", new { actionCode = "profile.sizes", endpointIds = new[] { ep }, parameters = new { sid = "S-1-5-18'; --" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
