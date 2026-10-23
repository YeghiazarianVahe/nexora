using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>The ten-column export: SSD/HDD space and health, blanks for what is not known, and the same numbers as the table.</summary>
public sealed class DriveExportTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly TimeZoneInfo Yerevan = EndpointCsv.ExportZone;

    // ---------- fixtures

    private static ExportDrive Drive(string model, string type, int? health, long size, string? serial = null) =>
        new(model, serial ?? model, type, health, 35, 1000, size);

    private static VolumeDto Vol(string letter, double totalGib, double freeGib) => new(letter, "NTFS", (long)(totalGib * GiB), (long)(freeGib * GiB));

    /// <summary>The list row for a PC; SSD / HDD health come from the shared helper, exactly as the list API computes them.</summary>
    private static EndpointSummaryDto Pc(string name, bool online, IReadOnlyList<ExportDrive> drives, IReadOnlyList<VolumeDto>? volumes, double? cpu = 12.5, double? ram = 50,
        string? user = "CONTOSO\\user", DateTime? seen = null, string? ip = "10.0.0.5")
    {
        var (ssd, hdd) = DriveClassifier.Worst(drives.Select(d => new DriveHealthInput(d.Type, null, d.HealthPercent)));
        return new(Guid.NewGuid(), name, name, null, ip, user, "Windows 11", "10", 7200, seen ?? new DateTime(2026, 10, 12, 11, 44, 6, DateTimeKind.Utc), online,
            cpu, ram, 40, [], "OK", null, [], null, "Dell Inc.", "Vostro 3030", null, false, ssd, hdd, 16 * GiB, volumes);
    }

    private static Dictionary<string, string> Row(ExportRow row)
    {
        var cells = EndpointCsv.Cells(row, Yerevan);
        cells.Should().HaveCount(EndpointCsv.Header.Length);
        return EndpointCsv.Header.Zip(cells).ToDictionary(p => p.First, p => p.Second);
    }

    // wks-111: Micron NVMe (SSD) with C:, Seagate HDD with D:
    private static ExportRow Pi111(bool online = true, double cpu = 22, double ram = 59)
    {
        var drives = new[] { Drive("ST1000VX012-3CU10C", "HDD", 100, 1_000_202_273_280), Drive("2500 Micron 512GB", "SSD", 96, 512_105_932_800) };
        return new(Pc("wks-111", online, drives, [Vol("C:", 475.1, 316.5), Vol("D:", 931.4, 871.1)], cpu, ram), drives);
    }

    // wks-44: ADATA SATA SSD with C:, Seagate HDD with D:
    private static ExportRow Pi44()
    {
        var drives = new[] { Drive("ST1000DM010-2EP102", "HDD", 100, 1_000_202_273_280), Drive("ADATA SU800NS38", "SSD", 65, 256_052_966_400) };
        return new(Pc("wks-44", true, drives, [Vol("C:", 236.9, 32.4), Vol("D:", 931.4, 923.7)]), drives);
    }

    // ---------- schema

    [Fact]
    public void The_header_is_exactly_the_ten_columns_in_order()
    {
        EndpointCsv.Header.Should().Equal("PC Name", "IP Address", "Logged-in User", "Last Seen Online", "CPU %", "RAM %", "SSD Free / Total", "SSD Health %", "HDD Free / Total", "HDD Health %");
    }

    // ---------- the two reference PCs

    [Fact]
    public void Reference_PCs_export_their_SSD_and_HDD_health_and_space()
    {
        var a = Row(Pi111());
        (a["SSD Health %"], a["HDD Health %"]).Should().Be(("96", "100"));
        (a["SSD Free / Total"], a["HDD Free / Total"]).Should().Be(("316.5 / 475.1 GB", "871.1 / 931.4 GB"));

        var b = Row(Pi44());
        (b["SSD Health %"], b["HDD Health %"]).Should().Be(("65", "100"));
        (b["SSD Free / Total"], b["HDD Free / Total"]).Should().Be(("32.4 / 236.9 GB", "923.7 / 931.4 GB"));
    }

    [Fact]
    public void The_health_figures_come_from_the_shared_helper_even_when_the_drive_records_arrive_in_the_opposite_order()
    {
        // SMART health records keyed by serial, listed in reverse of the drive order: the identity join still gives the right drive its value.
        var disks = new[] { (Serial: "WWD2VWAH", Class: "HDD"), (Serial: "MICRON1", Class: "SSD") };
        var health = new[] { new { Serial = "MICRON1", Percent = 96 }, new { Serial = "WWD2VWAH", Percent = 100 } };
        var inputs = disks.Select(d =>
        {
            var (match, _) = DriveMatching.Find(health, new DriveKeys(d.Serial, null, null), h => new DriveKeys(h.Serial, null, null));
            return new DriveHealthInput(d.Class, null, match?.Percent);
        });

        DriveClassifier.Worst(inputs).Should().Be((96, 100));
    }

    // ---------- blanks

    [Fact]
    public void An_SSD_only_PC_leaves_the_HDD_columns_blank_and_an_HDD_only_PC_leaves_the_SSD_columns_blank()
    {
        var s = new[] { Drive("CT500BX500SSD1", "SSD", 92, 500 * GiB) };
        var h = new[] { Drive("ST2000DM008", "HDD", 88, 1800 * GiB) };
        var ssdOnly = Row(new(Pc("ssd", true, s, [Vol("C:", 464.7, 211.7)]), s));
        var hddOnly = Row(new(Pc("hdd", true, h, [Vol("C:", 1800, 900)]), h));

        (ssdOnly["SSD Free / Total"], ssdOnly["SSD Health %"], ssdOnly["HDD Free / Total"], ssdOnly["HDD Health %"]).Should().Be(("211.7 / 464.7 GB", "92", "", ""));
        (hddOnly["SSD Free / Total"], hddOnly["SSD Health %"], hddOnly["HDD Free / Total"], hddOnly["HDD Health %"]).Should().Be(("", "", "0.9 / 1.8 TB", "88"));
    }

    [Fact]
    public void An_offline_PC_has_blank_CPU_and_RAM_not_zero_but_keeps_the_last_known_health_and_space()
    {
        var r = Row(Pi111(online: false));

        (r["CPU %"], r["RAM %"]).Should().Be(("", ""));
        (r["SSD Health %"], r["HDD Health %"], r["SSD Free / Total"]).Should().Be(("96", "100", "316.5 / 475.1 GB"));
        r["Last Seen Online"].Should().Be("12.10.2026 15:44", "the last time Nexora saw it");
        r.Values.Should().NotContain("0");
    }

    [Fact]
    public void A_PC_that_was_never_polled_is_blank_everywhere_and_never_zero()
    {
        var never = Pc("never", online: false, [], null, cpu: null, ram: null, user: null) with { LastSeenUtc = null };

        var r = Row(new(never, []));

        r["PC Name"].Should().Be("never");
        foreach (var c in new[] { "Logged-in User", "Last Seen Online", "CPU %", "RAM %", "SSD Free / Total", "SSD Health %", "HDD Free / Total", "HDD Health %" })
            r[c].Should().BeEmpty(c);
    }

    [Fact]
    public void A_zero_that_is_real_stays_zero_for_an_online_PC()
    {
        var online = Row(new(Pc("idle", true, [], null, cpu: 0, ram: 0.2), []));
        (online["CPU %"], online["RAM %"]).Should().Be(("0", "0"));
    }

    // ---------- values

    [Theory]
    [InlineData(12.4, "12")]
    [InlineData(12.5, "13")]
    [InlineData(99.5, "100")]
    [InlineData(0.49, "0")]
    public void CPU_and_RAM_are_whole_numbers_with_no_percent_sign(double value, string expected)
    {
        var r = Row(new(Pc("n", true, [], null, cpu: value, ram: value), []));
        (r["CPU %"], r["RAM %"]).Should().Be((expected, expected));
    }

    [Fact]
    public void Last_Logon_is_dd_MM_yyyy_HH_mm_in_Yerevan_time_on_a_24_hour_clock()
    {
        var late = new DateTime(2026, 12, 31, 21, 5, 0, DateTimeKind.Utc); // 01:05 on New Year's Day in Yerevan
        Row(new(Pc("t", true, [], null, seen: late), []))["Last Seen Online"].Should().Be("01.01.2027 01:05");
        Row(new(Pc("t", true, [], null, seen: new DateTime(2026, 10, 12, 8, 30, 0, DateTimeKind.Utc)), []))["Last Seen Online"].Should().Be("12.10.2026 12:30");
    }

    [Fact]
    public void Sizes_are_written_with_their_unit_in_GB_or_TB_with_one_decimal()
    {
        DriveExport.FormatSpace(0, 486_000_000_000).Should().Be("0.0 / 452.6 GB", "486,000,000,000 bytes is 452.6 GB (1 GB = 2^30 bytes)");
        DriveExport.FormatSpace(316_500_000_000, 510_115_442_688).Should().Be("294.8 / 475.1 GB");
        DriveExport.FormatSpace(512L * GiB, 1024L * GiB).Should().Be("0.5 / 1.0 TB", "a 1 TB drive shows in TB, free and total in the same unit");
        DriveExport.FormatSpace(1000L * GiB, 1023L * GiB).Should().EndWith("GB");
        DriveExport.FormatSpace(250L * GiB, 2000L * GiB).Should().Be("0.2 / 2.0 TB");
    }

    // ---------- several drives of one kind

    [Fact]
    public void Several_drives_of_one_type_are_combined()
    {
        var two = new[] { Drive("SSD-A", "SSD", 90, 500 * GiB), Drive("SSD-B", "SSD", 70, 500 * GiB) };
        var volumes = new[] { Vol("C:", 400, 100), Vol("D:", 450, 300) };   // C: on one SSD, D: on the other
        var layout = DriveExport.Layout(two, volumes);

        DriveExport.Space(layout, "SSD").Should().Be(((long)(100 * GiB) + (long)(300 * GiB), (long)(400 * GiB) + (long)(450 * GiB)));
        DriveExport.Space(layout, "HDD").Should().BeNull();
        var row = Row(new(Pc("two-ssd", true, two, volumes), two));
        row["SSD Health %"].Should().Be("70", "worst of the two");
        row["SSD Free / Total"].Should().Be("400.0 / 850.0 GB");
    }

    [Fact]
    public void Volumes_that_cannot_be_placed_on_a_drive_are_left_out_rather_than_guessed()
    {
        var drives = new[] { Drive("A", "SSD", 90, 500 * GiB), Drive("B", "HDD", 90, 2000 * GiB), Drive("C", "HDD", 90, 1000 * GiB) };
        var layout = DriveExport.Layout(drives, [Vol("C:", 400, 100), Vol("D:", 900, 400)]);

        DriveExport.Space(layout, "SSD").Should().Be(((long)(100 * GiB), (long)(400 * GiB)));
        DriveExport.Space(layout, "HDD").Should().BeNull("D: could be on either HDD");
        layout.Unassigned.Should().ContainSingle().Which.Letter.Should().Be("D:");
    }

    // ---------- text safety

    [Fact]
    public void PC_name_IP_and_user_cannot_start_a_spreadsheet_formula()
    {
        var evil = new ExportRow(Pc("=cmd|' /C calc'!A0", true, [], null, user: " +SUM(1)", ip: "@x"), []);

        var cells = EndpointCsv.Cells(evil, Yerevan);

        cells[0].Should().Be("'=cmd|' /C calc'!A0");
        cells[1].Should().Be("'@x");
        cells[2].Should().Be("' +SUM(1)");
        cells.Where(c => c.Length > 0 && "=+-@\t\r\n ".Contains(c[0])).Should().BeEmpty();
    }

    // ---------- through the API

    private static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>(); var row = new List<string>(); var cur = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted) { if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cur.Append('"'); i++; } else if (c == '"') quoted = false; else cur.Append(c); }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cur.ToString()); cur.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { row.Add(cur.ToString()); cur.Clear(); rows.Add(row.ToArray()); row.Clear(); i++; }
            else cur.Append(c);
        }
        if (cur.Length > 0 || row.Count > 0) { row.Add(cur.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    private async Task<Guid> SeedBoxAsync(string name, Action<ManagedEndpoint> configure, params DiskRaw[] drives)
    {
        var id = await factory.SeedEndpointAsync(name, online: true);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        var e = db.Endpoints.Single(x => x.Id == id);
        e.RamPercent = 50; e.RamTotalBytes = 16 * GiB; e.CpuPercent = 20; e.LastSeenUtc = new DateTime(2026, 10, 13, 6, 5, 0, DateTimeKind.Utc); e.LastPolledUtc = DateTime.UtcNow;
        e.VolumesJson = System.Text.Json.JsonSerializer.Serialize(new[] { new VolumeDto("C:", "NTFS", 400 * GiB, 100 * GiB) });
        configure(e);
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IDiskHealthService>().StoreAsync(id, drives);
        return id;
    }

    private static DiskRaw Raw(string key, string model, string media, string bus, long size) =>
        new(key, model, key, media, bus, size, "Healthy", false, 38, 70, 12000, 3, null, 0, 0, null, null, null, null, []);

    [Fact]
    public async Task The_export_has_the_BOM_the_ten_columns_and_the_same_SSD_HDD_values_as_the_table()
    {
        var mix = await SeedBoxAsync("PC-X-MIX", e => { e.LastUser = "CONTOSO\\Վահե Եղիազարյան"; },
            Raw("WWD2VWAH", "ST1000VX012-3CU10C", "HDD", "SATA", 1_000_202_273_280), Raw("MICRON1", "2500 Micron 512GB", "NVMe", "NVMe", 512_105_932_800));
        var ssd = await SeedBoxAsync("PC-X-SSD", _ => { }, Raw("S1", "CT500BX500SSD1", "SSD", "SATA", 500 * GiB));
        var hdd = await SeedBoxAsync("PC-X-HDD", _ => { }, Raw("H1", "ST2000DM008", "HDD", "SATA", 2000 * GiB));
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        var response = await viewer.PostAsJsonAsync("/api/endpoints/export", new { ids = new[] { mix, ssd, hdd }, sort = new { key = "name", dir = "asc" } });
        var bytes = await response.Content.ReadAsByteArrayAsync();

        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        response.Content.Headers.ContentDisposition!.FileName.Should().MatchRegex(@"^nexora-endpoints-selected-\d{8}-\d{4}\.csv$");
        var rows = Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
        rows[0].Should().Equal(EndpointCsv.Header);
        rows.Should().HaveCount(4).And.OnlyContain(r => r.Length == 10);

        foreach (var (id, line) in new[] { (hdd, rows[1]), (mix, rows[2]), (ssd, rows[3]) })   // PC-X-HDD, PC-X-MIX, PC-X-SSD
        {
            var csv = EndpointCsv.Header.Zip(line).ToDictionary(p => p.First, p => p.Second);
            var table = (await viewer.GetFromJsonAsync<EndpointSummaryDto>($"/api/endpoints/{id}"))!;
            csv["SSD Health %"].Should().Be(table.SsdHealthPercent?.ToString() ?? "", "the export and the table share one helper");
            csv["HDD Health %"].Should().Be(table.HddHealthPercent?.ToString() ?? "");
            csv["RAM %"].Should().Be("50");
            csv["Last Seen Online"].Should().Be("13.10.2026 10:05");
        }

        var mixRow = EndpointCsv.Header.Zip(rows[2]).ToDictionary(p => p.First, p => p.Second);
        mixRow["Logged-in User"].Should().Be("CONTOSO\\Վահե Եղիազարյան");
        mixRow["SSD Free / Total"].Should().Be("100.0 / 400.0 GB", "C: (400 GB) sits on the NVMe, the smallest drive that can hold it");
        mixRow["SSD Health %"].Should().NotBeEmpty();
        mixRow["HDD Health %"].Should().NotBeEmpty();
        EndpointCsv.Header.Zip(rows[3]).ToDictionary(p => p.First, p => p.Second)["HDD Health %"].Should().BeEmpty("the SSD-only PC has no HDD");
        EndpointCsv.Header.Zip(rows[1]).ToDictionary(p => p.First, p => p.Second)["SSD Free / Total"].Should().BeEmpty("the HDD-only PC has no SSD");
    }
}
