using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Services;

namespace Nexora.Application.Tests;

/// <summary>
/// Reference PCs, from real drives and the values the service desk reads in HD Sentinel:
/// wks-111 SSD 96 / HDD 100, wks-44 SSD 65 / HDD 100, wks-50 SSD 92 (its HDD is healthy). Hours of use alone never lower a score.
/// </summary>
public sealed class HealthReferenceTests
{
    private static SmartAttribute A(int id, int current, long raw = 0, int threshold = 0) => new(id, "attr", current, current, raw, threshold);

    // wks-111: Seagate ST1000VX012, 7807 h, power-on attribute at 92, one old CRC error.
    private static readonly DiskRaw Pi111Hdd = new(
        "WWD2VWAH", "ST1000VX012-3CU10C", "WWD2VWAH", "HDD", "SATA", 1_000_000_000_000, "Healthy", false, 38, null, 7807, null, null,
        0, null, 0, 0, 0, 1,
        [A(1, 75, 33674841, 6), A(3, 97), A(5, 100, 0, 10), A(7, 71, 12886212, 45), A(9, 92, 7807), A(194, 38, 38), A(199, 200, 1)]);

    // wks-111: Micron 2500 NVMe. Its health log says "percentage used: 4", so 96% of its life is left.
    private static readonly DiskRaw Pi111Nvme = new(
        "0000_0000_0000_0001_00A0_7525_4E81_8DEC", "2500 Micron 512GB", "0000_0000_0000_0001_00A0_7525_4E81_8DEC", "NVMe", "NVMe", 512_105_932_800, "Healthy", false,
        36, null, 2218, 0, 96, null, null, null, null, null, null, []);

    // wks-44: ADATA SU800 SATA SSD. Attribute 169 keeps the remaining life in its RAW value (65) and leaves the normalized value at 100.
    private static readonly SmartAttribute[] Pi44SsdAttributes =
        [A(1, 100), A(5, 100), A(9, 100, 21727), A(12, 100, 228), A(169, 100, 65), A(177, 100, 2132, 50), A(181, 100), A(182, 100), A(187, 100), A(194, 100, 27), A(196, 100, 0, 16), A(199, 100)];

    private static DiskRaw Pi44Ssd() => new(
        "ADATA1", "ADATA SU800NS38", "ADATA1", "SSD", "SATA", 256_052_966_400, "Healthy", false, 28, null, 21727, 0,
        SmartParser.LifeLeft(Pi44SsdAttributes, isSsd: true, model: "ADATA SU800NS38"), null, null, 0, 0, 0, 0, Pi44SsdAttributes);

    // wks-44: Seagate ST1000DM010, 44620 h, power-on attribute at 50. Many hours, no fault.
    private static readonly DiskRaw Pi44Hdd = new(
        "ST1000DM010-SER", "ST1000DM010-2EP102", "ST1000DM010-SER", "HDD", "SATA", 1_000_202_273_280, "Healthy", false, 40, null, 44620, null, null,
        0, null, 0, 0, 0, 0,
        [A(1, 78, 69323989, 6), A(3, 98), A(4, 37, 65535, 20), A(5, 100, 0, 10), A(7, 71, 13325000, 45), A(9, 50, 44620), A(10, 100, 0, 97), A(12, 100, 231, 20),
         A(184, 100, 0, 99), A(187, 100), A(188, 100, 42), A(190, 58, 740491306, 40), A(193, 60, 81341), A(194, 42), A(197, 100), A(198, 100), A(199, 200)]);

    // wks-50: Seagate ST2000DM008, 13796 h, power-on attribute at 85.
    private static readonly DiskRaw Pi50Hdd = new(
        "WFL827JA", "ST2000DM008-2UB102", "WFL827JA", "HDD", "SATA", 2_000_000_000_000, "Healthy", false, 37, null, 13796, null, null,
        0, null, 0, 0, 0, 0,
        [A(1, 77, 54301983, 6), A(5, 100, 0, 10), A(7, 71, 13228020, 45), A(9, 85, 13796), A(194, 38, 38), A(199, 200)]);

    // wks-50: Crucial BX500 SSD, 92% lifetime remaining (attributes 202 and 173).
    private static readonly DiskRaw Pi50Ssd = new(
        "2428E8BB7B85", "CT500BX500SSD1", "2428E8BB7B85", "SSD", "SATA", 500_000_000_000, "Healthy", false, 34, null, 13797, 0, 92,
        0, null, 0, 0, 0, 0,
        [A(5, 100, 0, 10), A(9, 100, 13797), A(173, 92, 86), A(202, 92, 8, 1)]);

    private static int? Score(DiskRaw d) => DiskHealthEvaluator.Evaluate(d).HealthPercent;

    [Fact]
    public void ab_pi111_reads_SSD_96_and_HDD_100()
    {
        Score(Pi111Nvme).Should().Be(96);
        Score(Pi111Hdd).Should().Be(100, "7807 hours and one old CRC error are not a fault");
        DriveClassifier.Worst([new("NVMe", "NVMe", Score(Pi111Nvme)), new("HDD", "SATA", Score(Pi111Hdd))]).Should().Be((96, 100));
    }

    [Fact]
    public void ab_pi44_reads_SSD_65_and_HDD_100()
    {
        Pi44Ssd().LifeLeftPercent.Should().Be(65, "attribute 169 raw is the remaining life on this controller");
        Score(Pi44Ssd()).Should().Be(65);
        Score(Pi44Hdd).Should().Be(100, "44620 hours of use are not a fault");
        DriveClassifier.Worst([new("SSD", "SATA", Score(Pi44Ssd())), new("HDD", "SATA", Score(Pi44Hdd))]).Should().Be((65, 100));
    }

    [Fact]
    public void ab_pi50_reads_92_from_its_SSD_and_its_HDD_is_healthy()
    {
        Score(Pi50Ssd).Should().Be(92);
        Score(Pi50Hdd).Should().Be(100);
        DriveClassifier.Worst([new("SSD", "SATA", Score(Pi50Ssd)), new("HDD", "SATA", Score(Pi50Hdd))]).Should().Be((92, 100));
    }

    [Fact]
    public void Attribute_169_raw_counts_as_remaining_life_only_on_ADATA_SSDs()
    {
        SmartAttribute[] attrs = [A(169, 100, 65)];

        SmartParser.LifeLeft(attrs, isSsd: true, model: "ADATA SU800NS38").Should().Be(65);
        SmartParser.LifeLeft(attrs, isSsd: true, model: "Other SSD").Should().BeNull("169 has no universal meaning");
        SmartParser.LifeLeft(attrs, isSsd: false, model: "ADATA HD330").Should().BeNull("the raw rule is for SSDs only");
        SmartParser.LifeLeft([A(169, 100, 5000)], isSsd: true, model: "ADATA SU800NS38").Should().BeNull("an invalid raw percentage cannot fall back to a constant normalized 100");
    }

    [Fact]
    public void A_single_crc_error_is_reported_but_not_penalized()
    {
        var r = DiskHealthEvaluator.Evaluate(Pi111Hdd);
        r.Summary.Should().Contain("1 interface (CRC) error;");
        r.Status.Should().Be("OK");
    }

    [Fact]
    public void Many_crc_errors_are_an_interface_warning_and_do_not_cost_points()
    {
        // v4 (ADR-0005): SMART 199 is a cable / connection problem, reported but never scored.
        var noisy = Pi111Hdd with { CrcErrors = 100 };
        var r = DiskHealthEvaluator.Evaluate(noisy);
        r.HealthPercent.Should().Be(100);
        r.InterfaceWarning.Should().Contain("100 interface (CRC) errors");
    }

    [Fact]
    public void An_attribute_at_its_failure_threshold_is_critical()
    {
        var failing = Pi111Hdd with { Attributes = [.. Pi111Hdd.Attributes, A(5, 10, 900, 10)] };
        var r = DiskHealthEvaluator.Evaluate(failing);
        r.Status.Should().Be("Critical");
        r.Summary.Should().Contain("failure threshold");
    }

    [Fact]
    public void Attribute_202_only_means_lifetime_on_solid_state_drives()
    {
        var attrs = new[] { A(202, 92) };
        SmartParser.LifeLeft(attrs, isSsd: true, model: "Crucial BX500").Should().Be(92);
        SmartParser.LifeLeft(attrs, isSsd: false).Should().BeNull();
    }

    [Fact]
    public void Thresholds_are_merged_into_the_attributes()
    {
        var smart = new byte[512];
        var thr = new byte[512];
        smart[2] = 5; smart[2 + 3] = 100; smart[2 + 4] = 100;
        thr[2] = 5; thr[2 + 1] = 10;

        SmartParser.Parse(smart, thr).Single().Threshold.Should().Be(10);
        SmartParser.Parse(smart, null).Single().Threshold.Should().Be(0);
    }
}

public sealed class AdPathTests
{
    [Theory]
    [InlineData("CN=LAB-1,OU=Workstations,OU=Branch A,DC=contoso,DC=local", "Branch A/Workstations")]
    [InlineData("CN=PC1,CN=Computers,DC=contoso,DC=local", "Computers")]
    [InlineData("CN=PC1,OU=Head Office,DC=corp,DC=local", "Head Office")]
    [InlineData(@"CN=PC1,OU=Sales\, East,OU=Retail,DC=corp,DC=local", "Retail/Sales, East")]
    [InlineData("CN=PC1,DC=corp,DC=local", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_ou_path_is_root_first_without_the_domain_or_computer(string? dn, string? expected) =>
        AdPaths.OuFromDn(dn).Should().Be(expected);
}
