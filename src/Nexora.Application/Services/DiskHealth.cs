using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nexora.Domain;

namespace Nexora.Application.Services;

// ---------- raw telemetry as collected from an endpoint ----------

public sealed record SmartAttribute(int Id, string Name, int Current, int Worst, long Raw, int Threshold = 0);

/// <summary>Everything a collector could learn about one physical drive. Any field may be unknown.</summary>
public sealed record DiskRaw(
    string Key, string Model, string? Serial, string MediaType, string? BusType, long SizeBytes,
    string? HealthStatus, bool PredictFailure,
    int? TemperatureC, int? MaxTemperatureC, long? PowerOnHours, int? WearPercent, int? LifeLeftPercent,
    long? ReadErrors, long? WriteErrors,
    long? Reallocated, long? Pending, long? OfflineUncorrectable, long? CrcErrors,
    IReadOnlyList<SmartAttribute> Attributes,
    DiskMapping? Mapping = null,
    NvmeInfo? Nvme = null,
    /// <summary>The drive's own temperature limits in Celsius (NVMe Identify Controller WCTEMP / CCTEMP); null when it does not report them.</summary>
    int? DriveTempWarnC = null, int? DriveTempCritC = null,
    /// <summary>Lifetime maximum temperature when the collector has one from the drive; null otherwise (never invented).</summary>
    int? LifetimeMaxTempC = null,
    DateTime? SourceSampledUtc = null, bool SourceStale = false, string? SourceNote = null);

/// <summary>The NVMe health log fields the scorer needs (read by the existing collector; nothing new is collected).</summary>
public sealed record NvmeInfo(int CriticalWarning, int PercentageUsed, int AvailableSpare, int SpareThreshold, long MediaErrors);

/// <summary>How a drive was classified and joined during one scan; stored for the fleet audit.</summary>
public sealed record DiskMapping(
    int? BusTypeCode, int? MediaTypeCode, long? SpindleSpeed, string ClassSource, string PhysicalMatch, string HealthSource,
    bool SmartMatched, int HostSmartRecords, int HostDriveCount);


public static class DiskStatus
{
    public const string Ok = "OK";
    public const string Warning = "Warning";
    public const string Critical = "Critical";
    public const string Unknown = "Unknown";

    /// <summary>Health at or above this is OK; below it is a Warning. One definition for the evaluator, the API and every screen.</summary>
    public const int OkFrom = 85;
    /// <summary>Health below this is Critical.</summary>
    public const int CriticalBelow = 50;

    public static int Rank(string? s) => s switch { Critical => 3, Warning => 2, Ok => 1, _ => 0 };
}

/// <summary>Decodes the 512-byte SMART vendor block (MSStorageDriver_ATAPISmartData.VendorSpecific).</summary>
public static class SmartParser
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "Read error rate", [3] = "Spin-up time", [4] = "Start/stop count", [5] = "Reallocated sectors",
        [7] = "Seek error rate", [9] = "Power-on hours", [10] = "Spin retry count", [12] = "Power cycle count",
        [168] = "SATA PHY error count", [170] = "Available reserved space", [171] = "Program fail count",
        [172] = "Erase fail count", [173] = "Wear leveling count", [174] = "Unexpected power loss",
        [177] = "Wear range delta", [181] = "Program fail count", [182] = "Erase fail count",
        [183] = "SATA downshift errors", [184] = "End-to-end error", [187] = "Reported uncorrectable errors",
        [188] = "Command timeout", [190] = "Airflow temperature", [191] = "G-sense error rate",
        [192] = "Power-off retract count", [193] = "Load/unload cycles", [194] = "Temperature",
        [196] = "Reallocation events", [197] = "Current pending sectors", [198] = "Offline uncorrectable",
        [199] = "UDMA CRC errors", [200] = "Write error rate", [231] = "SSD life left",
        [232] = "Available reserved space", [233] = "Media wearout indicator", [241] = "Total host writes",
        [242] = "Total host reads",
    };

    /// <summary>Adds the failure thresholds from MSStorageDriver_FailurePredictThresholds.</summary>
    public static IReadOnlyList<SmartAttribute> Parse(byte[]? block, byte[]? thresholdBlock) =>
        ParseTable(block ?? [], thresholdBlock ?? []);

    public static IReadOnlyList<SmartAttribute> Parse(byte[]? block) => ParseTable(block ?? []);

    /// <summary>
    /// Pure decoder of the ATA SMART attribute table: the 512-byte vendor-specific block (revision word, then 30 entries of 12 bytes: id, flags,
    /// current, worst, 6-byte raw, reserved) and, optionally, the 512-byte thresholds block (same layout: id, then threshold). No I/O.
    /// </summary>
    public static IReadOnlyList<SmartAttribute> ParseTable(ReadOnlySpan<byte> vendorSpecific, ReadOnlySpan<byte> thresholds = default)
    {
        var list = new List<SmartAttribute>();
        if (vendorSpecific.Length < 14) return list;

        var limits = new Dictionary<int, int>();
        if (thresholds.Length >= 14)
        {
            for (var i = 0; i < 30; i++)
            {
                var o = 2 + i * 12;
                if (o + 2 > thresholds.Length) break;
                if (thresholds[o] != 0) limits[thresholds[o]] = thresholds[o + 1];
            }
        }

        for (var i = 0; i < 30; i++)
        {
            var o = 2 + i * 12;
            if (o + 12 > vendorSpecific.Length) break;
            var id = vendorSpecific[o];
            if (id == 0) continue;
            long raw = 0;
            for (var b = 5; b >= 0; b--) raw = (raw << 8) | vendorSpecific[o + 5 + b];
            list.Add(new SmartAttribute(id, Names.GetValueOrDefault(id, $"Attribute {id}"), vendorSpecific[o + 3], vendorSpecific[o + 4], raw, limits.GetValueOrDefault(id)));
        }
        return list;
    }

    public static long? Raw(IEnumerable<SmartAttribute> attrs, int id) =>
        attrs.FirstOrDefault(a => a.Id == id)?.Raw;

    public static int? Temperature(IEnumerable<SmartAttribute> attrs)
    {
        var a = attrs.FirstOrDefault(x => x.Id == 194) ?? attrs.FirstOrDefault(x => x.Id == 190);
        if (a is null) return null;
        var t = (int)(a.Raw & 0xFF);
        return t is > 0 and < 120 ? t : null;
    }

    /// <summary>
    /// The lifetime maximum temperature from SMART 194 (or 190) when its raw value carries a plausible one: byte 0 is the current temperature and
    /// the minimum/maximum follow, either packed in the second word (Seagate) or one per word (others). Anything inconsistent yields null.
    /// </summary>
    public static int? LifetimeMaxTemperature(IEnumerable<SmartAttribute> attrs)
    {
        var a = attrs.FirstOrDefault(x => x.Id == 194) ?? attrs.FirstOrDefault(x => x.Id == 190);
        if (a is null) return null;
        var current = (int)(a.Raw & 0xFF);
        var word1 = (int)((a.Raw >> 16) & 0xFFFF);
        var word2 = (int)((a.Raw >> 32) & 0xFFFF);
        if (current is <= 0 or >= 120) return null;
        var (lo, hi) = word2 == 0 ? (word1 & 0xFF, (word1 >> 8) & 0xFF) : (word1, word2);
        if (hi is <= 0 or >= 120 || hi < current || lo > current) return null;
        return hi;
    }

    /// <summary>Remaining SSD life (0-100) from the first life-related normalized attribute present.</summary>
    public static int? LifeLeft(IEnumerable<SmartAttribute> attrs, bool isSsd = false, string? model = null, bool adata169 = true) =>
        LifeLeftWithSource(attrs, isSsd, model, adata169)?.Value;

    /// <summary>Where a remaining-life figure came from: the value and the SMART attribute that carried it.</summary>
    public sealed record LifeSource(int Value, int AttributeId);

    /// <summary>Same as <see cref="LifeLeft"/>, plus the SMART attribute used (231, 233, 202, 169, 177 or 173, decided by vendor and drive type).</summary>
    public static LifeSource? LifeLeftWithSource(IEnumerable<SmartAttribute> attrs, bool isSsd = false, string? model = null, bool adata169 = true)
    {
        // ADATA (Silicon Motion) SATA SSDs keep "remaining life" in the RAW value of attribute 169 and leave its normalized value at 100.
        if (adata169 && isSsd && (model ?? "").StartsWith("ADATA", StringComparison.OrdinalIgnoreCase)
            && attrs.FirstOrDefault(x => x.Id == 169) is { Raw: >= 0 and <= 100 } adata)
            return new LifeSource((int)adata.Raw, 169);

        // 202 (Crucial/Micron "percent lifetime remaining") and 173 (wear leveling) mean other things on hard drives.
        var vendor = model ?? "";
        var micron = vendor.StartsWith("Crucial", StringComparison.OrdinalIgnoreCase) || vendor.StartsWith("Micron", StringComparison.OrdinalIgnoreCase)
            || vendor.StartsWith("CT", StringComparison.OrdinalIgnoreCase);
        // 177/173 are erase/wear counters on many vendors, not a percentage. Never treat an unknown vendor's counter as life.
        var ids = new List<int> { 231, 233 };
        if (isSsd && micron) ids.Add(202);
        if (isSsd && vendor.StartsWith("Samsung", StringComparison.OrdinalIgnoreCase)) ids.Add(177);
        foreach (var id in ids)
        {
            var a = attrs.FirstOrDefault(x => x.Id == id);
            if (a is { Current: >= 0 and <= 100 }) return new LifeSource(a.Current, id);
        }
        return null;
    }
}

// ---------- persistence-facing service ----------

public sealed record TempPoint(DateTime TimeUtc, int? TemperatureC);

public sealed record DiskHealthDto(
    string DiskKey, string Model, string? Serial, string MediaType, string? BusType, long SizeBytes,
    int? HealthPercent, string Status, int? TemperatureC, int? MaxTemperatureC, long? PowerOnHours,
    int? WearPercent, long? ReadErrors, long? WriteErrors, long? ReallocatedSectors, long? PendingSectors,
    bool PredictFailure, int? EstimatedDaysLeft, string Summary, IReadOnlyList<SmartAttribute> Attributes,
    DateTime UpdatedUtc, IReadOnlyList<TempPoint> TemperatureHistory,
    /// <summary>SSD (SSD and NVMe), HDD, or Unknown only when the drive cannot be told apart. See <see cref="DriveClassifier"/>.</summary>
    string Type = "Unknown",
    /// <summary>OK, Warning or Critical for the temperature alone; never part of the health percentage.</summary>
    string? TemperatureStatus = null,
    /// <summary>The health breakdown: stable code, text and points (negative = deducted). The deltas add up to HealthPercent - 100.</summary>
    IReadOnlyList<HealthReason>? Reasons = null,
    /// <summary>Temperature on its own: current, the limits used and where they came from ("drive" or "default"), lifetime maximum, status.</summary>
    TemperatureInfo? Temperature = null,
    /// <summary>A cable / connection warning from SMART 199 (UDMA CRC); never part of the health score. Null when there is none.</summary>
    string? InterfaceWarning = null,
    string Confidence = "Low", string ValueKind = "unavailable", bool Stale = false,
    int? LastKnownTemperatureC = null, string? Firmware = null, int? HealthModelVersion = null,
    int CurrentHealthModelVersion = DiskHealthEvaluator.ScoreVersion, DateTime? ScanCompletedUtc = null, string? SourceNote = null);

/// <summary>One drive as the summary needs it: how it is classified and how healthy it is.</summary>
public sealed record DriveHealthInput(string MediaType, string? BusType, int? HealthPercent);

/// <summary>
/// SSD or HDD, decided once and shared by the collector, the detail view, the table and the CSV export. A drive is never forced into
/// a class it does not clearly belong to: when nothing says, it is <c>Unknown</c> and counts for neither the SSD nor the HDD figure.
/// </summary>
public static class DriveClassifier
{
    public const string Ssd = "SSD", Hdd = "HDD", Unknown = "Unknown";
    public const int BusTypeNvme = 17;
    public const int MediaTypeHdd = 3, MediaTypeSsd = 4;
    /// <summary>MSFT_PhysicalDisk.SpindleSpeed reports this (0xFFFFFFFF) when the rotation rate is unknown.</summary>
    public const long SpindleUnknown = 4_294_967_295;

    /// <summary>
    /// From what the drive reports, in priority order: NVMe bus is always SSD; then MSFT_PhysicalDisk.MediaType (4 SSD, 3 HDD);
    /// then the rotation rate (0 is SSD, above 0 is HDD); otherwise Unknown. The text "Fixed hard disk media" says nothing and is never consulted.
    /// </summary>
    public static string FromFacts(int? busType, int? mediaType, long? spindleSpeed) => FromFactsWithSource(busType, mediaType, spindleSpeed).Class;

    /// <summary>Same decision, plus the signal that made it ("bus", "media", "spindle" or "none") for the audit.</summary>
    public static (string Class, string Source) FromFactsWithSource(int? busType, int? mediaType, long? spindleSpeed)
    {
        if (busType == BusTypeNvme) return (Ssd, "bus");
        if (mediaType == MediaTypeSsd) return (Ssd, "media");
        if (mediaType == MediaTypeHdd) return (Hdd, "media");
        if (spindleSpeed == 0) return (Ssd, "spindle");
        if (spindleSpeed is > 0 and not SpindleUnknown) return (Hdd, "spindle");
        return (Unknown, "none");
    }

    /// <summary>
    /// From the stored scan: the collector stored "NVMe", "SSD" or "HDD" (decided by <see cref="FromFacts"/>) or "Unknown", plus the bus.
    /// The bus is checked again so an NVMe drive is an SSD whatever was stored.
    /// </summary>
    public static string Classify(string? mediaType, string? busType)
    {
        if ((busType ?? "").Equals("NVMe", StringComparison.OrdinalIgnoreCase)) return Ssd;
        var media = (mediaType ?? "").Trim();
        if (media.Equals("SSD", StringComparison.OrdinalIgnoreCase) || media.Equals("NVMe", StringComparison.OrdinalIgnoreCase)) return Ssd;
        if (media.Equals("HDD", StringComparison.OrdinalIgnoreCase)) return Hdd;
        return Unknown;
    }

    /// <summary>The worst health among the SSD/NVMe drives and among the HDDs. Null when that kind is absent or none of its drives has a health value.</summary>
    public static (int? Ssd, int? Hdd) Worst(IEnumerable<DriveHealthInput> drives)
    {
        int? ssd = null, hdd = null;
        foreach (var d in drives)
        {
            if (d.HealthPercent is not { } h) continue;
            switch (Classify(d.MediaType, d.BusType))
            {
                case Ssd: ssd = ssd is { } s ? Math.Min(s, h) : h; break;
                case Hdd: hdd = hdd is { } p ? Math.Min(p, h) : h; break;
            }
        }
        return (ssd, hdd);
    }
}

/// <summary>The identifiers a drive can be told apart by. Matching never uses enumeration order or array position.</summary>
public sealed record DriveKeys(string? Serial, string? DeviceId, string? PnpId);

/// <summary>
/// Pairs records about the same physical drive (storage properties, SMART data, reliability counters) by a stable identity, in this order:
/// serial number, then the PnP / unique id, then the disk number. A level that matches more than one record is ambiguous and is skipped;
/// when no level matches, the result is null and the drive's data is shown as N/A, never borrowed from another drive.
/// </summary>
public static partial class DriveMatching
{
    [GeneratedRegex(@"_\d+$")]
    private static partial Regex InstanceSuffix();

    public static string? NormalizeSerial(string? serial)
    {
        var s = (serial ?? "").Trim().TrimEnd('.').Trim();
        return s.Length == 0 ? null : s.ToUpperInvariant();
    }

    /// <summary>SMART records are named after the PnP id plus an instance suffix ("..._0").</summary>
    public static string? NormalizePnp(string? instanceOrPnp)
    {
        var s = InstanceSuffix().Replace((instanceOrPnp ?? "").Trim(), "");
        return s.Length == 0 ? null : s.ToUpperInvariant();
    }

    /// <summary>Which level matched, for the audit log.</summary>
    public enum Level { None, Serial, PnpId, DiskNumber }

    public static (T? Match, Level Level) Find<T>(IEnumerable<T> candidates, DriveKeys drive, Func<T, DriveKeys> keysOf) where T : class
    {
        var list = candidates.Select(c => (Item: c, Keys: keysOf(c))).ToList();

        var serial = NormalizeSerial(drive.Serial);
        if (serial is not null && Single(list.Where(c => NormalizeSerial(c.Keys.Serial) == serial).ToList()) is { } bySerial) return (bySerial, Level.Serial);

        var pnp = NormalizePnp(drive.PnpId);
        if (pnp is not null && Single(list.Where(c => NormalizePnp(c.Keys.PnpId) == pnp).ToList()) is { } byPnp) return (byPnp, Level.PnpId);

        var number = (drive.DeviceId ?? "").Trim();
        if (number.Length > 0 && Single(list.Where(c => (c.Keys.DeviceId ?? "").Trim() == number).ToList()) is { } byNumber) return (byNumber, Level.DiskNumber);

        return (null, Level.None);

        static T? Single(List<(T Item, DriveKeys Keys)> hits) => hits.Count == 1 ? hits[0].Item : null;
    }
}

public static class HistoryRange
{
    public static TimeSpan Parse(string? range) => range switch
    {
        "1h" => TimeSpan.FromHours(1),
        "6h" => TimeSpan.FromHours(6),
        "7d" => TimeSpan.FromDays(7),
        "30d" => TimeSpan.FromDays(30),
        _ => TimeSpan.FromHours(24),
    };

    /// <summary>Averages values into at most <paramref name="maxPoints"/> evenly spaced buckets.</summary>
    public static List<(DateTime Time, double? Value)> Bucket(
        IEnumerable<(DateTime Time, double? Value)> rows, DateTime from, DateTime to, int maxPoints = 240)
    {
        var span = to - from;
        var bucket = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond * 30, span.Ticks / maxPoints));
        return rows
            .GroupBy(r => (r.Time - from).Ticks / bucket.Ticks)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var vals = g.Where(x => x.Value.HasValue && double.IsFinite(x.Value.Value)).Select(x => x.Value!.Value).ToList();
                return (g.Max(x => x.Time), vals.Count == 0 ? (double?)null : Math.Round(vals.Average(), 1));
            })
            .ToList();
    }
}

public interface IDiskHealthService
{
    Task<IReadOnlyList<DiskHealthDto>> GetAsync(Guid endpointId, string? range, CancellationToken ct = default);
    Task<Result<IReadOnlyList<DiskHealthDto>>> RefreshAsync(Guid endpointId, string? range, CancellationToken ct = default);
    /// <summary>Evaluates and stores a collection result. Used by the monitor and by Refresh.</summary>
    Task StoreAsync(Guid endpointId, IReadOnlyList<DiskRaw> disks, CancellationToken ct = default);
    Task<bool> StoreIfCurrentAsync(Guid endpointId, string host, uint version, IReadOnlyList<DiskRaw> disks, CancellationToken ct = default);
    /// <summary>
    /// A drive scan that could not be read or stored: remember why (shown on the PC's page) and push the next attempt back by the normal
    /// interval, instead of repeating the full scan on every poll cycle.
    /// </summary>
    Task MarkScanFailedAsync(Guid endpointId, string reason, CancellationToken ct = default);
}

public sealed class DiskHealthService(INexoraDb db, IEndpointCollector collector, ISystemClock clock, Microsoft.Extensions.Options.IOptions<HealthWeights>? weights = null, ISettingsService? settings = null, Microsoft.Extensions.Logging.ILogger<DiskHealthService>? logger = null) : IDiskHealthService
{
    public async Task<IReadOnlyList<DiskHealthDto>> GetAsync(Guid endpointId, string? range, CancellationToken ct = default)
    {
        var snapshots = await db.DiskHealth.AsNoTracking().Where(s => s.EndpointId == endpointId)
            .OrderBy(s => s.DiskKey).ToListAsync(ct);
        var limits = await LimitsAsync(ct);
        var uncertain = await db.Endpoints.AsNoTracking().Where(e => e.Id == endpointId)
            .Select(e => !e.Online || e.DiskScanError != null).FirstOrDefaultAsync(ct);
        var from = clock.UtcNow - HistoryRange.Parse(range);
        var temps = await db.DiskTemps.AsNoTracking()
            .Where(t => t.EndpointId == endpointId && t.TimestampUtc >= from)
            .OrderBy(t => t.TimestampUtc).ToListAsync(ct);

        return snapshots.Select(s =>
        {
            var history = HistoryRange.Bucket(
                temps.Where(t => t.DiskKey == s.DiskKey).Select(t => (t.TimestampUtc, t.TemperatureC.HasValue ? (double?)t.TemperatureC.Value : null)),
                from, clock.UtcNow, 200)
                .Select(p => new TempPoint(p.Time, p.Value.HasValue ? (int)Math.Round(p.Value.Value) : null)).ToList();
            var lastGood = temps.LastOrDefault(t => t.DiskKey == s.DiskKey && DriveTemperature.Valid(t.TemperatureC) is not null)?.TemperatureC;
            return ToDto(s, history, limits, uncertain, lastGood);
        }).ToList();
    }

    /// <summary>The Admin-set default temperature limits per drive class (the built-in defaults when settings are unavailable).</summary>
    private async Task<DriveTempLimits> LimitsAsync(CancellationToken ct) =>
        settings is null ? DriveTempLimits.Default : (await settings.GetAsync(ct)).ToTempLimits();

    public async Task<Result<IReadOnlyList<DiskHealthDto>>> RefreshAsync(Guid endpointId, string? range, CancellationToken ct = default)
    {
        var e = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == endpointId, ct);
        if (e is null) return Result<IReadOnlyList<DiskHealthDto>>.Fail(ErrorKind.NotFound, "Endpoint not found.");
        try
        {
            var samples = await collector.CollectDisksAsync(e.Hostname, ct);
            if (!await StoreIfCurrentAsync(endpointId, e.Hostname, e.RowVersion, samples, ct))
                return Result<IReadOnlyList<DiskHealthDto>>.Fail(ErrorKind.Conflict, "The workstation changed while disks were being read. Refresh the workstation and retry.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var correlation = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DiskHealthService>.Instance,
                ex, "Disk refresh failed for endpoint {EndpointId}. CorrelationId {CorrelationId}", endpointId, correlation);
            return Result<IReadOnlyList<DiskHealthDto>>.Fail(ErrorKind.Unavailable, $"Disk data is temporarily unavailable. Check workstation connectivity and permissions, then retry. Reference: {correlation}");
        }
        return Result<IReadOnlyList<DiskHealthDto>>.Success(await GetAsync(endpointId, range, ct));
    }

    public async Task<bool> StoreIfCurrentAsync(Guid endpointId, string host, uint version, IReadOnlyList<DiskRaw> disks, CancellationToken ct = default)
    {
        // No connection or lock is retained during remote collection. PostgreSQL xmin
        // detects ABA and deletion/recreation; the row lock closes check-then-write races.
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var current = db.Database.IsRelational()
            ? await db.Endpoints.FromSqlInterpolated($"SELECT *, xmin FROM endpoints WHERE \"Id\" = {endpointId} FOR UPDATE").AsNoTracking().FirstOrDefaultAsync(ct)
            : await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == endpointId, ct);
        if (current is null || current.RowVersion != version || !string.Equals(current.Hostname, host, StringComparison.OrdinalIgnoreCase)) return false;
        await StoreAsync(endpointId, disks, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// A drive is identified by its key (its serial, or its disk number when it has none). Two drives of one PC that report the same key would
    /// collide on the unique index and lose the whole result, so the second one becomes "KEY#2", the third "KEY#3": stable between scans because
    /// the drives come in disk-number order.
    /// </summary>
    public static IReadOnlyList<(DiskRaw Raw, string Key)> WithUniqueKeys(IReadOnlyList<DiskRaw> disks)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<(DiskRaw, string)>(disks.Count);
        foreach (var d in disks)
        {
            var n = seen.GetValueOrDefault(d.Key) + 1;
            seen[d.Key] = n;
            result.Add((d, n == 1 ? d.Key : $"{d.Key}#{n}"));
        }
        return result;
    }

    /// <summary>True when the failure is a unique-index violation (Postgres 23505), the shape of two stores racing for the same new drive.</summary>
    public static bool IsUniqueViolation(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e.GetType().GetProperty("SqlState")?.GetValue(e) as string == "23505") return true;
            if (e.InnerException is null) break;
        }
        return false;
    }

    public async Task StoreAsync(Guid endpointId, IReadOnlyList<DiskRaw> disks, CancellationToken ct = default)
    {
        try { await StoreOnceAsync(endpointId, disks, ct); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another store (a "Read now" at the same moment) created the same drive first: forget our half-saved state, read what is there now, and upsert once more.
            db.ChangeTracker.Clear();
            await StoreOnceAsync(endpointId, disks, ct);
        }
    }

    private async Task StoreOnceAsync(Guid endpointId, IReadOnlyList<DiskRaw> disks, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var existing = await db.DiskHealth.Where(s => s.EndpointId == endpointId).ToListAsync(ct);
        var limits = await LimitsAsync(ct);

        foreach (var (raw, key) in WithUniqueKeys(disks))
        {
            var result = DiskHealthEvaluator.Evaluate(raw, weights?.Value, limits);
            var snap = existing.FirstOrDefault(s => s.DiskKey == key);
            var repeatedCachedSample = raw.SourceSampledUtc is { } time && snap?.UpdatedUtc == time;
            if (snap is not null && raw.SourceStale && result.HealthPercent is null)
            {
                List<HealthReason> retained;
                try { retained=JsonSerializer.Deserialize<List<HealthReason>>(snap.ReasonsJson ?? "[]") ?? []; }
                catch (JsonException) { retained=[]; }
                retained.RemoveAll(r => r.Code is "source-stale" or "scan-completed-utc");
                retained.Add(new HealthReason("source-stale",raw.SourceNote ?? "Collector evidence is unavailable; last-known health retained.",0));
                retained.Add(new HealthReason("scan-completed-utc",now.ToString("O"),0));
                snap.ReasonsJson=JsonSerializer.Serialize(retained);
                continue;
            }
            if (snap is null)
            {
                snap = new DiskHealthSnapshot { Id = Guid.NewGuid(), EndpointId = endpointId, DiskKey = key };
                db.DiskHealth.Add(snap);
                existing.Add(snap);
            }
            snap.Model = raw.Model;
            snap.Serial = raw.Serial;
            snap.MediaType = raw.MediaType;
            snap.BusType = raw.BusType;
            snap.ScoreVersion = DiskHealthEvaluator.ScoreVersion;
            if (raw.Mapping is { } m)
            {
                snap.BusTypeCode = m.BusTypeCode; snap.MediaTypeCode = m.MediaTypeCode; snap.SpindleSpeed = m.SpindleSpeed;
                snap.ClassSource = m.ClassSource; snap.PhysicalMatch = m.PhysicalMatch; snap.HealthSource = m.HealthSource;
                snap.SmartMatched = m.SmartMatched; snap.HostSmartRecords = m.HostSmartRecords; snap.HostDriveCount = m.HostDriveCount;
            }
            snap.SizeBytes = raw.SizeBytes;
            snap.HealthPercent = result.HealthPercent;
            snap.Status = result.Status;
            snap.TemperatureC = result.TempC;
            snap.MaxTemperatureC = raw.MaxTemperatureC;
            snap.PowerOnHours = raw.PowerOnHours;
            snap.WearPercent = raw.Nvme is { PercentageUsed: >= 0 and <= 255 } nv ? Math.Min(100, nv.PercentageUsed)
                : raw.WearPercent is >= 0 and <= 100 ? raw.WearPercent : null;
            snap.ReadErrors = raw.ReadErrors;
            snap.WriteErrors = raw.WriteErrors;
            snap.ReallocatedSectors = raw.Reallocated;
            snap.PendingSectors = raw.Pending;
            snap.PredictFailure = raw.PredictFailure;
            snap.EstimatedDaysLeft = result.EstimatedDaysLeft;
            snap.Summary = result.Summary;
            snap.AttributesJson = JsonSerializer.Serialize(raw.Attributes);
            var evidence = (result.Reasons ?? []).ToList();
            if (raw.SourceSampledUtc is not null)
            {
                evidence.Add(new HealthReason("scan-completed-utc", now.ToString("O"), 0));
                evidence.Add(new HealthReason("source-provenance", raw.Mapping?.HealthSource ?? "native-cache", 0));
                if (raw.SourceStale || now - raw.SourceSampledUtc.Value > TimeSpan.FromMinutes(2))
                    evidence.Add(new HealthReason("source-stale", raw.SourceNote ?? "Local collector data is old. Check the telemetry task; this is last-known evidence.", 0));
            }
            if (raw.SourceStale && !evidence.Any(r => r.Code == "source-stale"))
                evidence.Add(new HealthReason("source-stale", raw.SourceNote ?? "Collector evidence is unavailable.", 0));
            snap.ReasonsJson = JsonSerializer.Serialize(evidence);
            snap.InterfaceWarning = result.InterfaceWarning;
            snap.TempWarnC = result.Temperature?.WarnC;
            snap.TempCritC = result.Temperature?.CritC;
            snap.TempThresholdSource = result.Temperature?.Source;
            snap.TempMaxLifetimeC = result.Temperature?.LifetimeMaxC;
            snap.UpdatedUtc = raw.SourceSampledUtc ?? now;

            if (!repeatedCachedSample) db.DiskTemps.Add(new DiskTempSample
            {
                EndpointId = endpointId, DiskKey = key, TimestampUtc = snap.UpdatedUtc,
                TemperatureC = result.TempC, HealthPercent = result.HealthPercent,
            });
        }

        // Drives that disappeared (replaced or removed) drop out of the latest view; their history ages out.
        var keys = WithUniqueKeys(disks).Select(d => d.Key).ToHashSet();
        foreach (var gone in existing.Where(s => !keys.Contains(s.DiskKey))) db.DiskHealth.Remove(gone);

        var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == endpointId, ct);
        if (e is not null) { e.LastDiskPollUtc = now; e.DiskScanError = null; }
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkScanFailedAsync(Guid endpointId, string reason, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();   // whatever the failed attempt left tracked must not be saved again
        var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == endpointId, ct);
        if (e is null) return;
        e.LastDiskPollUtc = clock.UtcNow;
        e.DiskScanError = reason.Length > 300 ? reason[..300] : reason;
        await db.SaveChangesAsync(ct);
    }

    private DiskHealthDto ToDto(DiskHealthSnapshot s, IReadOnlyList<TempPoint> history, DriveTempLimits limits, bool uncertain, int? lastGood)
    {
        IReadOnlyList<SmartAttribute> attrs;
        try { attrs = JsonSerializer.Deserialize<List<SmartAttribute>>(s.AttributesJson) ?? []; }
        catch (JsonException) { attrs = []; }
        IReadOnlyList<HealthReason> reasons;
        try { reasons = JsonSerializer.Deserialize<List<HealthReason>>(s.ReasonsJson ?? "[]") ?? []; }
        catch (JsonException) { reasons = []; }
        var temperature = TemperatureOf(s, limits, reasons.Any(r => r.Code == "nvme-warning-temp"));
        var cached = reasons.Any(r => r.Code == "source-provenance");
        var stale = uncertain || reasons.Any(r => r.Code == "source-stale") || clock.UtcNow - s.UpdatedUtc > TimeSpan.FromMinutes(cached ? 2 : 30) || s.UpdatedUtc > clock.UtcNow.AddMinutes(2);
        var lastKnown = temperature.CurrentC ?? lastGood;
        if (stale) temperature = temperature with { CurrentC = null, Status = null };
        var confidence = s.HealthPercent is null || stale || s.ScoreVersion != DiskHealthEvaluator.ScoreVersion || reasons.Any(r => r.Code is "windows-only" or "169-cap" or "telemetry-conflict")
            || (s.Model.StartsWith("ADATA", StringComparison.OrdinalIgnoreCase) && attrs.Any(a => a.Id == 169)) ? "Low"
            : reasons.Any(r => r.Code == "nvme-source") ? "High" : attrs.Count > 0 ? "Medium" : "Low";
        return new DiskHealthDto(s.DiskKey, s.Model, s.Serial, s.MediaType, s.BusType, s.SizeBytes, s.HealthPercent, s.Status,
            temperature.CurrentC, s.MaxTemperatureC, s.PowerOnHours, s.WearPercent, s.ReadErrors, s.WriteErrors,
            s.ReallocatedSectors, s.PendingSectors, s.PredictFailure, s.EstimatedDaysLeft, s.Summary, attrs, s.UpdatedUtc, history,
            DriveClassifier.Classify(s.MediaType, s.BusType), temperature.Status, reasons, temperature, s.InterfaceWarning,
            confidence, s.HealthPercent is null ? "unavailable" : "estimated", stale, lastKnown, HealthModelVersion: s.ScoreVersion,
            ScanCompletedUtc: DateTime.TryParse(reasons.FirstOrDefault(r => r.Code == "scan-completed-utc")?.Text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var scan) ? scan : s.UpdatedUtc,
            SourceNote: reasons.FirstOrDefault(r => r.Code == "source-stale")?.Text);
    }

    /// <summary>
    /// A drive's own limits (stored at the scan) win; otherwise the current Admin defaults for its class apply, so a Settings change
    /// takes effect without a rescan.
    /// </summary>
    public static TemperatureInfo TemperatureOf(DiskHealthSnapshot s, DriveTempLimits limits, bool driveFlagged = false)
    {
        var own = s.TempThresholdSource == DriveTemperature.SourceDrive && s.TempWarnC is not null && s.TempCritC is not null;
        var probe = new DiskRaw(s.DiskKey, s.Model, s.Serial, s.MediaType, s.BusType, s.SizeBytes, null, false, s.TemperatureC, null, null, null, null,
            null, null, null, null, null, null, [],
            DriveTempWarnC: own ? s.TempWarnC : null, DriveTempCritC: own ? s.TempCritC : null, LifetimeMaxTempC: s.TempMaxLifetimeC);
        return DriveTemperature.Resolve(probe, limits, s.TemperatureC, driveFlagged);
    }
}
