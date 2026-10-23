using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>Drive temperature limits in Settings (Admin only, validated, audited), the thresholds endpoint, and the v4 drive DTO fields (ADR-0005).</summary>
public sealed class DriveTempSettingsApiTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private sealed class SettingsEnvelope { public PlatformSettings Settings { get; set; } = new(); }

    private static DiskRaw Nvme(string key, int temp, int? warn = null, int? crit = null) =>
        new(key, "Micron 2500", key, "NVMe", "NVMe", 512_000_000_000, "Healthy", false, temp, null, 2218, null, null, null, null, null, null, null, null, [],
            Nvme: new NvmeInfo(0, 4, 100, 5, 0), DriveTempWarnC: warn, DriveTempCritC: crit);

    private static DiskRaw Hdd(string key, int temp) =>
        new(key, "ST1000VX012", key, "HDD", "SATA", 1_000_000_000_000, "Healthy", false, temp, null, 7807, null, null, 0, null, 0, 0, 0, 1,
            [new SmartAttribute(5, "Reallocated sectors", 100, 100, 0), new SmartAttribute(199, "UDMA CRC errors", 200, 200, 1)]);

    private async Task<PlatformSettings> CurrentAsync(HttpClient admin) =>
        (await admin.GetFromJsonAsync<SettingsEnvelope>("/api/settings"))!.Settings;

    [Fact]
    public async Task Only_an_admin_can_change_the_drive_temperature_limits_and_everyone_signed_in_can_read_them()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var settings = await CurrentAsync(admin);
        settings.DiskTempHddWarnC = 45; settings.DiskTempHddCritC = 52;

        foreach (var role in new[] { Role.Viewer, Role.Operator })
        {
            var client = await factory.UserClientAsync(TestIdentities.NewSid(), role);
            (await client.PutAsJsonAsync("/api/settings", settings)).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} must not change settings");
        }
        (await factory.ClientFor(null).PutAsJsonAsync("/api/settings", settings)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        (await viewer.GetFromJsonAsync<ThresholdsDto>("/api/thresholds"))!.DiskTempHddWarnC.Should().Be(50, "the denied attempts changed nothing");

        var adminSid = TestIdentities.NewSid();
        var admin2 = await factory.UserClientAsync(adminSid, Role.Admin);
        try
        {
            (await admin2.PutAsJsonAsync("/api/settings", settings)).StatusCode.Should().Be(HttpStatusCode.OK);

            var t = (await viewer.GetFromJsonAsync<ThresholdsDto>("/api/thresholds"))!;
            (t.DiskTempHddWarnC, t.DiskTempHddCritC).Should().Be((45, 52));
            (t.DiskTempSsdWarnC, t.DiskTempSsdCritC, t.DiskTempNvmeWarnC, t.DiskTempNvmeCritC).Should().Be((60, 70, 70, 80));
            factory.Query(db => db.AuditEntries.Any(a => a.ActorSid == adminSid && a.Action == "settings.update" && a.NewValue!.Contains("\"DiskTempHddCritC\":52"))).Should().BeTrue("the change is audited with the new values");
        }
        finally
        {
            settings.DiskTempHddWarnC = 50; settings.DiskTempHddCritC = 55;
            await admin2.PutAsJsonAsync("/api/settings", settings);
        }
    }

    [Fact]
    public async Task A_save_that_omits_the_temperature_limits_keeps_the_stored_ones_and_the_defaults_are_numbers()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var settings = await CurrentAsync(admin);
        var raw = await viewer.GetStringAsync("/api/thresholds");
        var doc = System.Text.Json.JsonDocument.Parse(raw).RootElement;
        int[] expected = [50, 55, 60, 70, 70, 80];
        string[] names = ["diskTempHddWarnC", "diskTempHddCritC", "diskTempSsdWarnC", "diskTempSsdCritC", "diskTempNvmeWarnC", "diskTempNvmeCritC"];
        for (var i = 0; i < 6; i++)
        {
            doc.GetProperty(names[i]).ValueKind.Should().Be(System.Text.Json.JsonValueKind.Number);
            doc.GetProperty(names[i]).GetInt32().Should().Be(expected[i]);
        }
        try
        {
            settings.DiskTempHddWarnC = 45; settings.DiskTempHddCritC = 52;
            (await admin.PutAsJsonAsync("/api/settings", settings)).StatusCode.Should().Be(HttpStatusCode.OK);

            // an older client: a document without any diskTemp* member
            var legacy = new StringContent("{\"pollIntervalSeconds\":60,\"maxConcurrency\":32,\"cpuAlertPercent\":90,\"ramAlertPercent\":90,\"diskAlertPercent\":90,\"offlineAfterFailures\":2,\"metricRetentionDays\":7,\"diskPollMinutes\":10,\"jobParallelism\":10,\"protectedHosts\":[\"wks-111\"]}",
                System.Text.Encoding.UTF8, "application/json");
            (await admin.PutAsync("/api/settings", legacy)).StatusCode.Should().Be(HttpStatusCode.OK);

            var t = (await viewer.GetFromJsonAsync<ThresholdsDto>("/api/thresholds"))!;
            (t.DiskTempHddWarnC, t.DiskTempHddCritC).Should().Be((45, 52), "omitted limits keep their stored values");

            // explicit values are still validated
            settings.DiskTempHddWarnC = 60;
            (await admin.PutAsJsonAsync("/api/settings", settings)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            settings.DiskTempHddWarnC = 50; settings.DiskTempHddCritC = 55;
            await admin.PutAsJsonAsync("/api/settings", settings);
        }
    }

    [Theory]
    [InlineData(55, 55)]     // warn == crit
    [InlineData(60, 50)]     // warn > crit
    [InlineData(10, 55)]     // below 20
    [InlineData(50, 101)]    // above 100
    public async Task Invalid_temperature_limits_are_rejected_and_nothing_is_stored(int warn, int crit)
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var settings = await CurrentAsync(admin);
        settings.DiskTempSsdWarnC = warn; settings.DiskTempSsdCritC = crit;

        (await admin.PutAsJsonAsync("/api/settings", settings)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        (await viewer.GetFromJsonAsync<ThresholdsDto>("/api/thresholds"))!.DiskTempSsdWarnC.Should().Be(60);
    }

    [Fact]
    public async Task The_drive_list_carries_reasons_temperature_and_the_interface_warning()
    {
        var id = await factory.SeedEndpointAsync("PC-HDS-1", online: true);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IDiskHealthService>().StoreAsync(id, [Nvme("NV1", 36, warn: 85, crit: 90), Hdd("HD1", 51)]);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        var disks = (await viewer.GetFromJsonAsync<List<DiskHealthDto>>($"/api/endpoints/{id}/disks"))!;

        var nvme = disks.Single(d => d.DiskKey == "NV1");
        nvme.HealthPercent.Should().Be(96);
        nvme.Reasons!.Should().Contain(r => r.Code == "nvme-used" && r.Delta == -4);
        nvme.Temperature!.Should().Match<TemperatureInfo>(t => t.CurrentC == 36 && t.WarnC == 85 && t.CritC == 90 && t.Source == "drive" && t.Status == "OK");
        nvme.InterfaceWarning.Should().BeNull();

        var hdd = disks.Single(d => d.DiskKey == "HD1");
        hdd.HealthPercent.Should().Be(100);
        hdd.Temperature!.Should().Match<TemperatureInfo>(t => t.WarnC == 50 && t.CritC == 55 && t.Source == "default" && t.Status == "Warning");
        hdd.TemperatureStatus.Should().Be("Warning");
        hdd.InterfaceWarning.Should().Contain("1 interface (CRC) error");
        (100 + hdd.Reasons!.Sum(r => r.Delta)).Should().Be(hdd.HealthPercent!.Value);

        var json = await viewer.GetStringAsync($"/api/endpoints/{id}/disks");
        json.Should().Contain("\"reasons\":").And.Contain("\"temperature\":").And.Contain("\"lifetimeMaxC\"").And.Contain("\"interfaceWarning\":");
    }

    [Fact]
    public async Task A_changed_default_applies_to_stored_drives_without_a_rescan_but_never_to_a_drive_with_its_own_limits()
    {
        var id = await factory.SeedEndpointAsync("PC-HDS-2", online: true);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IDiskHealthService>().StoreAsync(id, [Nvme("NV2", 75, warn: 85, crit: 90), Hdd("HD2", 51)]);
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var settings = await CurrentAsync(admin);
        try
        {
            settings.DiskTempHddWarnC = 60; settings.DiskTempHddCritC = 70;
            settings.DiskTempNvmeWarnC = 40; settings.DiskTempNvmeCritC = 50;
            (await admin.PutAsJsonAsync("/api/settings", settings)).StatusCode.Should().Be(HttpStatusCode.OK);

            var disks = (await viewer.GetFromJsonAsync<List<DiskHealthDto>>($"/api/endpoints/{id}/disks"))!;
            disks.Single(d => d.DiskKey == "HD2").Temperature!.Status.Should().Be("OK");
            var nvme = disks.Single(d => d.DiskKey == "NV2").Temperature!;
            (nvme.WarnC, nvme.Source, nvme.Status).Should().Be((85, "drive", "OK"));
        }
        finally
        {
            settings.DiskTempHddWarnC = 50; settings.DiskTempHddCritC = 55;
            settings.DiskTempNvmeWarnC = 70; settings.DiskTempNvmeCritC = 80;
            await admin.PutAsJsonAsync("/api/settings", settings);
        }
    }
}
