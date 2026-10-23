using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>SSD / HDD split, system drive and RAM size on the list and detail APIs, from what is already stored.</summary>
public sealed class DriveHealthSplitTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private static DiskRaw Drive(string key, string model, string media, string bus, int? health, long size = 512_000_000_000) =>
        new(key, model, key, media, bus, size, "Healthy", false, 38, 70, 12000, health is null ? null : 3, null, 0, 0, 0, 0, 0, null, []);

    private async Task<Guid> SeedBoxAsync(string name, params DiskRaw[] drives)
    {
        var id = await factory.SeedEndpointAsync(name, online: true);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        var e = db.Endpoints.Single(x => x.Id == id);
        e.RamPercent = 50; e.RamTotalBytes = 16L * 1024 * 1024 * 1024;
        e.VolumesJson = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new VolumeDto("D:", "NTFS", 1_000_000_000_000, 900_000_000_000), new VolumeDto("C:", "NTFS", 500_000_000_000, 125_000_000_000),
        });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IDiskHealthService>().StoreAsync(id, drives);
        return id;
    }

    [Fact]
    public async Task A_mixed_box_reports_the_worst_SSD_and_HDD_separately_with_the_drive_types()
    {
        var id = await SeedBoxAsync("PC-MIX",
            Drive("WFL827JA", "ST2000DM008-2UB102", "HDD", "SATA", health: null),
            Drive("MICRON1", "Micron_2450_MTFDKBA512TFK", "NVMe", "NVMe", health: null));
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        var disks = (await viewer.GetFromJsonAsync<List<DiskHealthDto>>($"/api/endpoints/{id}/disks"))!;
        disks.Single(d => d.Model.StartsWith("ST2000")).Type.Should().Be("HDD");
        disks.Single(d => d.Model.StartsWith("Micron")).Type.Should().Be("SSD");

        var row = (await viewer.GetFromJsonAsync<EndpointSummaryDto>($"/api/endpoints/{id}"))!;
        row.SsdHealthPercent.Should().NotBeNull();
        row.HddHealthPercent.Should().NotBeNull();
        row.DiskHealthPercent.Should().Be(Math.Min(row.SsdHealthPercent!.Value, row.HddHealthPercent!.Value), "the old overall figure is still the weakest drive");
    }

    [Fact]
    public async Task An_SSD_only_box_has_no_HDD_value_and_the_volumes_and_RAM_size_come_through()
    {
        var id = await SeedBoxAsync("PC-SSD", Drive("S1", "CT500BX500SSD1", "SSD", "SATA", health: 1));
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        var row = (await viewer.GetFromJsonAsync<EndpointSummaryDto>($"/api/endpoints/{id}"))!;

        row.SsdHealthPercent.Should().NotBeNull();
        row.HddHealthPercent.Should().BeNull();
        row.RamTotalBytes.Should().Be(16L * 1024 * 1024 * 1024);
        row.Volumes.Should().HaveCount(2);
        SystemVolume.Of(row.Volumes)!.Letter.Should().Be("C:");
    }

    [Fact]
    public async Task Every_role_can_read_the_thresholds_the_screens_colour_by()
    {
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var t = (await viewer.GetFromJsonAsync<ThresholdsDto>("/api/thresholds"))!;

        t.CpuAlertPercent.Should().BeInRange(1, 100);
        (t.HealthOkFrom, t.HealthCriticalBelow).Should().Be((DiskStatus.OkFrom, DiskStatus.CriticalBelow));
        (await factory.ClientFor(null).GetAsync("/api/thresholds")).StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }
}
