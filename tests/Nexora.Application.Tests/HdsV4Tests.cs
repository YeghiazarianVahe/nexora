using FluentAssertions;
using Nexora.Application.Services;
using Nexora.Infrastructure.Wmi;

namespace Nexora.Application.Tests;

/// <summary>
/// Score version 4, the HDS-compatible model (ADR-0005): raw-byte parsers, non-linear penalties, the reasons ledger, temperature limits and
/// their precedence, the interface (SMART 199) warning, and the serial join. Synthetic data only; the captured-drive acceptance cases are in
/// <see cref="HdsAcceptanceTests"/>.
/// </summary>
public sealed class HdsV4Tests
{
    private static SmartAttribute A(int id, int cur = 100, long raw = 0, int thr = 0) => new(id, $"A{id}", cur, cur, raw, thr);

    private static DiskRaw Drive(string media, string model, SmartAttribute[] attrs, NvmeInfo? nvme = null, int? temp = 35,
        int? driveWarn = null, int? driveCrit = null, long? crc = null, int? maxCounter = null, int? lifetimeMax = null) =>
        new("K", model, "S", media, media == "NVMe" ? "NVMe" : "SATA", 1_000_000_000_000, "Healthy", false, temp, maxCounter, 5000, null, null,
            null, null, SmartParser.Raw(attrs, 5), SmartParser.Raw(attrs, 197), SmartParser.Raw(attrs, 198), crc ?? SmartParser.Raw(attrs, 199), attrs,
            Nvme: nvme, DriveTempWarnC: driveWarn, DriveTempCritC: driveCrit, LifetimeMaxTempC: lifetimeMax);

    private static DiskRaw Hdd(params SmartAttribute[] attrs) => Drive("HDD", "Seagate ST1000", attrs);

    private static int Score(DiskRaw d, HealthWeights? w = null) => DiskHealthEvaluator.Evaluate(d, w).HealthPercent!.Value;

    // ---------- version

    [Fact]
    public void The_score_version_is_7() => DiskHealthEvaluator.ScoreVersion.Should().Be(7);

    // ---------- NVMe health log (raw bytes)

    private static byte[] HealthLog(int criticalWarning = 0, int kelvin = 310, int spare = 100, int spareThreshold = 10, int used = 0, ulong mediaErrors = 0, ulong powerOnHours = 100)
    {
        var b = new byte[512];
        b[0] = (byte)criticalWarning;
        b[1] = (byte)(kelvin & 0xFF); b[2] = (byte)(kelvin >> 8);
        b[3] = (byte)spare; b[4] = (byte)spareThreshold; b[5] = (byte)used;
        BitConverter.GetBytes(powerOnHours).CopyTo(b, 128);
        BitConverter.GetBytes(mediaErrors).CopyTo(b, 160);
        return b;
    }

    [Fact]
    public void The_health_log_is_decoded_from_raw_bytes_with_temperature_in_kelvin()
    {
        var h = NvmeSmart.ParseHealthLog(HealthLog(criticalWarning: 0x04, kelvin: 309, spare: 97, spareThreshold: 5, used: 4, mediaErrors: 7, powerOnHours: 2218))!;

        h.CriticalWarning.Should().Be(0x04);
        h.TemperatureC.Should().Be(36, "309 K is 36 C");
        (h.AvailableSpare, h.SpareThreshold, h.PercentageUsed).Should().Be((97, 5, 4));
        (h.MediaErrors, h.PowerOnHours).Should().Be((7L, 2218L));
        h.LifeLeftPercent.Should().Be(96);
    }

    [Fact]
    public void A_temperature_of_zero_kelvin_means_not_reported_and_a_short_log_is_rejected()
    {
        NvmeSmart.ParseHealthLog(HealthLog(kelvin: 0))!.TemperatureC.Should().Be(0);
        NvmeSmart.ParseHealthLog(new byte[100]).Should().BeNull();
        NvmeSmart.ParseHealthLog([]).Should().BeNull();
    }

    [Fact]
    public void The_percentage_used_may_run_past_100_without_a_negative_life()
    {
        NvmeSmart.ParseHealthLog(HealthLog(used: 120))!.LifeLeftPercent.Should().Be(0);
    }

    // ---------- NVMe Identify Controller (raw bytes)

    private static byte[] Identify(int wctemp, int cctemp, string model = "Micron 2500 NVMe 512GB", string serial = "SER123")
    {
        var b = new byte[4096];
        System.Text.Encoding.ASCII.GetBytes(serial.PadRight(20)).CopyTo(b, 4);
        System.Text.Encoding.ASCII.GetBytes(model.PadRight(40)).CopyTo(b, 24);
        System.Text.Encoding.ASCII.GetBytes("FW1.0".PadRight(8)).CopyTo(b, 64);
        b[266] = (byte)(wctemp & 0xFF); b[267] = (byte)(wctemp >> 8);
        b[268] = (byte)(cctemp & 0xFF); b[269] = (byte)(cctemp >> 8);
        return b;
    }

    [Fact]
    public void Identify_gives_the_drives_own_temperature_limits_in_celsius()
    {
        var id = NvmeSmart.ParseIdentify(Identify(wctemp: 358, cctemp: 363))!;   // 85 C / 90 C

        (id.WarningTempC, id.CriticalTempC).Should().Be((85, 90));
        id.Model.Should().Be("Micron 2500 NVMe 512GB");
        id.Serial.Should().Be("SER123");
        id.Firmware.Should().Be("FW1.0");
    }

    [Fact]
    public void A_WCTEMP_of_zero_is_not_reported_and_a_short_identify_is_rejected()
    {
        var id = NvmeSmart.ParseIdentify(Identify(wctemp: 0, cctemp: 0))!;
        (id.WarningTempC, id.CriticalTempC).Should().Be((null, null));
        NvmeSmart.ParseIdentify(new byte[200]).Should().BeNull();
    }

    // ---------- ATA attribute table (raw bytes)

    [Fact]
    public void The_ata_table_is_decoded_from_the_vendor_and_threshold_blocks()
    {
        var vendor = new byte[512];
        var thresholds = new byte[512];
        // entry 0: id 5, current 100, worst 99, raw 0x0000_0000_0123; entry 1: id 194
        vendor[2] = 5; vendor[5] = 100; vendor[6] = 99; vendor[7] = 0x23; vendor[8] = 0x01;
        vendor[14] = 194; vendor[17] = 70; vendor[18] = 60; vendor[19] = 41;
        thresholds[2] = 5; thresholds[3] = 36;

        var attrs = SmartParser.ParseTable(vendor, thresholds);

        attrs.Should().HaveCount(2);
        var a5 = attrs.Single(a => a.Id == 5);
        (a5.Current, a5.Worst, a5.Raw, a5.Threshold).Should().Be((100, 99, 0x123L, 36));
        attrs.Single(a => a.Id == 194).Threshold.Should().Be(0, "no threshold row for it");
        SmartParser.ParseTable(vendor).Should().HaveCount(2);
        SmartParser.ParseTable(new byte[4]).Should().BeEmpty();
    }

    // ---------- NVMe scoring

    [Fact]
    public void Nvme_health_is_100_minus_percentage_used()
    {
        foreach (var used in new[] { 0, 4, 37, 100 })
        {
            var r = DiskHealthEvaluator.Evaluate(Drive("NVMe", "Micron", [], new NvmeInfo(0, used, 100, 5, 0)));
            r.HealthPercent.Should().Be(Math.Max(0, 100 - used));
        }
    }

    [Theory]
    [InlineData(0x01)]   // spare below threshold
    [InlineData(0x04)]   // reliability degraded
    [InlineData(0x08)]   // read-only
    [InlineData(0x10)]   // volatile memory backup failed
    [InlineData(0x20)]   // persistent memory region
    public void Each_health_critical_warning_bit_makes_an_nvme_drive_critical(int bit)
    {
        var r = DiskHealthEvaluator.Evaluate(Drive("NVMe", "Micron", [], new NvmeInfo(bit, 2, 100, 5, 0)));
        r.Status.Should().Be("Critical");
        r.HealthPercent.Should().BeLessThanOrEqualTo(20);
        r.Reasons!.Should().Contain(x => x.Code == "nvme-warning");
    }

    [Fact]
    public void The_nvme_temperature_warning_bit_is_a_temperature_matter_not_a_health_penalty()
    {
        var r = DiskHealthEvaluator.Evaluate(Drive("NVMe", "Micron", [], new NvmeInfo(0x02, 2, 100, 5, 0), temp: 40));
        r.HealthPercent.Should().Be(98);
        r.Status.Should().Be("OK");
        r.Temperature!.Status.Should().Be("Warning", "the drive itself flagged a temperature problem");
        r.Reasons!.Should().Contain(x => x.Code == "nvme-warning-temp" && x.Delta == 0);
    }

    [Fact]
    public void Available_spare_below_the_threshold_is_critical_and_close_to_it_costs_points()
    {
        DiskHealthEvaluator.Evaluate(Drive("NVMe", "M", [], new NvmeInfo(0, 1, 9, 10, 0))).Status.Should().Be("Critical");
        var near = DiskHealthEvaluator.Evaluate(Drive("NVMe", "M", [], new NvmeInfo(0, 1, 12, 10, 0)));
        near.HealthPercent.Should().Be(94);
        near.Reasons!.Should().Contain(x => x.Code == "nvme-spare" && x.Delta == -5);
        DiskHealthEvaluator.Evaluate(Drive("NVMe", "M", [], new NvmeInfo(0, 1, 100, 10, 0))).Reasons!.Should().NotContain(x => x.Code == "nvme-spare");
    }

    [Fact]
    public void Nvme_media_errors_cost_more_the_more_there_are_but_ever_more_slowly()
    {
        var scores = new[] { 0L, 1, 2, 5, 20, 200, 20_000 }
            .Select(n => Score(Drive("NVMe", "M", [], new NvmeInfo(0, 0, 100, 5, n)))).ToArray();

        scores.Should().BeInDescendingOrder();
        scores[0].Should().Be(100);
        scores[1].Should().BeLessThan(100);
        (scores[1] - scores[2]).Should().BeGreaterThanOrEqualTo(scores[5] - scores[6], "the penalty is concave");
        scores[^1].Should().BeGreaterThanOrEqualTo(100 - new HealthWeights().MediaErrorMax);
    }

    // ---------- HDD scoring: non-linear, monotonic

    [Theory]
    [InlineData(5)]
    [InlineData(196)]
    [InlineData(197)]
    [InlineData(198)]
    [InlineData(10)]
    public void HDD_penalties_never_go_down_as_the_count_goes_up_and_start_with_a_visible_hit(int attribute)
    {
        // 197 and 198 hit the failing-drive floor at the limits in HealthWeights, so they are compared below that.
        var counts = attribute is 197 ? new long[] { 0, 1, 2, 3, 4, 5 } : attribute is 198 ? [0, 1] : [0, 1, 2, 5, 10, 50, 500, 5000];
        var scores = counts.Select(c => Score(Hdd(A(attribute, 100, c)))).ToArray();

        scores.Should().BeInDescendingOrder();
        scores[0].Should().Be(100);
        scores[1].Should().BeLessThan(100);
        scores[^1].Should().BeLessThanOrEqualTo(scores[1]);
        if (attribute is not 198) scores[^1].Should().BeLessThan(scores[1]);
    }

    [Fact]
    public void The_count_penalties_are_concave_and_capped()
    {
        var w = new HealthWeights();
        long[] n = [1, 2, 5, 10, 100, 100_000];
        var p = n.Select(c => HealthWeights.Graded(c, w.PendingBase, w.PendingScale, w.PendingMax)).ToArray();

        p[0].Should().Be(w.PendingBase);
        p.Should().BeInAscendingOrder();
        (p[1] - p[0]).Should().BeGreaterThanOrEqualTo(p[3] - p[2] - 1);
        p[^1].Should().Be(w.PendingMax);
    }

    [Fact]
    public void Spin_retries_and_reallocation_events_are_graded_penalties()
    {
        DiskHealthEvaluator.Evaluate(Hdd(A(10, 100, 1))).Reasons!.Single(r => r.Code == "10").Delta.Should().Be(-5);
        DiskHealthEvaluator.Evaluate(Hdd(A(196, 100, 1))).Reasons!.Single(r => r.Code == "196").Delta.Should().Be(-3);
        DiskHealthEvaluator.Evaluate(Hdd(A(10, 100, 100_000))).Reasons!.Single(r => r.Code == "10").Delta.Should().Be(-new HealthWeights().SpinRetryMax);
    }

    // ---------- SMART 199 is an interface issue

    [Fact]
    public void Attribute_199_is_a_connection_warning_and_never_part_of_the_score()
    {
        var clean = DiskHealthEvaluator.Evaluate(Hdd(A(5), A(199, 200, 0)));
        var noisy = DiskHealthEvaluator.Evaluate(Hdd(A(5), A(199, 200, 5_000)));

        noisy.HealthPercent.Should().Be(clean.HealthPercent);
        clean.InterfaceWarning.Should().BeNull();
        noisy.InterfaceWarning.Should().Contain("5000 interface (CRC) errors").And.Contain("cable");
        noisy.Reasons!.Single(r => r.Code == "199").Delta.Should().Be(0);
        noisy.Status.Should().Be("OK");
    }

    // ---------- SATA SSD

    [Fact]
    public void A_sata_ssd_takes_the_vendor_wear_attribute_and_names_it_in_the_reasons()
    {
        var r = DiskHealthEvaluator.Evaluate(Drive("SSD", "Samsung SSD 860", [A(177, 91, 9), A(5)]));
        r.HealthPercent.Should().Be(91);
        r.Reasons!.Single(x => x.Code == "life").Text.Should().Contain("attribute 177");

        DiskHealthEvaluator.Evaluate(Drive("SSD", "Intel SSD", [A(233, 80)])).Reasons!.Single(x => x.Code == "life").Text.Should().Contain("233");
        DiskHealthEvaluator.Evaluate(Drive("SSD", "Crucial BX500", [A(202, 92), A(173, 50)])).HealthPercent.Should().Be(92);
    }

    [Fact]
    public void A_sata_ssd_pays_for_reallocated_pending_and_uncorrectable_sectors_on_top_of_its_wear()
    {
        var wearOnly = Score(Drive("SSD", "Samsung SSD 860", [A(177, 91, 9)]));
        Score(Drive("SSD", "Samsung SSD 860", [A(177, 91, 9), A(5, 100, 3)])).Should().BeLessThan(wearOnly);
        Score(Drive("SSD", "Samsung SSD 860", [A(177, 91, 9), A(197, 100, 2)])).Should().BeLessThan(wearOnly);
        Score(Drive("SSD", "Samsung SSD 860", [A(177, 91, 9), A(198, 100, 1)])).Should().BeLessThan(wearOnly);
    }

    [Fact]
    public void ADATA_low_endurance_is_preserved_and_the_reasons_still_add_up()
    {
        var r = DiskHealthEvaluator.Evaluate(Drive("SSD", "ADATA SU800", [A(169, 100, 20)]));

        r.Status.Should().Be("Critical");
        r.HealthPercent.Should().Be(20);
        r.Reasons!.Should().NotContain(x => x.Code == "169-cap");
        (100 + r.Reasons!.Sum(x => x.Delta)).Should().Be(r.HealthPercent);
        DiskHealthEvaluator.Evaluate(Drive("SSD", "Kingston SA400", [A(169, 100, 20)])).HealthPercent.Should().BeNull("169 means this only on ADATA");
    }

    // ---------- the reasons ledger

    public static IEnumerable<object[]> LedgerCases()
    {
        yield return [Hdd(A(5, 100, 3), A(10, 100, 2), A(196, 100, 9))];
        yield return [Hdd(A(197, 100, 3), A(188, 100, 5000))];
        yield return [Hdd(A(197, 100, 8), A(198, 100, 2), A(5, 100, 900))];
        yield return [Drive("SSD", "ADATA SU800", [A(169, 100, 20), A(5, 100, 600), A(171, 100, 5)])];
        yield return [Drive("SSD", "Samsung SSD", [A(177, 40, 60), A(197, 100, 3)])];
        yield return [Drive("NVMe", "Micron", [], new NvmeInfo(0, 4, 100, 5, 0))];
        yield return [Drive("NVMe", "Micron", [], new NvmeInfo(0x01, 30, 3, 10, 400))];
        yield return [Drive("NVMe", "Micron", [], new NvmeInfo(0, 100, 11, 10, 100_000))];
        yield return [Hdd(A(5, 100, 5000), A(197, 100, 200), A(198, 100, 50), A(10, 100, 99), A(196, 100, 400), A(187, 100, 60))];
        yield return [Hdd(A(9, 90, 5000))];
        yield return [Hdd(A(1, 75, 33674841, 6), A(5, 100, 0, 10), A(199, 200, 1))];
    }

    [Theory]
    [MemberData(nameof(LedgerCases))]
    public void The_reason_points_always_add_up_to_100_minus_the_score(DiskRaw d)
    {
        var r = DiskHealthEvaluator.Evaluate(d);

        if (d.Attributes.Count == 1 && d.Attributes[0].Id == 9)
        {
            r.HealthPercent.Should().BeNull("power-on hours alone do not measure remaining life");
            return;
        }
        r.HealthPercent.Should().NotBeNull();
        (100 + r.Reasons!.Sum(x => x.Delta)).Should().Be(r.HealthPercent!.Value);
        r.Reasons!.All(x => x.Code.Length > 0 && x.Text.Length > 0).Should().BeTrue();
    }

    // ---------- temperature: separate from health, thresholds precedence

    [Fact]
    public void Temperature_never_changes_the_health_score()
    {
        var cool = DiskHealthEvaluator.Evaluate(Drive("HDD", "Seagate", [A(5)], temp: 30));
        var hot = DiskHealthEvaluator.Evaluate(Drive("HDD", "Seagate", [A(5)], temp: 90));

        hot.HealthPercent.Should().Be(cool.HealthPercent);
        hot.Status.Should().Be(cool.Status);
        hot.Reasons.Should().BeEquivalentTo(cool.Reasons);
        hot.Temperature!.Status.Should().Be("Critical");
        cool.Temperature!.Status.Should().Be("OK");
    }

    [Theory]
    [InlineData("HDD", 49, "OK")]
    [InlineData("HDD", 50, "Warning")]
    [InlineData("HDD", 54, "Warning")]
    [InlineData("HDD", 55, "Critical")]
    [InlineData("SSD", 59, "OK")]
    [InlineData("SSD", 60, "Warning")]
    [InlineData("SSD", 70, "Critical")]
    [InlineData("NVMe", 69, "OK")]
    [InlineData("NVMe", 70, "Warning")]
    [InlineData("NVMe", 80, "Critical")]
    public void The_class_defaults_apply_when_the_drive_reports_no_limits(string media, int temp, string status)
    {
        var t = DiskHealthEvaluator.Evaluate(Drive(media, "M", [A(5)], temp: temp)).Temperature!;

        t.Status.Should().Be(status);
        t.Source.Should().Be("default");
        t.CurrentC.Should().Be(temp);
    }

    [Fact]
    public void The_drives_own_limits_take_precedence_over_the_defaults()
    {
        // 85 / 90 reported by the drive: 75 C is fine for it although the NVMe default would call 75 a warning.
        var d = Drive("NVMe", "Micron", [], new NvmeInfo(0, 4, 100, 5, 0), temp: 75, driveWarn: 85, driveCrit: 90);
        var t = DiskHealthEvaluator.Evaluate(d).Temperature!;
        (t.WarnC, t.CritC, t.Source, t.Status).Should().Be((85, 90, "drive", "OK"));

        DiskHealthEvaluator.Evaluate(d with { TemperatureC = 86 }).Temperature!.Status.Should().Be("Warning");
        DiskHealthEvaluator.Evaluate(d with { TemperatureC = 91 }).Temperature!.Status.Should().Be("Critical");

        // changing the Admin defaults does not move the drive's own limits
        var admin = new DriveTempLimits(NvmeWarnC: 40, NvmeCritC: 50);
        DiskHealthEvaluator.Evaluate(d, limits: admin).Temperature!.Should().Match<TemperatureInfo>(x => x.WarnC == 85 && x.Source == "drive");
    }

    [Fact]
    public void Unreported_or_nonsense_drive_limits_fall_back_to_the_defaults()
    {
        foreach (var (w, c) in new (int?, int?)[] { (null, null), (null, 90), (85, null), (0, 0), (90, 85), (85, 85) })
        {
            var t = DiskHealthEvaluator.Evaluate(Drive("NVMe", "Micron", [], new NvmeInfo(0, 4, 100, 5, 0), temp: 60, driveWarn: w, driveCrit: c)).Temperature!;
            t.Source.Should().Be("default", $"({w},{c}) is not a usable pair");
            (t.WarnC, t.CritC).Should().Be((70, 80));
        }
    }

    [Fact]
    public void A_changed_admin_default_moves_the_status_of_drives_without_own_limits()
    {
        var d = Drive("HDD", "Seagate", [A(5)], temp: 52);
        DiskHealthEvaluator.Evaluate(d).Temperature!.Status.Should().Be("Warning");
        DiskHealthEvaluator.Evaluate(d, limits: new DriveTempLimits(HddWarnC: 55, HddCritC: 65)).Temperature!.Status.Should().Be("OK");
    }

    [Fact]
    public void The_stored_snapshot_uses_the_drives_own_limits_but_the_current_admin_defaults()
    {
        var own = new Nexora.Domain.DiskHealthSnapshot { MediaType = "NVMe", BusType = "NVMe", TemperatureC = 75, TempWarnC = 85, TempCritC = 90, TempThresholdSource = "drive" };
        var dflt = new Nexora.Domain.DiskHealthSnapshot { MediaType = "HDD", BusType = "SATA", TemperatureC = 52, TempWarnC = 50, TempCritC = 55, TempThresholdSource = "default" };
        var limits = new DriveTempLimits(HddWarnC: 60, HddCritC: 70);

        DiskHealthService.TemperatureOf(own, limits).Should().Match<TemperatureInfo>(t => t.WarnC == 85 && t.Source == "drive" && t.Status == "OK");
        DiskHealthService.TemperatureOf(dflt, limits).Should().Match<TemperatureInfo>(t => t.WarnC == 60 && t.CritC == 70 && t.Source == "default" && t.Status == "OK");
    }

    // ---------- lifetime maximum temperature

    [Fact]
    public void The_lifetime_maximum_comes_from_194_when_the_raw_bytes_are_consistent()
    {
        // Seagate: current in byte 0, minimum / maximum packed in the second word (35 / 44).
        SmartParser.LifetimeMaxTemperature([A(190, 58, 0x2C23_002A)]).Should().Be(44);
        // one word each: current 40, min 20, max 51
        SmartParser.LifetimeMaxTemperature([A(194, 60, 0x0033_0014_0028)]).Should().Be(51);
        // nonsense (maximum below current, or zero) is not a maximum
        SmartParser.LifetimeMaxTemperature([A(194, 60, 0x0012_0000_002A)]).Should().BeNull();
        SmartParser.LifetimeMaxTemperature([A(194, 60, 0x2A)]).Should().BeNull();
        SmartParser.LifetimeMaxTemperature([A(5)]).Should().BeNull();
    }

    [Fact]
    public void The_lifetime_maximum_is_shown_for_a_hard_drive_and_never_invented_for_nvme()
    {
        var hdd = DiskHealthEvaluator.Evaluate(Drive("HDD", "Seagate", [A(190, 58, 0x2C23_002A)], temp: 42)).Temperature!;
        hdd.LifetimeMaxC.Should().Be(44);

        var nvme = DiskHealthEvaluator.Evaluate(Drive("NVMe", "Micron", [], new NvmeInfo(0, 4, 100, 5, 0), temp: 36, maxCounter: 99)).Temperature!;
        nvme.LifetimeMaxC.Should().BeNull("the NVMe health log has no lifetime maximum");

        var counterOnly = DiskHealthEvaluator.Evaluate(Drive("SSD", "Crucial", [A(202, 92)], temp: 34, maxCounter: 61)).Temperature!;
        counterOnly.LifetimeMaxC.Should().Be(61, "Windows reports it for SATA drives");
    }

    // ---------- settings validation

    [Fact]
    public void Temperature_defaults_validate_and_match_the_documented_values()
    {
        var s = new Nexora.Application.PlatformSettings();
        s.Validate().Should().BeNull();
        s.ToTempLimits().Should().Be(new DriveTempLimits(50, 55, 60, 70, 70, 80));
    }

    [Theory]
    [InlineData("DiskTempHddWarnC", 19)]
    [InlineData("DiskTempHddWarnC", 101)]
    [InlineData("DiskTempHddCritC", 101)]
    [InlineData("DiskTempSsdWarnC", 0)]
    [InlineData("DiskTempSsdCritC", 150)]
    [InlineData("DiskTempNvmeWarnC", -5)]
    [InlineData("DiskTempNvmeCritC", 19)]
    [InlineData("DiskTempHddWarnC", 55)]    // warn == crit
    [InlineData("DiskTempSsdWarnC", 70)]
    [InlineData("DiskTempNvmeCritC", 70)]
    [InlineData("DiskTempNvmeWarnC", 90)]   // warn > crit
    public void Temperature_limits_outside_20_to_100_or_with_warn_not_below_crit_are_rejected(string property, int value)
    {
        var s = new Nexora.Application.PlatformSettings();
        typeof(Nexora.Application.PlatformSettings).GetProperty(property)!.SetValue(s, value);

        s.Validate().Should().NotBeNull();
    }

    [Fact]
    public void Temperature_limits_at_the_edges_of_the_range_are_accepted()
    {
        new Nexora.Application.PlatformSettings { DiskTempHddWarnC = 20, DiskTempHddCritC = 21, DiskTempNvmeWarnC = 99, DiskTempNvmeCritC = 100 }.Validate().Should().BeNull();
    }

    // ---------- the join is by serial, never by order

    private sealed record Rec(string Serial, string Pnp, string Id, SmartAttribute[] Attrs);

    private static readonly Rec[] Records =
    [
        new("SERIAL-A", @"IDE\DISKST1000VX012\SERIAL-A", "0", [A(5), A(197), A(194, 38, 38)]),
        new("SERIAL-B", @"IDE\DISKADATA_SU800\SERIAL-B", "1", [A(5, 100, 600), A(177, 80, 20)]),
        new("SERIAL-C", @"IDE\DISKWDC_WD10\SERIAL-C", "2", [A(5, 100, 3), A(197, 100, 4)]),
        new("SERIAL-D", @"IDE\DISKCT500BX500\SERIAL-D", "3", [A(202, 92), A(5)]),
    ];

    private static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var p in Permutations(rest)) yield return [items[i], .. p];
        }
    }

    [Fact]
    public void Every_enumeration_order_gives_each_drive_the_same_record_and_the_same_score()
    {
        Dictionary<string, (string Id, int Score)>? expected = null;
        foreach (var order in Permutations(Records))
        {
            var seen = new Dictionary<string, (string, int)>();
            foreach (var drive in Records)
            {
                // The drive is known by serial (and PnP / disk number); the candidates arrive in whatever order the host listed them.
                var (match, level) = DriveMatching.Find(order, new DriveKeys(drive.Serial, drive.Id, drive.Pnp), r => new DriveKeys(r.Serial, r.Id, r.Pnp));
                match.Should().NotBeNull();
                level.Should().Be(DriveMatching.Level.Serial);
                match!.Serial.Should().Be(drive.Serial);
                seen[drive.Serial] = (match.Id, Score(Drive(drive.Attrs.Any(a => a.Id is 177 or 202) ? "SSD" : "HDD", "M", match.Attrs)));
            }
            expected ??= seen;
            seen.Should().BeEquivalentTo(expected);
        }
    }

    [Fact]
    public void A_wrong_disk_number_does_not_borrow_another_drives_record_when_the_serial_matches()
    {
        // The drive's own serial finds its record even though the (stale) disk number points at another drive.
        var (match, level) = DriveMatching.Find(Records, new DriveKeys("SERIAL-C", "0", null), r => new DriveKeys(r.Serial, r.Id, r.Pnp));
        match!.Serial.Should().Be("SERIAL-C");
        level.Should().Be(DriveMatching.Level.Serial);
    }
}
