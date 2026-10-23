using FluentAssertions;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Application.Tests;

/// <summary>The fleet audit over fixtures shaped like stored scans: reference PCs pass, broken mappings are flagged.</summary>
public sealed class DiskAuditTests
{
    private static DiskHealthSnapshot Snap(string model, string serial, string media, string bus, int? health, int? mediaCode = 4, long? spindle = 0,
        string classSource = "media", string healthSource = "smart-attributes", bool smartMatched = true, int smartRecords = 2, int driveCount = 2) => new()
    {
        Id = Guid.NewGuid(), DiskKey = serial, Model = model, Serial = serial, MediaType = media, BusType = bus, HealthPercent = health,
        MediaTypeCode = mediaCode, SpindleSpeed = spindle, ClassSource = classSource, HealthSource = healthSource, PhysicalMatch = "Serial",
        SmartMatched = smartMatched, HostSmartRecords = smartRecords, HostDriveCount = driveCount, ScoreVersion = DiskHealthEvaluator.ScoreVersion,
    };

    // wks-44: ADATA SATA SSD (65) + Seagate HDD (100); both have SMART records.
    private static DiskHealthSnapshot[] Pi44() =>
    [
        Snap("ADATA SU800NS38", "2J1520171828", "SSD", "SATA", 65),
        Snap("ST1000DM010-2EP102", "ZN15K892", "HDD", "SATA", 100, mediaCode: 3, spindle: 4_294_967_295),
    ];

    // wks-111: Micron NVMe read from its own SMART log (96) + Seagate HDD (100); one SMART record (the HDD's), NVMe has none.
    private static DiskHealthSnapshot[] Pi111() =>
    [
        Snap("2500 Micron 512GB", "MICRON1", "NVMe", "NVMe", 96, healthSource: "nvme-smart-log", smartMatched: false, smartRecords: 1),
        Snap("ST1000VX012-3CU10C", "WWD2VWAH", "HDD", "SATA", 100, mediaCode: 3, spindle: 4_294_967_295, smartRecords: 1),
    ];

    private static bool Flagged(DiskAuditPc? pc) => pc is not null && pc.Findings.Any(f => !DiskAudit.IsInformational(f.Code));

    [Fact]
    public void The_reference_PCs_are_not_flagged_and_read_their_values()
    {
        var a = DiskAudit.Evaluate(Guid.NewGuid(), "wks-44", Pi44())!;
        var b = DiskAudit.Evaluate(Guid.NewGuid(), "wks-111", Pi111())!;

        Flagged(a).Should().BeFalse(string.Join("; ", a.Findings.Select(f => f.Message)));
        Flagged(b).Should().BeFalse(string.Join("; ", b.Findings.Select(f => f.Message)));
        (a.SsdHealth, a.HddHealth).Should().Be((65, 100));
        (b.SsdHealth, b.HddHealth).Should().Be((96, 100));
        a.Findings.Should().BeEmpty();
        b.Findings.Should().BeEmpty("the NVMe's health came from its own SMART log");
    }

    [Fact]
    public void An_NVMe_whose_health_is_not_from_its_SMART_log_is_noted_but_not_flagged()
    {
        var remote = new[]
        {
            Snap("2500 Micron 512GB", "M1", "NVMe", "NVMe", 100, healthSource: "reliability-counter", smartMatched: false, smartRecords: 1),
            Snap("ST1000", "S1", "HDD", "SATA", 100, mediaCode: 3, spindle: 4_294_967_295, smartRecords: 1),
        };

        var pc = DiskAudit.Evaluate(Guid.NewGuid(), "remote-nvme", remote)!;

        pc.Findings.Should().ContainSingle().Which.Code.Should().Be(DiskAudit.NvmeNoSmartLog);
        Flagged(pc).Should().BeFalse();
    }

    [Fact]
    public void An_NVMe_that_does_answer_with_a_SMART_record_is_a_normal_joined_drive()
    {
        var pc = DiskAudit.Evaluate(Guid.NewGuid(), "nvme-with-smart", [Snap("2500 Micron 1024GB", "N1", "NVMe", "NVMe", 100, smartRecords: 1, driveCount: 1)])!;

        pc.Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_PC_with_an_Unknown_type_drive_is_flagged_and_the_drive_counts_for_neither_figure()
    {
        var snaps = new[]
        {
            Snap("Mystery USB bridge", "U1", "Unknown", "USB", 40, mediaCode: 0, spindle: 4_294_967_295, classSource: "none", smartRecords: 0, driveCount: 2),
            Snap("CT500BX500SSD1", "S1", "SSD", "SATA", 92, smartRecords: 0, driveCount: 2),
        };

        var pc = DiskAudit.Evaluate(Guid.NewGuid(), "unknown-type", snaps)!;

        Flagged(pc).Should().BeTrue();
        pc.Findings.Should().Contain(f => f.Code == DiskAudit.UnknownType);
        (pc.SsdHealth, pc.HddHealth).Should().Be((92, (int?)null), "the Unknown drive's 40% belongs to neither");
    }

    [Fact]
    public void A_drive_without_health_is_flagged_and_two_SSDs_plus_one_HDD_report_the_worst_SSD()
    {
        var snaps = new[]
        {
            Snap("SSD-A", "A", "SSD", "SATA", 90, smartRecords: 3, driveCount: 3),
            Snap("SSD-B", "B", "SSD", "SATA", 71, smartRecords: 3, driveCount: 3),
            Snap("HDD-C", "C", "HDD", "SATA", null, mediaCode: 3, spindle: 7200, healthSource: "none", smartMatched: false, smartRecords: 3, driveCount: 3),
        };

        var pc = DiskAudit.Evaluate(Guid.NewGuid(), "three-drives", snaps)!;

        (pc.SsdHealth, pc.HddHealth).Should().Be((71, (int?)null));
        pc.Findings.Select(f => f.Code).Should().Contain([DiskAudit.NoHealth, DiskAudit.UnjoinedSmart]);
        Flagged(pc).Should().BeTrue();
    }

    [Fact]
    public void A_SMART_record_count_that_differs_from_the_drive_count_is_flagged()
    {
        var broken = new[] { Snap("A", "A", "SSD", "SATA", 90, smartRecords: 3), Snap("B", "B", "HDD", "SATA", 90, mediaCode: 3, spindle: 7200, smartRecords: 3) };

        var pc = DiskAudit.Evaluate(Guid.NewGuid(), "broken", broken)!;

        pc.Findings.Select(f => f.Code).Should().Contain(DiskAudit.CountMismatch);
        Flagged(pc).Should().BeTrue();
    }

    [Fact]
    public void A_class_that_contradicts_the_rotation_rate_is_flagged()
    {
        var pc = DiskAudit.Evaluate(Guid.NewGuid(), "conflict", [Snap("Odd", "O1", "SSD", "SATA", 90, spindle: 7200, smartRecords: 1, driveCount: 1)])!;
        pc.Findings.Should().Contain(f => f.Code == DiskAudit.ClassConflict);
    }

    [Fact]
    public void Rows_stored_before_the_mapping_was_recorded_note_a_weak_signal_but_are_not_flagged_for_it()
    {
        var legacy = new DiskHealthSnapshot { Id = Guid.NewGuid(), DiskKey = "L1", Model = "Old", MediaType = "HDD", BusType = "SATA", HealthPercent = 90 };

        var pc = DiskAudit.Evaluate(Guid.NewGuid(), "legacy", [legacy])!;

        pc.Findings.Should().ContainSingle().Which.Code.Should().Be(DiskAudit.WeakSignal);
        Flagged(pc).Should().BeFalse();
    }

    [Fact]
    public void The_CSV_lists_every_drive_of_a_flagged_PC_with_its_mapping_facts()
    {
        var pc = DiskAudit.Evaluate(Guid.NewGuid(), "=bad host", [Snap("A", "A", "SSD", "SATA", 90, smartRecords: 3), Snap("B", "B", "HDD", "SATA", 90, mediaCode: 3, spindle: 7200, smartRecords: 3)])!;

        var csv = DiskAudit.Csv([pc]);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(3);
        lines[0].Should().StartWith("Hostname,Findings,SsdHealthWorst");
        lines[1].Should().StartWith("'=bad host,UnjoinedSmart CountMismatch");
        lines[1].Should().Contain("SATA,4,SSD,0,SSD,media,90,smart-attributes,Serial,true");
    }

    // ---------- the identity join with an empty serial

    private sealed record Physical(string DeviceId, string Serial, string Name);

    [Fact]
    public void A_drive_with_an_empty_serial_joins_by_disk_number_never_by_position()
    {
        // Two physical-disk records with blank serials, listed in the opposite order to the drives.
        Physical[] records = [new("1", "", "second disk"), new("0", "", "first disk")];

        var first = DriveMatching.Find(records, new DriveKeys("", "0", null), p => new DriveKeys(p.Serial, p.DeviceId, null));
        var second = DriveMatching.Find(records, new DriveKeys("   ", "1", null), p => new DriveKeys(p.Serial, p.DeviceId, null));

        first.Match!.Name.Should().Be("first disk");
        first.Level.Should().Be(DriveMatching.Level.DiskNumber);
        second.Match!.Name.Should().Be("second disk");
    }

    [Fact]
    public void An_empty_serial_falls_back_to_the_PnP_id_before_the_disk_number()
    {
        string[] smartInstances = [@"SCSI\Disk&Ven_&Prod_B\5&2&0&000000_0", @"SCSI\Disk&Ven_&Prod_A\4&1&0&040000_0"];

        var (match, level) = DriveMatching.Find(smartInstances, new DriveKeys("", "0", @"SCSI\DISK&VEN_&PROD_A\4&1&0&040000"), i => new DriveKeys(null, null, i));

        match.Should().Be(smartInstances[1]);
        level.Should().Be(DriveMatching.Level.PnpId);
    }
}
