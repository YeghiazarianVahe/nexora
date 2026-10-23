using FluentAssertions;
using Nexora.Application.Services;

namespace Nexora.Application.Tests;

/// <summary>The weighted health model (fixtures only, no live drives) and the separate temperature reading.</summary>
public sealed class HealthModelTests
{
    private static SmartAttribute A(int id, int cur = 100, long raw = 0, int thr = 0) => new(id, $"A{id}", cur, cur, raw, thr);

    private static DiskRaw Hdd(params SmartAttribute[] attrs) => Make("HDD", "Seagate ST1000", attrs, null);
    private static DiskRaw Make(string media, string model, SmartAttribute[] attrs, NvmeInfo? nvme, int? life = null) =>
        new("K", model, "S", media, media == "NVMe" ? "NVMe" : "SATA", 1_000_000_000_000, "Healthy", false, 35, null, 5000, null, life,
            null, null, SmartParser.Raw(attrs, 5), SmartParser.Raw(attrs, 197), SmartParser.Raw(attrs, 198), SmartParser.Raw(attrs, 199), attrs, Nvme: nvme);

    [Fact]
    public void A_clean_drive_is_about_100()
    {
        var r = DiskHealthEvaluator.Evaluate(Hdd(A(5), A(197), A(198), A(9, 90, 5000)));
        r.HealthPercent.Should().BeInRange(98, 100);
        r.Status.Should().Be("OK");
    }

    [Fact]
    public void Pending_and_uncorrectable_sectors_on_a_hard_drive_are_critical_and_named()
    {
        var r = DiskHealthEvaluator.Evaluate(Hdd(A(5), A(197, 100, 8), A(198, 100, 2)));
        r.Status.Should().Be("Critical");
        r.HealthPercent.Should().BeLessThan(50);
        r.Reasons!.Select(x => x.Code).Should().Contain(["197", "198"]);
        r.Reasons!.Single(x => x.Code == "197").Delta.Should().BeLessThan(0);
        r.Summary.Should().Contain("Pending sectors: 8").And.Contain("−");
    }

    [Fact]
    public void Reallocated_sectors_alone_are_a_graded_penalty_not_a_floor()
    {
        DiskHealthEvaluator.Evaluate(Hdd(A(5, 100, 3))).HealthPercent.Should().Be(90);
        DiskHealthEvaluator.Evaluate(Hdd(A(5, 100, 600))).HealthPercent.Should().Be(55);
    }

    [Fact]
    public void Secondary_signals_cost_points_but_background_noise_does_not()
    {
        DiskHealthEvaluator.Evaluate(Hdd(A(188, 100, 42))).HealthPercent.Should().Be(100, "a few command timeouts are normal");
        DiskHealthEvaluator.Evaluate(Hdd(A(10, 100, 2))).HealthPercent.Should().BeLessThan(100);
        DiskHealthEvaluator.Evaluate(Hdd(A(196, 100, 8))).HealthPercent.Should().BeLessThan(100);
        DiskHealthEvaluator.Evaluate(Hdd(A(187, 100, 4))).HealthPercent.Should().BeLessThan(100);
        DiskHealthEvaluator.Evaluate(Hdd(A(188, 100, 5000))).Reasons!.Should().Contain(r => r.Code == "188");
    }

    [Fact]
    public void Nvme_with_30_percent_used_and_spare_just_above_its_threshold_is_a_warning()
    {
        // v4: health = 100 - percentage used (30), minus 5 for the spare close to its threshold = 65.
        var r = DiskHealthEvaluator.Evaluate(Make("NVMe", "Some NVMe", [], new NvmeInfo(0, 30, 12, 10, 0)));
        r.HealthPercent.Should().Be(65);
        r.Status.Should().Be("Warning");
        r.HealthPercent.Should().BeInRange(50, 84);
        r.Reasons!.Select(x => x.Code).Should().Contain(["nvme-used", "nvme-spare"]);
    }

    [Fact]
    public void An_nvme_critical_warning_for_spare_below_threshold_is_critical_whatever_else_is_true()
    {
        var r = DiskHealthEvaluator.Evaluate(Make("NVMe", "Some NVMe", [], new NvmeInfo(0x01, 1, 100, 10, 0)));
        r.Status.Should().Be("Critical");
        DiskHealthEvaluator.Evaluate(Make("NVMe", "Some NVMe", [], new NvmeInfo(0, 1, 5, 10, 0))).Status.Should().Be("Critical", "spare under its threshold even if the flag is clear");
    }

    [Fact]
    public void Nvme_percentage_used_4_is_96()
    {
        DiskHealthEvaluator.Evaluate(Make("NVMe", "2500 Micron", [], new NvmeInfo(0, 4, 100, 5, 0))).HealthPercent.Should().Be(96);
    }

    [Fact]
    public void Ssd_program_erase_failures_are_penalised()
    {
        DiskHealthEvaluator.Evaluate(Make("SSD", "Crucial", [A(171, 100, 3)], null)).HealthPercent.Should().BeLessThan(90);
    }

    // ---------- ADATA attribute 169

    private static DiskRaw Adata(string model, int rawLife, string media = "SSD") =>
        Make(media, model, [A(169, 100, rawLife)], null, life: rawLife);

    [Fact]
    public void ADATA_low_life_is_not_artificially_raised_to_50_percent()
    {
        var r = DiskHealthEvaluator.Evaluate(Adata("ADATA SU800", 20));
        r.Status.Should().Be("Critical");
        r.HealthPercent.Should().Be(20);
        r.Summary.Should().Contain("attribute 169");
        DiskHealthEvaluator.Evaluate(Adata("ADATA SU800", 65)).HealthPercent.Should().Be(65, "the cap only lifts a Critical");
    }

    [Fact]
    public void ADATA_169_can_be_switched_off_and_never_touches_other_vendors()
    {
        var off = new HealthWeights { Adata169Enabled = false };
        DiskHealthEvaluator.Evaluate(Adata("ADATA SU800", 20), off).HealthPercent.Should().Be(100, "the fixture also reports zero fault counters, but disabled wear is not used");
        DiskHealthEvaluator.Evaluate(Adata("Kingston SA400", 20)).HealthPercent.Should().Be(100, "only the fixture's zero fault counters are used, not attribute 169");
    }

    [Fact]
    public void A_real_fault_on_an_ADATA_drive_is_not_hidden_by_the_cap()
    {
        var d = Make("SSD", "ADATA SU800", [A(169, 100, 20), A(5, 100, 600), A(171, 100, 5)], null, life: 20);
        DiskHealthEvaluator.Evaluate(d).HealthPercent.Should().BeLessThan(50);
    }

    // ---------- N/A

    [Fact]
    public void Missing_SMART_is_NA_with_a_reason_not_100()
    {
        var d = new DiskRaw("K", "M", "S", "HDD", "SATA", 1, "Unknown", false, null, null, null, null, null, null, null, null, null, null, null, []);
        var r = DiskHealthEvaluator.Evaluate(d);
        r.HealthPercent.Should().BeNull();
        r.Status.Should().Be("Unknown");
        r.Summary.Should().StartWith("Unavailable");
    }

    [Fact]
    public void Windows_only_health_is_labelled_as_such()
    {
        var d = Make("HDD", "M", [], null);
        DiskHealthEvaluator.Evaluate(d).Summary.Should().Contain("insufficient endurance or fault evidence");
    }

    // ---------- weights are configuration

    [Fact]
    public void Changing_a_weight_changes_the_score_without_new_data()
    {
        var d = Hdd(A(197, 100, 2));
        var normal = DiskHealthEvaluator.Evaluate(d).HealthPercent;
        var harsher = DiskHealthEvaluator.Evaluate(d, new HealthWeights { PendingScale = 20 }).HealthPercent;
        harsher.Should().BeLessThan(normal!.Value);
        new HealthWeights { ReallocatedTiers = "1:50" }.ParseReallocatedTiers().Should().Equal((1L, 50));
    }

    // ---------- temperature

    [Fact]
    public void Temperature_comes_from_194_low_byte_then_190()
    {
        SmartParser.Temperature([A(194, 70, 0x2A)]).Should().Be(42);
        SmartParser.Temperature([A(194, 70, 0x0012_0000_002A)]).Should().Be(42, "only the low byte is the temperature");
        SmartParser.Temperature([A(190, 60, 0x28)]).Should().Be(40);
        SmartParser.Temperature([A(1)]).Should().BeNull();
    }

    [Fact]
    public void Temperature_limits_are_configurable()
    {
        var d = new DiskRaw("K", "M", "S", "HDD", "SATA", 1, "Healthy", false, null, null, null, null, null, null, null, null, null, null, null, new[] { A(194, 70, 55) });
        DiskHealthEvaluator.Evaluate(d).TempStatus.Should().Be("Critical", "v4: the HDD critical limit is 55, not 60");
        DiskHealthEvaluator.Evaluate(d, limits: new DriveTempLimits(HddWarnC: 60, HddCritC: 70)).TempStatus.Should().Be("OK");
    }
}
