using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Infrastructure.Wmi;

namespace Nexora.Application.Tests;

public sealed class RemoteNvmeTests
{
    [Fact]
    public async Task Optional_live_hdd_capture_reads_fault_attributes_without_elevating_the_server()
    {
        var host = Environment.GetEnvironmentVariable("NEXORA_LIVE_HDD_HOST");
        if (string.IsNullOrEmpty(host)) return;
        var collector = new WmiEndpointCollector(Options.Create(new NexoraOptions()), NullLogger<WmiEndpointCollector>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var disks = await collector.CollectDisksAsync(host, timeout.Token);
        var hdd = disks.Single(d => d.Model == "ST1000VX012-3CU10C");
        hdd.Attributes.Should().Contain(a => a.Id == 5);
        hdd.Attributes.Should().Contain(a => a.Id == 197);
        hdd.Attributes.Should().Contain(a => a.Id == 198);
        var result = DiskHealthEvaluator.Evaluate(hdd, new HealthWeights { HddMethod = "smart-raw" });
        result.HealthPercent.Should().Be(100);
    }
    [Fact]
    public void Native_reply_uses_authenticated_machine_identity_even_when_connected_by_ip_or_alias()
    {
        RemoteNvmeReader.MachineMatches("WKS-TB011", "wks-tb011").Should().BeTrue();
        RemoteNvmeReader.MachineMatches("OTHER-PC", "WKS-TB011").Should().BeFalse();
        RemoteNvmeReader.MachineMatches(null, "WKS-TB011").Should().BeFalse();
    }
    [Fact]
    public void Native_logs_match_the_same_physical_disk_by_index_model_and_normalized_serial()
    {
        var disk = new RemoteNvmeReader.NativeDisk(0, "WD Blue SN570 250GB", "E823_8FA6.", "", "");
        RemoteNvmeReader.Matches(disk, 0, "E8238FA6", disk.Model).Should().BeTrue();
        RemoteNvmeReader.Matches(disk, 1, "E8238FA6", disk.Model).Should().BeFalse();
        RemoteNvmeReader.Matches(disk, 0, "OTHER", disk.Model).Should().BeFalse();
        RemoteNvmeReader.Matches(disk, 0, "E8238FA6", "Other model").Should().BeFalse();
        RemoteNvmeReader.Matches(disk, 0, null, disk.Model).Should().BeFalse();
    }

    private static DiskRaw Nvme(int? wear, NvmeInfo? log = null) => new("WD", "WD Blue SN570 250GB", "WD", "NVMe", "NVMe", 250_056_737_280, "Healthy", false,
        35, null, null, wear, null, null, null, null, null, null, null, [], Nvme: log);

    [Fact]
    public void Placeholder_zero_wear_without_a_native_nvme_log_does_not_mean_100_percent_health()
    {
        var result = DiskHealthEvaluator.Evaluate(Nvme(0));
        result.HealthPercent.Should().BeNull();
        result.Summary.Should().Contain("insufficient");
    }

    [Fact]
    public void Forty_four_percent_native_endurance_used_means_56_percent_remaining()
    {
        var result = DiskHealthEvaluator.Evaluate(Nvme(0, new NvmeInfo(0, 44, 100, 10, 0)));
        result.HealthPercent.Should().Be(56);
        result.Reasons.Should().Contain(r => r.Code == "nvme-used" && r.Delta == -44);
    }

    [Fact]
    public void Eight_percent_native_endurance_used_means_92_percent_remaining_even_when_windows_reports_zero()
    {
        var result = DiskHealthEvaluator.Evaluate(Nvme(0, new NvmeInfo(0, 8, 100, 10, 0)));
        result.HealthPercent.Should().Be(92);
        result.Reasons.Should().Contain(r => r.Code == "nvme-used" && r.Delta == -8);
        DiskHealthEvaluator.Evaluate(Nvme(0, new NvmeInfo(0, 0, 100, 10, 0))).HealthPercent.Should().Be(100);
    }

    [Fact]
    public async Task Optional_live_capture_reads_native_health_from_a_reference_pc()
    {
        var host = Environment.GetEnvironmentVariable("NEXORA_LIVE_DISK_HOST");
        if (string.IsNullOrEmpty(host)) return;
        var collector = new WmiEndpointCollector(Options.Create(new NexoraOptions()), NullLogger<WmiEndpointCollector>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var disks = await collector.CollectDisksAsync(host, timeout.Token);
        var expectedModel = Environment.GetEnvironmentVariable("NEXORA_LIVE_DISK_MODEL") ?? "WD Blue SN570 250GB";
        var expectedHealth = int.Parse(Environment.GetEnvironmentVariable("NEXORA_LIVE_DISK_HEALTH") ?? "92");
        var ssd = disks.Single(d => d.Model == expectedModel);
        ssd.Nvme.Should().NotBeNull("the live reference must return a native NVMe log");
        var result = DiskHealthEvaluator.Evaluate(ssd);
        result.HealthPercent.Should().Be(expectedHealth);
        var report = Environment.GetEnvironmentVariable("NEXORA_LIVE_DISK_REPORT");
        if (!string.IsNullOrEmpty(report)) await File.WriteAllTextAsync(report, System.Text.Json.JsonSerializer.Serialize(ssd, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
