using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Services;

namespace Nexora.Application.Tests;

/// <summary>Classification and identity-join of drives, from fixtures shaped like the WMI records (no live WMI).</summary>
public sealed class DriveClassifierTests
{
    // ---------- fixtures: what Win32_DiskDrive, MSFT_PhysicalDisk and the SMART/health records say

    private sealed record Disk(int Index, string Model, string Serial, string Pnp);
    private sealed record Physical(string DeviceId, string Serial, int? Bus, int? Media, long? Spindle, string Name);
    private sealed record Health(string Key, int Percent);

    // wks-111: PhysicalDrive0 is the Seagate HDD, PhysicalDrive1 the Micron NVMe.
    private static readonly Disk SeagateDisk = new(0, "ST1000VX012-3CU10C", "            WWD2VWAH", @"SCSI\DISK&VEN_&PROD_ST1000VX012-3CU1\4&122FB2B7&0&040000");
    private static readonly Disk MicronDisk = new(1, "2500 Micron 512GB", "0000_0000_0000_0001_00A0_7525_4E81_8DEC.", @"SCSI\DISK&VEN_NVME&PROD_2500_MICRON_512G\5&17966DB9&0&000000");
    private static readonly Physical SeagatePhysical = new("0", "WWD2VWAH", 11, 3, 4_294_967_295, "ST1000VX012-3CU10C");
    private static readonly Physical MicronPhysical = new("1", "0000_0000_0000_0001_00A0_7525_4E81_8DEC.", 17, 4, 0, "2500 Micron 512GB");

    private static DriveKeys KeysOf(Disk d) => new(d.Serial, d.Index.ToString(), d.Pnp);

    /// <summary>Joins each disk to its physical-disk record and its health record the way the collector does, and returns what the table would show.</summary>
    private static (int? Ssd, int? Hdd) Summarize(IEnumerable<Disk> disks, IReadOnlyList<Physical> physical, IReadOnlyList<Health> health)
    {
        var inputs = new List<DriveHealthInput>();
        foreach (var d in disks)
        {
            var keys = KeysOf(d);
            var (pd, _) = DriveMatching.Find(physical, keys, p => new DriveKeys(p.Serial, p.DeviceId, null));
            var (h, _) = DriveMatching.Find(health, keys, r => new DriveKeys(r.Key, null, null));
            var cls = DriveClassifier.FromFacts(pd?.Bus, pd?.Media, pd?.Spindle);
            inputs.Add(new DriveHealthInput(pd?.Bus == 17 ? "NVMe" : cls, pd?.Bus == 17 ? "NVMe" : null, h?.Percent));
        }
        return DriveClassifier.Worst(inputs);
    }

    // ---------- the dual-drive box, joined by identity

    [Fact]
    public void A_dual_drive_box_maps_SSD_96_and_HDD_100_even_when_every_list_is_in_the_opposite_order()
    {
        // Health keyed by serial; physical records and health records both listed in reverse of the disk order.
        Physical[] physicalReversed = [MicronPhysical, SeagatePhysical];
        Health[] healthReversed = [new("0000_0000_0000_0001_00A0_7525_4E81_8DEC", 96), new("WWD2VWAH", 100)];

        Summarize([SeagateDisk, MicronDisk], physicalReversed, healthReversed).Should().Be((96, 100));
        Summarize([MicronDisk, SeagateDisk], [SeagatePhysical, MicronPhysical], healthReversed.Reverse().ToArray()).Should().Be((96, 100), "order never matters");
    }

    [Fact]
    public void Matching_never_uses_position_so_swapping_the_lists_cannot_swap_the_health()
    {
        var disks = new[] { SeagateDisk, MicronDisk };
        Physical[] a = [SeagatePhysical, MicronPhysical];
        Physical[] b = [MicronPhysical, SeagatePhysical];
        Health[] h1 = [new("WWD2VWAH", 100), new("0000_0000_0000_0001_00A0_7525_4E81_8DEC", 96)];
        Health[] h2 = [h1[1], h1[0]];

        var results = new[] { Summarize(disks, a, h1), Summarize(disks, a, h2), Summarize(disks, b, h1), Summarize(disks, b, h2) };

        results.Should().OnlyContain(r => r.Ssd == 96 && r.Hdd == 100);
    }

    [Fact]
    public void A_health_record_with_no_matching_key_gives_NA_not_another_drives_value()
    {
        var keys = KeysOf(SeagateDisk);
        Health[] onlyTheMicron = [new("0000_0000_0000_0001_00A0_7525_4E81_8DEC", 96)];

        var (match, level) = DriveMatching.Find(onlyTheMicron, keys, r => new DriveKeys(r.Key, null, null));

        match.Should().BeNull();
        level.Should().Be(DriveMatching.Level.None);
        Summarize([SeagateDisk, MicronDisk], [SeagatePhysical, MicronPhysical], onlyTheMicron).Should().Be((96, (int?)null), "the HDD has no health, it must not borrow the SSD's 96");
    }

    [Fact]
    public void Identity_levels_run_serial_then_pnp_then_disk_number_and_an_ambiguous_level_is_skipped()
    {
        Physical[] sameBlankSerial = [new("0", "0000", 11, 3, null, "A"), new("1", "0000", 17, 4, 0, "B")];
        var drive = new DriveKeys("0000", "1", null);

        var (match, level) = DriveMatching.Find(sameBlankSerial, drive, p => new DriveKeys(p.Serial, p.DeviceId, null));

        match!.Name.Should().Be("B");
        level.Should().Be(DriveMatching.Level.DiskNumber, "two records share the serial, so the serial level is ambiguous");

        var (bySerial, serialLevel) = DriveMatching.Find([MicronPhysical, SeagatePhysical], KeysOf(MicronDisk), p => new DriveKeys(p.Serial, p.DeviceId, null));
        (bySerial, serialLevel).Should().Be((MicronPhysical, DriveMatching.Level.Serial));

        // SMART instance names are the PnP id plus "_0", in any letter case.
        string[] instances = [@"SCSI\Disk&Ven_&Prod_ST1000VX012-3CU1\4&122fb2b7&0&040000_0"];
        var (smart, smartLevel) = DriveMatching.Find(instances, KeysOf(SeagateDisk), i => new DriveKeys(null, null, i));
        smart.Should().NotBeNull();
        smartLevel.Should().Be(DriveMatching.Level.PnpId);
        DriveMatching.Find(instances, KeysOf(MicronDisk), i => new DriveKeys(null, null, i)).Match.Should().BeNull();
    }

    // ---------- classification

    [Theory]
    [InlineData(0L, "SSD")]
    [InlineData(7200L, "HDD")]
    [InlineData(5400L, "HDD")]
    [InlineData(4_294_967_295L, "Unknown")]
    public void When_both_drives_say_Fixed_hard_disk_media_the_rotation_rate_decides(long spindle, string expected)
    {
        // Win32_DiskDrive.MediaType is "Fixed hard disk media" for SSDs and HDDs alike and is never an input; MSFT MediaType is unspecified (0).
        DriveClassifier.FromFacts(busType: 11, mediaType: 0, spindleSpeed: spindle).Should().Be(expected);
    }

    [Fact]
    public void NVMe_on_bus_17_is_an_SSD_with_no_media_type_and_even_a_rotation_rate_of_nothing()
    {
        DriveClassifier.FromFacts(17, null, null).Should().Be("SSD");
        DriveClassifier.FromFacts(17, 0, 4_294_967_295).Should().Be("SSD");
        DriveClassifier.FromFacts(17, 3, 7200).Should().Be("SSD", "the bus comes first");
    }

    [Theory]
    [InlineData(11, 4, null, "SSD")]
    [InlineData(11, 3, null, "HDD")]
    [InlineData(11, 4, 7200L, "SSD")]   // media type outranks the spindle value
    [InlineData(11, 3, 0L, "HDD")]
    [InlineData(null, null, null, "Unknown")]
    [InlineData(7, 0, null, "Unknown")]  // USB, nothing known: never forced into a class
    [InlineData(11, 5, null, "Unknown")] // storage-class memory etc.
    public void Priority_is_bus_then_media_type_then_rotation_then_unknown(int? bus, int? media, long? spindle, string expected) =>
        DriveClassifier.FromFacts(bus, media, spindle).Should().Be(expected);

    [Theory]
    [InlineData("SSD", "SATA", "SSD")]
    [InlineData("NVMe", "NVMe", "SSD")]
    [InlineData("HDD", "SATA", "HDD")]
    [InlineData("Unknown", "NVMe", "SSD")]
    [InlineData("Unknown", "SATA", "Unknown")]   // the model name is never used to guess
    [InlineData(null, null, "Unknown")]
    public void The_stored_scan_is_read_the_same_way_by_the_view_table_and_export(string? media, string? bus, string expected) =>
        DriveClassifier.Classify(media, bus).Should().Be(expected);

    // ---------- worst-of

    [Fact]
    public void Worst_health_is_taken_per_kind_across_two_SSDs_and_two_HDDs()
    {
        var drives = new[]
        {
            new DriveHealthInput("SSD", "SATA", 92), new DriveHealthInput("NVMe", "NVMe", 71),
            new DriveHealthInput("HDD", "SATA", 85), new DriveHealthInput("HDD", "SATA", 60),
        };

        DriveClassifier.Worst(drives).Should().Be((71, 60));
    }

    [Fact]
    public void A_kind_that_is_absent_gives_null_and_an_Unknown_drive_counts_for_neither()
    {
        DriveClassifier.Worst([new DriveHealthInput("SSD", "SATA", 90)]).Should().Be((90, (int?)null));
        DriveClassifier.Worst([new DriveHealthInput("HDD", "SATA", 70)]).Should().Be(((int?)null, 70));
        DriveClassifier.Worst([new DriveHealthInput("HDD", "SATA", null)]).Should().Be(((int?)null, (int?)null));
        DriveClassifier.Worst([]).Should().Be(((int?)null, (int?)null));
        DriveClassifier.Worst([new DriveHealthInput("Unknown", null, 55)]).Should().Be(((int?)null, (int?)null));
    }

    [Fact]
    public void The_system_volume_is_C_or_the_first_and_used_percent_is_exact()
    {
        var c = new VolumeDto("C:", "NTFS", 500_000_000_000, 125_000_000_000);
        var d = new VolumeDto("D:", "NTFS", 1_000_000_000_000, 900_000_000_000);

        SystemVolume.Of([d, c]).Should().Be(c);
        SystemVolume.Of([d]).Should().Be(d);
        SystemVolume.Of([]).Should().BeNull();
        SystemVolume.Of(null).Should().BeNull();
        SystemVolume.UsedPercent(c).Should().Be(75.0);
        SystemVolume.UsedPercent(d).Should().Be(10.0);
        SystemVolume.UsedPercent(new VolumeDto("E:", null, 0, 0)).Should().BeNull();
    }
}
