using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>GET /api/endpoints/{id}/overview: authorization, offline/stale snapshots, persistence across a restart, Fast Startup caveat, no leaked internals.</summary>
public sealed class EndpointOverviewTests : IClassFixture<NexoraFactory>, IDisposable
{
    private readonly NexoraFactory _factory;

    public EndpointOverviewTests(NexoraFactory factory)
    {
        _factory = factory;
        FakeHardwareCollector.Calls = 0;
        FakeHardwareCollector.Behavior = (_, _) => Task.FromResult(FakeHardwareCollector.Sample());
    }

    public void Dispose() => FakeHardwareCollector.Behavior = (_, _) => Task.FromResult(FakeHardwareCollector.Sample());

    private async Task SetAsync(Guid id, Action<ManagedEndpoint> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        change(await db.Endpoints.FirstAsync(e => e.Id == id));
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>());

    [Fact]
    public async Task Viewer_gets_the_overview_anonymous_is_rejected_unknown_ids_are_404_and_unapproved_users_are_forbidden()
    {
        var id = await _factory.SeedEndpointAsync(online: true);
        var viewer = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        (await viewer.GetAsync($"/api/endpoints/{id}/overview")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _factory.ClientFor(null).GetAsync($"/api/endpoints/{id}/overview")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await viewer.GetAsync($"/api/endpoints/{Guid.NewGuid()}/overview")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var noRole = await _factory.UserClientAsync(TestIdentities.NewSid(), null);
        (await noRole.GetAsync($"/api/endpoints/{id}/overview")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Online_host_returns_inventory_live_sample_camelCase_and_no_serials()
    {
        var id = await _factory.SeedEndpointAsync(online: true);
        var sampled = DateTime.UtcNow.AddSeconds(-20);
        await SetAsync(id, e => { e.LastSeenUtc = sampled; e.CpuPercent = 12.5; e.RamPercent = 50; e.RamTotalBytes = 16_000_000_000; e.RamFreeBytes = 8_000_000_000; });
        var viewer = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        var r = await Json(await viewer.GetAsync($"/api/endpoints/{id}/overview"));

        r.GetProperty("online").GetBoolean().Should().BeTrue();
        r.GetProperty("stale").GetBoolean().Should().BeFalse();
        r.GetProperty("unavailableReason").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("collectedUtc").ValueKind.Should().Be(JsonValueKind.String);
        r.GetProperty("inventory").GetProperty("cpu").GetProperty("model").GetString().Should().Contain("i5-12500");
        r.GetProperty("inventory").GetProperty("board").GetProperty("serial").ValueKind.Should().Be(JsonValueKind.Null);
        var live = r.GetProperty("live");
        live.GetProperty("cpuLoadPercent").GetDouble().Should().Be(12.5);
        live.GetProperty("ramTotalBytes").GetInt64().Should().Be(16_000_000_000);
        live.GetProperty("ramFreeBytes").GetInt64().Should().Be(8_000_000_000);
        live.GetProperty("ramUsedBytes").GetInt64().Should().Be(8_000_000_000);
        live.GetProperty("ramPercentUsed").GetDouble().Should().Be(50);
        live.GetProperty("sampledUtc").GetDateTime().Should().BeCloseTo(sampled, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Offline_without_a_snapshot_is_200_with_a_generic_reason_and_is_not_queried()
    {
        var id = await _factory.SeedEndpointAsync(online: false);
        var viewer = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var before = FakeHardwareCollector.Calls;

        var res = await viewer.GetAsync($"/api/endpoints/{id}/overview");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var r = await Json(res);

        r.GetProperty("online").GetBoolean().Should().BeFalse();
        r.GetProperty("inventory").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("live").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("collectedUtc").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("unavailableReason").GetString().Should().NotBeNullOrWhiteSpace();
        FakeHardwareCollector.Calls.Should().Be(before);
    }

    [Fact]
    public async Task Offline_with_a_snapshot_returns_it_flagged_stale_without_querying_the_host()
    {
        var id = await _factory.SeedEndpointAsync(online: true);
        var viewer = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        (await viewer.GetAsync($"/api/endpoints/{id}/overview")).EnsureSuccessStatusCode();   // collects and stores
        await SetAsync(id, e => e.Online = false);
        var before = FakeHardwareCollector.Calls;

        var r = await Json(await viewer.GetAsync($"/api/endpoints/{id}/overview"));

        r.GetProperty("online").GetBoolean().Should().BeFalse();
        r.GetProperty("stale").GetBoolean().Should().BeTrue();
        r.GetProperty("inventory").GetProperty("cpu").GetProperty("model").GetString().Should().NotBeNull();
        r.GetProperty("collectedUtc").ValueKind.Should().Be(JsonValueKind.String);
        r.GetProperty("unavailableReason").GetString().Should().NotBeNullOrWhiteSpace();
        r.GetProperty("live").ValueKind.Should().Be(JsonValueKind.Null);
        FakeHardwareCollector.Calls.Should().Be(before);
    }

    [Fact]
    public async Task A_failing_host_never_leaks_exception_text_and_is_not_a_500()
    {
        var id = await _factory.SeedEndpointAsync(online: true);
        var viewer = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        FakeHardwareCollector.Behavior = (_, _) => throw new InvalidOperationException(FakeCollector.SecretText);

        var res = await viewer.GetAsync($"/api/endpoints/{id}/overview");
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotContain("internal-share").And.NotContain("10.99.99.99").And.NotContain("secret-detail").And.NotContain("InvalidOperationException");
        var r = JsonDocument.Parse(body).RootElement;
        r.GetProperty("inventory").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("unavailableReason").GetString().Should().NotBeNullOrWhiteSpace();
    }

    private sealed class TestClock(DateTime start) : ISystemClock
    {
        public DateTime UtcNow { get; set; } = start;
    }

    [Fact]
    public async Task A_failed_refresh_after_a_good_one_keeps_the_snapshot_flagged_stale_with_a_generic_reason()
    {
        var id = await _factory.SeedEndpointAsync(online: true);
        var clock = new TestClock(DateTime.UtcNow);
        using var scope = _factory.Services.CreateScope();
        var svc = NewService(scope, new HardwareInventoryCache(), clock);
        var good = (await svc.GetOverviewAsync(id)).Value!;
        good.Stale.Should().BeFalse();
        good.UnavailableReason.Should().BeNull();

        clock.UtcNow = clock.UtcNow.AddHours(3);   // past the TTL and the minimum refresh window
        FakeHardwareCollector.Behavior = (_, _) => throw new InvalidOperationException(FakeCollector.SecretText);
        var after = (await svc.GetOverviewAsync(id)).Value!;

        after.Stale.Should().BeTrue();
        after.Inventory.Should().NotBeNull();
        after.CollectedUtc.Should().Be(good.CollectedUtc);
        after.UnavailableReason.Should().NotBeNullOrWhiteSpace().And.NotContain("10.99.99.99").And.NotContain("secret-detail");
    }

    [Fact]
    public async Task A_viewer_cannot_trigger_unbounded_collection()
    {
        var id = await _factory.SeedEndpointAsync(online: true);
        var viewer = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var before = FakeHardwareCollector.Calls;
        for (var i = 0; i < 6; i++) (await viewer.GetAsync($"/api/endpoints/{id}/overview")).EnsureSuccessStatusCode();
        FakeHardwareCollector.Calls.Should().Be(before + 1);
    }

    [Theory]
    [InlineData(true, "fullBoot")]
    [InlineData(false, "boot")]
    [InlineData(null, "boot")]
    public async Task Fast_startup_sets_the_uptime_basis_caveat(bool? fastStartup, string basis)
    {
        var id = await _factory.SeedEndpointAsync(online: true);
        var viewer = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        FakeHardwareCollector.Behavior = (_, _) =>
        {
            var s = FakeHardwareCollector.Sample();
            return Task.FromResult(s with { Os = s.Os! with { FastStartupEnabled = fastStartup } });
        };

        var os = (await Json(await viewer.GetAsync($"/api/endpoints/{id}/overview"))).GetProperty("inventory").GetProperty("os");

        os.GetProperty("uptimeBasis").GetString().Should().Be(basis);
        os.GetProperty("fastStartupEnabled").ValueKind.Should().Be(fastStartup is null ? JsonValueKind.Null : fastStartup.Value ? JsonValueKind.True : JsonValueKind.False);
        os.GetProperty("bootTimeUtc").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task The_stored_snapshot_survives_a_service_restart()
    {
        var id = await _factory.SeedEndpointAsync(online: true);
        DateTime stored;
        using (var scope = _factory.Services.CreateScope())
        {
            var first = NewService(scope, new HardwareInventoryCache());   // "process 1"
            var collected = await first.GetAsync(id, false, true);
            collected.Ok.Should().BeTrue();
            stored = collected.Value!.CollectedUtc!.Value;
        }
        await SetAsync(id, e => e.Online = false);
        var before = FakeHardwareCollector.Calls;

        using var scope2 = _factory.Services.CreateScope();
        var second = NewService(scope2, new HardwareInventoryCache());       // "process 2": empty cache, same database
        var overview = (await second.GetOverviewAsync(id)).Value!;

        overview.Inventory.Should().NotBeNull();
        overview.Inventory!.Cpu!.Model.Should().Be("Intel(R) Core(TM) i5-12500");
        overview.Stale.Should().BeTrue();
        overview.Online.Should().BeFalse();
        overview.CollectedUtc.Should().BeCloseTo(stored, TimeSpan.FromMilliseconds(1));
        FakeHardwareCollector.Calls.Should().Be(before);
        _factory.Query(db => db.EndpointInventories.Count(x => x.EndpointId == id)).Should().Be(1);
    }

    [Fact]
    public async Task A_second_successful_collection_updates_the_single_stored_row()
    {
        var id = await _factory.SeedEndpointAsync(online: true);
        using var scope = _factory.Services.CreateScope();
        var svc = NewService(scope, new HardwareInventoryCache());
        (await svc.GetAsync(id, false, true)).Ok.Should().BeTrue();
        using var scope2 = _factory.Services.CreateScope();
        var other = NewService(scope2, new HardwareInventoryCache());
        FakeHardwareCollector.Behavior = (_, _) => Task.FromResult(FakeHardwareCollector.Sample() with { Cpu = new CpuInventory("Intel", "Changed CPU", 8, 16, "x64", 3600) });
        // the new instance loads the stored copy (fresh) so it does not collect; force it
        (await other.GetAsync(id, true, true)).Value!.Cpu!.Model.Should().Be("Changed CPU");
        _factory.Query(db => db.EndpointInventories.Where(x => x.EndpointId == id).Select(x => x.Json).ToList()).Should().ContainSingle().Which.Should().Contain("Changed CPU");
    }

    private HardwareInventoryService NewService(IServiceScope scope, HardwareInventoryCache cache, ISystemClock? clock = null)
    {
        var sp = scope.ServiceProvider;
        return new HardwareInventoryService(sp.GetRequiredService<INexoraDb>(), sp.GetRequiredService<IHardwareInventoryCollector>(), cache,
            sp.GetRequiredService<IOptions<NexoraOptions>>(), clock ?? sp.GetRequiredService<ISystemClock>(), NullLogger<HardwareInventoryService>.Instance);
    }
}
