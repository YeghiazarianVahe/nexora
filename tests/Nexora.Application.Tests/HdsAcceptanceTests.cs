using System.Text.Json;
using FluentAssertions;
using Nexora.Application.Services;
using Nexora.Infrastructure.Wmi;

namespace Nexora.Application.Tests;

// ---------- fixture loader (tests/Nexora.Application.Tests/Fixtures/Smart/*.json, written by scripts/capture-smart.ps1) ----------

internal sealed class SmartFixture
{
    public int Schema { get; set; }
    public string Host { get; set; } = "";
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string BusType { get; set; } = "";
    public string MediaType { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool IsNvme { get; set; }
    public AtaPart Ata { get; set; } = new();
    public NvmePart Nvme { get; set; } = new();
    public WindowsPart Windows { get; set; } = new();
    public List<string> Errors { get; set; } = [];

    internal sealed class AtaPart
    {
        public bool Available { get; set; }
        public string? VendorSpecificHex { get; set; }
        public string? ThresholdsHex { get; set; }
    }

    internal sealed class NvmePart
    {
        public bool Available { get; set; }
        public string? SmartLogHex { get; set; }
        public string? IdentifyControllerHex { get; set; }
    }

    internal sealed class WindowsPart
    {
        public double? TemperatureC { get; set; }
        public double? Wear { get; set; }
        public double? ReadErrorsTotal { get; set; }
        public double? WriteErrorsTotal { get; set; }
        public double? PowerOnHours { get; set; }
        public string? HealthStatus { get; set; }
        public string? OperationalStatus { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The Fixtures/Smart folder next to the test project's source (found from the build output, so files added later are picked up without a rebuild).</summary>
    public static string Directory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Nexora.Application.Tests.csproj")))
                return Path.Combine(dir.FullName, "Fixtures", "Smart");
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", "Smart");
    }

    public static IReadOnlyList<string> AllFiles() =>
        System.IO.Directory.Exists(Directory()) ? System.IO.Directory.GetFiles(Directory(), "*.json").Order(StringComparer.Ordinal).ToList() : [];

    public static SmartFixture Load(string path) =>
        JsonSerializer.Deserialize<SmartFixture>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path} is empty.");

    public static byte[] Hex(string? hex)
    {
        var text = (hex ?? "").Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        var clean = new string(text.Where(Uri.IsHexDigit).ToArray());
        return clean.Length == 0 || clean.Length % 2 != 0 ? [] : Convert.FromHexString(clean);
    }

    /// <summary>The drive as the collector would hand it to the evaluator, built only from the captured raw bytes and Windows values.</summary>
    public DiskRaw ToDiskRaw()
    {
        var attrs = Ata.Available ? SmartParser.ParseTable(Hex(Ata.VendorSpecificHex), Hex(Ata.ThresholdsHex)) : [];
        var log = Nvme.Available ? NvmeSmart.ParseHealthLog(Hex(Nvme.SmartLogHex)) : null;
        var identify = Hex(Nvme.IdentifyControllerHex) is { Length: > 0 } id ? NvmeSmart.ParseIdentify(id) : null;

        var media = IsNvme ? "NVMe"
            : MediaType.Equals("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD"
            : MediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase) ? "HDD"
            : SmartParser.LifeLeft(attrs, isSsd: true, model: Model) is not null && attrs.All(a => a.Id != 3) ? "SSD" : "HDD";   // unlabelled: no spin-up time attribute and a wear attribute means solid state

        int? temp = log is { TemperatureC: > 0 and < 150 } ? log.TemperatureC
            : Windows.TemperatureC is > 0 ? (int)Windows.TemperatureC.Value
            : SmartParser.Temperature(attrs);
        long? poh = Windows.PowerOnHours is > 0 ? (long)Windows.PowerOnHours.Value : log is { PowerOnHours: > 0 } ? log.PowerOnHours : SmartParser.Raw(attrs, 9) is { } r ? r & 0xFFFFFFFF : null;
        var health = Windows.HealthStatus ?? "Unknown";
        if (log is { CriticalWarning: not 0 } && health != "Unhealthy") health = "Warning";

        return new DiskRaw(
            Key: Serial, Model: Model, Serial: Serial, MediaType: media, BusType: BusType, SizeBytes: SizeBytes, HealthStatus: health, PredictFailure: false,
            TemperatureC: temp, MaxTemperatureC: null, PowerOnHours: poh,
            WearPercent: media == "HDD" ? null : Windows.Wear is { } w ? (int)w : null,
            LifeLeftPercent: log?.LifeLeftPercent ?? SmartParser.LifeLeft(attrs, media is "SSD" or "NVMe", Model),
            ReadErrors: Windows.ReadErrorsTotal is { } re ? (long)re : null, WriteErrors: Windows.WriteErrorsTotal is { } we ? (long)we : null,
            Reallocated: SmartParser.Raw(attrs, 5), Pending: SmartParser.Raw(attrs, 197), OfflineUncorrectable: SmartParser.Raw(attrs, 198), CrcErrors: SmartParser.Raw(attrs, 199),
            Attributes: attrs,
            Nvme: log is null ? null : new NvmeInfo(log.CriticalWarning, log.PercentageUsed, log.AvailableSpare, log.SpareThreshold, log.MediaErrors),
            DriveTempWarnC: identify?.WarningTempC, DriveTempCritC: identify?.CriticalTempC);
    }
}

/// <summary>
/// A fact that runs only when its captured fixture exists. A missing file shows up as SKIPPED with an explicit message in the xUnit summary,
/// never as a silent pass; the release is not done until all four acceptance cases run and pass.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class FixtureFactAttribute : FactAttribute
{
    public FixtureFactAttribute(string fileName)
    {
        var path = Path.Combine(SmartFixture.Directory(), fileName);
        if (!File.Exists(path)) Skip = $"fixture not captured: Fixtures/Smart/{fileName} is missing (run scripts/capture-smart.ps1 on the reference PC).";
    }
}

/// <summary>Skipped (with a message) while no fixture at all has been captured.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class AnyFixtureFactAttribute : FactAttribute
{
    public AnyFixtureFactAttribute()
    {
        if (SmartFixture.AllFiles().Count == 0) Skip = "fixture not captured: Fixtures/Smart contains no *.json files yet.";
    }
}

/// <summary>
/// Acceptance of the HDS-compatible model (ADR-0005) against drives captured from the reference PCs. The expected values are what the service
/// desk reads in Hard Disk Sentinel; the model is calibrated to them, it does not claim to be identical to HDS.
/// wks-111: Micron 2500 NVMe 96, Seagate ST1000VX012 100. wks-44: SSD 65, HDD 100.
/// </summary>
public sealed class HdsAcceptanceTests
{
    private static (DiskHealthResult Result, SmartFixture Fixture) Run(string fileName)
    {
        var f = SmartFixture.Load(Path.Combine(SmartFixture.Directory(), fileName));
        return (DiskHealthEvaluator.Evaluate(f.ToDiskRaw()), f);
    }

    private static void ExpectScore(string fileName, int expected, int tolerance)
    {
        var (r, f) = Run(fileName);
        var detail = $"{f.Host} {f.Model} ({f.Serial}): {string.Join("; ", r.Reasons!.Select(x => $"{x.Code} {x.Delta}"))}";

        r.HealthPercent.Should().NotBeNull($"the fixture has readable data. {detail}");
        r.HealthPercent!.Value.Should().BeInRange(expected - tolerance, expected + tolerance, detail);
        (100 + r.Reasons!.Sum(x => x.Delta)).Should().Be(r.HealthPercent.Value, "the reasons account for every point");
    }

    [FixtureFact("wks-111-micron-2500-nvme.json")]
    public void ab_pi111_Micron_2500_512GB_NVMe_scores_96() => ExpectScore("wks-111-micron-2500-nvme.json", 96, 2);

    [FixtureFact("wks-111-seagate-st1000vx012-hdd.json")]
    public void ab_pi111_Seagate_ST1000VX012_HDD_scores_100() => ExpectScore("wks-111-seagate-st1000vx012-hdd.json", 100, 1);

    [FixtureFact("wks-44-ssd.json")]
    public void ab_pi44_SSD_scores_65() => ExpectScore("wks-44-ssd.json", 65, 3);

    [FixtureFact("wks-44-hdd.json")]
    public void ab_pi44_HDD_scores_100() => ExpectScore("wks-44-hdd.json", 100, 1);

    [FixtureFact("wks-111-micron-2500-nvme.json")]
    public void The_Micron_fixture_decodes_its_own_temperature_limits_and_current_temperature()
    {
        var (r, f) = Run("wks-111-micron-2500-nvme.json");

        f.Nvme.Available.Should().BeTrue();
        r.Temperature!.CurrentC.Should().BeInRange(10, 90);
        if (SmartFixture.Hex(f.Nvme.IdentifyControllerHex).Length > 0)
        {
            var id = NvmeSmart.ParseIdentify(SmartFixture.Hex(f.Nvme.IdentifyControllerHex))!;
            if (id.WarningTempC is not null && id.CriticalTempC is not null && id.WarningTempC < id.CriticalTempC)
                (r.Temperature.WarnC, r.Temperature.CritC, r.Temperature.Source).Should().Be((id.WarningTempC, id.CriticalTempC, "drive"));
        }
    }

    [AnyFixtureFact]
    public void Every_captured_fixture_decodes_and_its_reasons_add_up()
    {
        foreach (var path in SmartFixture.AllFiles())
        {
            var f = SmartFixture.Load(path);
            f.Schema.Should().Be(1, Path.GetFileName(path));
            var r = DiskHealthEvaluator.Evaluate(f.ToDiskRaw());
            if (r.HealthPercent is { } h)
                (100 + r.Reasons!.Sum(x => x.Delta)).Should().Be(h, Path.GetFileName(path));
            (f.Ata.Available || f.Nvme.Available || f.Errors.Count > 0).Should().BeTrue($"{Path.GetFileName(path)} has either data or the reason it has none");
        }
    }

    // ---------- the loader itself is covered without any captured file

    [Fact]
    public void Hex_text_is_decoded_leniently()
    {
        SmartFixture.Hex("0A ff-10").Should().Equal(0x0A, 0xFF, 0x10);
        SmartFixture.Hex("").Should().BeEmpty();
        SmartFixture.Hex("ABC").Should().BeEmpty("an odd number of digits is not bytes");
    }

    [Fact]
    public void A_fixture_is_turned_into_a_drive_from_raw_bytes_only()
    {
        var vendor = new byte[512];
        vendor[2] = 5; vendor[5] = 100; vendor[6] = 100;                     // reallocated sectors, raw 0
        vendor[14] = 197; vendor[17] = 100; vendor[18] = 100; vendor[19] = 2; // pending sectors, raw 2
        var json = JsonSerializer.Serialize(new
        {
            schema = 1, host = "h", model = "Synthetic HDD", serial = "SYN1", busType = "SATA", mediaType = "HDD", isNvme = false,
            ata = new { available = true, vendorSpecificHex = Convert.ToHexString(vendor), thresholdsHex = "" },
            nvme = new { available = false, smartLogHex = "", identifyControllerHex = "" },
            windows = new { temperatureC = 36, healthStatus = "Healthy" }, errors = Array.Empty<string>(),
        });
        var path = Path.Combine(Path.GetTempPath(), $"nexora-fixture-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            var raw = SmartFixture.Load(path).ToDiskRaw();

            raw.Pending.Should().Be(2);
            raw.TemperatureC.Should().Be(36);
            raw.MediaType.Should().Be("HDD");
            DiskHealthEvaluator.Evaluate(raw).HealthPercent.Should().Be(100 - 19, "two pending sectors cost 19 points");
        }
        finally { File.Delete(path); }
    }
}
