using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>
/// N3: two drives of one PC with the same key used to collide on the unique index, which threw away the drive data (and its push) of that PC on
/// every poll cycle. Now they are told apart, a store is an upsert, and a scan that still fails says so on the PC's page.
/// </summary>
public sealed class DriveScanStoreTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private static DiskRaw Raw(string key, string model, string media = "SSD", int? life = 90) =>
        new(key, model, key, media, "SATA", 500_000_000_000, "Healthy", false, 35, null, 1000, 0, life, 0, null, 0, 0, 0, 0, []);

    private async Task StoreAsync(Guid id, params DiskRaw[] drives)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDiskHealthService>().StoreAsync(id, drives);
    }

    // ---------- pure

    [Fact]
    public void Drives_that_report_the_same_key_are_told_apart_by_a_stable_suffix()
    {
        var disks = new[] { Raw("SER", "first"), Raw("OTHER", "x"), Raw("SER", "second"), Raw("SER", "third") };

        var keys = DiskHealthService.WithUniqueKeys(disks).Select(d => d.Key).ToList();

        keys.Should().Equal("SER", "OTHER", "SER#2", "SER#3");
        DiskHealthService.WithUniqueKeys(disks).Select(d => d.Key).Should().Equal(keys, "the same scan gives the same keys again");
        DiskHealthService.WithUniqueKeys([]).Should().BeEmpty();
    }

    private sealed class FakePg(string state) : Exception("duplicate") { public string SqlState { get; } = state; }

    [Fact]
    public void A_unique_violation_is_recognised_through_the_exception_chain_and_nothing_else_is()
    {
        DiskHealthService.IsUniqueViolation(new Exception("save failed", new FakePg("23505"))).Should().BeTrue();
        DiskHealthService.IsUniqueViolation(new FakePg("23505")).Should().BeTrue();
        DiskHealthService.IsUniqueViolation(new Exception("save failed", new FakePg("23503"))).Should().BeFalse();
        DiskHealthService.IsUniqueViolation(new InvalidOperationException("x")).Should().BeFalse();
    }

    // ---------- stored

    [Fact]
    public async Task A_scan_with_two_drives_of_the_same_serial_stores_both_and_keeps_doing_so_on_every_rescan()
    {
        var id = await factory.SeedEndpointAsync("PC-DUP", online: true);

        await StoreAsync(id, Raw("SAMESER", "Drive A"), Raw("SAMESER", "Drive B", "HDD"));
        await StoreAsync(id, Raw("SAMESER", "Drive A"), Raw("SAMESER", "Drive B", "HDD"));

        var rows = factory.Query(db => db.DiskHealth.Where(s => s.EndpointId == id).OrderBy(s => s.DiskKey).Select(s => new { s.DiskKey, s.Model }).ToList());
        rows.Should().HaveCount(2, "the second scan updates, it does not add");
        rows.Select(r => r.DiskKey).Should().Equal("SAMESER", "SAMESER#2");
        rows.Select(r => r.Model).Should().Equal("Drive A", "Drive B");
        factory.Query(db => db.DiskTemps.Count(t => t.EndpointId == id)).Should().Be(4);
    }

    [Fact]
    public async Task A_removed_drive_drops_out_and_a_successful_scan_clears_an_earlier_failure()
    {
        var id = await factory.SeedEndpointAsync("PC-DROP", online: true);
        await StoreAsync(id, Raw("A", "a"), Raw("B", "b"));
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IDiskHealthService>().MarkScanFailedAsync(id, "RPC server unavailable");

        await StoreAsync(id, Raw("A", "a"));

        factory.Query(db => db.DiskHealth.Count(s => s.EndpointId == id)).Should().Be(1);
        factory.Query(db => db.Endpoints.Single(e => e.Id == id).DiskScanError).Should().BeNull();
    }

    [Fact]
    public async Task A_failed_scan_is_visible_on_the_PC_and_is_not_repeated_on_every_cycle()
    {
        var id = await factory.SeedEndpointAsync("PC-FAIL", online: true);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var before = DateTime.UtcNow.AddSeconds(-2);

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IDiskHealthService>().MarkScanFailedAsync(id, new string('x', 400));

        var row = (await viewer.GetFromJsonAsync<EndpointSummaryDto>($"/api/endpoints/{id}"))!;
        row.DiskScanError.Should().HaveLength(300, "the reason is bounded");
        factory.Query(db => db.Endpoints.Single(e => e.Id == id).LastDiskPollUtc).Should().BeAfter(before, "the next attempt waits the normal interval");
    }
}

public sealed class NvmeTemperatureTests
{
    [Fact]
    public void Nvme_composite_temperature_converts_from_kelvin()
    {
        Nexora.Infrastructure.Wmi.NvmeSmart.KelvinToCelsius(320).Should().Be(47);
        Nexora.Infrastructure.Wmi.NvmeSmart.KelvinToCelsius(0).Should().Be(0, "0 means the drive reports none");
        var log = new byte[512]; log[1] = 0x40; log[2] = 0x01; // 0x0140 = 320 K
        Nexora.Infrastructure.Wmi.NvmeSmart.Parse(log).TemperatureC.Should().Be(47);
    }
}
