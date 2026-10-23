using FluentAssertions;
using Nexora.Application.Services;

namespace Nexora.Application.Tests;

public sealed class StorageEvidenceTests
{
    [Theory]
    [InlineData(0, false)] [InlineData(2, false)]
    [InlineData(1, true)] [InlineData(4, true)] [InlineData(8, true)] [InlineData(16, true)] [InlineData(32, true)]
    public void Collector_and_scorer_distinguish_temperature_flags_from_health(int flags, bool healthWarning) =>
        DiskHealthEvaluator.HasNvmeHealthWarning(flags).Should().Be(healthWarning);

    private static DiskRaw Drive => new("K", "Unknown vendor", null, "SSD", "SATA", 500_000_000_000,
        "Healthy", false, 35, null, 5000, null, null, null, null, null, null, null, null, []);

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(120)] [InlineData(255)]
    public void Impossible_temperatures_are_unavailable(int temperature)
    {
        var result = DiskHealthEvaluator.Evaluate(Drive with { TemperatureC = temperature });
        result.TempC.Should().BeNull();
        result.Temperature!.CurrentC.Should().BeNull();
        result.TempStatus.Should().BeNull();
        result.HealthPercent.Should().BeNull("temperature and hours do not measure health");
    }

    [Theory]
    [InlineData(100)] [InlineData(150)] [InlineData(255)]
    public void Exhausted_nvme_endurance_has_no_invented_positive_floor(int used)
    {
        var result = DiskHealthEvaluator.Evaluate(Drive with { Nvme = new NvmeInfo(0, used, 100, 5, 0) });
        result.HealthPercent.Should().Be(0);
        result.Status.Should().Be("Critical");
        (100 + result.Reasons!.Sum(r => r.Delta)).Should().Be(0);
    }

    [Theory]
    [InlineData(-1, 100, 5)] [InlineData(256, 100, 5)] [InlineData(10, 101, 5)] [InlineData(10, 100, -1)]
    public void Invalid_nvme_fields_never_become_a_healthy_estimate(int used, int spare, int threshold)
    {
        var result = DiskHealthEvaluator.Evaluate(Drive with { Nvme = new NvmeInfo(0, used, spare, threshold, 0) });
        result.HealthPercent.Should().BeNull();
        result.Reasons.Should().Contain(r => r.Code == "invalid-nvme");
    }

    [Theory]
    [InlineData(169)] [InlineData(173)] [InlineData(177)] [InlineData(202)]
    public void Vendor_specific_counters_are_not_life_on_an_unidentified_ssd(int id) =>
        DiskHealthEvaluator.Evaluate(Drive with { Attributes = [new SmartAttribute(id, "Vendor-specific", 85, 85, 10)] }).HealthPercent.Should().BeNull();

    [Fact]
    public void Contradictory_endurance_is_visible_and_conservative()
    {
        var result = DiskHealthEvaluator.Evaluate(Drive with { LifeLeftPercent = 90, WearPercent = 80 });
        result.HealthPercent.Should().Be(20);
        result.Reasons.Should().Contain(r => r.Code == "telemetry-conflict");
    }

    [Fact]
    public void Temperature_smart_thresholds_do_not_become_health_failures()
    {
        var result = DiskHealthEvaluator.Evaluate(Drive with { LifeLeftPercent = 90,
            Attributes = [new SmartAttribute(194, "Temperature", 30, 30, 70, 40)], WearPercent = 10 });
        result.HealthPercent.Should().Be(90);
        result.Status.Should().Be("OK");
    }

    [Fact]
    public void Standard_nvme_log_wins_over_a_conflicting_windows_counter()
    {
        var result = DiskHealthEvaluator.Evaluate(Drive with { Nvme = new NvmeInfo(0, 4, 100, 5, 0), WearPercent = 80 });
        result.HealthPercent.Should().Be(96);
        result.Reasons.Should().Contain(r => r.Code == "telemetry-conflict");
    }
}
