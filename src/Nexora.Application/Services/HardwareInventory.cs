using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexora.Domain;

namespace Nexora.Application.Services;

// ---------- Hardware / OS inventory ----------
// Collected on demand and cached for a configurable time (Monitoring:InventoryTtlMinutes); it is NOT part of the high-frequency
// poll. Every field is nullable: null means "the workstation did not report it", never zero or a guess.

/// <param name="BootTimeUtc">Win32_OperatingSystem.LastBootUpTime. With Fast Startup this is the last FULL boot, not the last power-on.</param>
/// <param name="FastStartupEnabled">HiberbootEnabled from the registry; null = not reported.</param>
public sealed record OsInventory(
    string? ProductName, string? Edition, string? Version, string? Build, string? Architecture,
    DateTime? InstalledUtc, DateTime? BootTimeUtc, bool? FastStartupEnabled = null, string? KernelVersion = null)
{
    /// <summary>"fullBoot" when Fast Startup is on (uptime counts from the last full boot and the UI must say so), otherwise "boot".</summary>
    public string UptimeBasis => HardwareMapping.UptimeBasis(FastStartupEnabled);
}

public sealed record CpuInventory(
    string? Manufacturer, string? Model, int? PhysicalCores, int? LogicalProcessors, string? Architecture, int? MaxClockMhz);

public sealed record MemoryModuleInfo(
    string? Slot, string? Bank, long? CapacityBytes, string? Manufacturer, int? SpeedMhz, int? ConfiguredSpeedMhz, string? PartNumber, string? Technology);

/// <param name="InstalledBytes">Sum of the module capacities when modules were reported, otherwise the system's physical memory.</param>
/// <param name="SlotCount">Physical memory slots from the memory array; null when SMBIOS did not report it.</param>
/// <param name="EmptySlots">Only set when the slot count is known and consistent with the modules found.</param>
public sealed record MemoryInventory(
    long? InstalledBytes, long? UsableBytes, string? Technology, int? SpeedMhz, int? SlotCount, int OccupiedSlots, int? EmptySlots,
    long? MaxCapacityBytes, IReadOnlyList<MemoryModuleInfo> Modules, bool ModulesReported);

public sealed record BoardInventory(string? Manufacturer, string? Model, string? Serial);
public sealed record BiosInventory(string? Manufacturer, string? Version, DateTime? ReleaseDateUtc);
public sealed record ComputerInventory(string? Manufacturer, string? Model, string? Hostname, string? Domain, bool? DomainJoined);

/// <param name="Status">Complete, Partial (some sections failed), Unavailable (never collected: offline or failed).</param>
public sealed record HardwareInventoryDto(
    string Status, DateTime? CollectedUtc, bool Cached, bool SerialsHidden, IReadOnlyList<string> Errors,
    OsInventory? Os, CpuInventory? Cpu, MemoryInventory? Memory, BoardInventory? Board, BiosInventory? Bios, ComputerInventory? Computer,
    DateTime? AttemptedUtc = null);

public interface IHardwareInventoryCollector
{
    /// <summary>Reads the inventory over WMI/CIM. Sections that fail are left null and named in the errors; it throws only when the PC cannot be reached at all.</summary>
    Task<HardwareInventoryDto> CollectHardwareAsync(string host, CancellationToken ct);
}

/// <summary>Pure mapping rules from raw WMI values to inventory values (unit-tested without Windows).</summary>
public static class HardwareMapping
{
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown", "to be filled by o.e.m.", "to be filled by oem", "default string", "not specified", "none", "n/a", "na",
        "system manufacturer", "system product name", "system version", "o.e.m.", "oem", "0", "00000000", "123456789", "not applicable", "undefined",
    };

    /// <summary>Trimmed text, or null for empty values and the placeholders firmware vendors leave behind.</summary>
    public static string? Clean(string? value)
    {
        var t = value?.Trim().Trim('\0');
        if (string.IsNullOrEmpty(t) || Placeholders.Contains(t)) return null;
        return t.Length > 3 && t.All(c => c is '0' or 'F' or 'f' or '-' or ' ') ? null : t;   // all-zero / all-F serials are firmware filler
    }

    public static string? CpuVendor(string? raw) => Clean(raw) switch
    {
        null => null,
        var v when v.Equals("GenuineIntel", StringComparison.OrdinalIgnoreCase) => "Intel",
        var v when v.Equals("AuthenticAMD", StringComparison.OrdinalIgnoreCase) => "AMD",
        var v => v,
    };

    /// <summary>Win32_Processor.Architecture.</summary>
    public static string? ProcessorArchitecture(int? code) => code switch { 0 => "x86", 5 => "ARM", 9 => "x64", 12 => "ARM64", _ => null };

    /// <summary>Win32_OperatingSystem.OSArchitecture ("64-bit", "32-bit", "ARM 64-bit Processor") combined with the CPU architecture.</summary>
    public static string? OsArchitecture(string? osArchitecture, int? cpuArchitecture)
    {
        var o = osArchitecture?.Trim() ?? "";
        if (o.Contains("ARM", StringComparison.OrdinalIgnoreCase)) return o.Contains("64") ? "ARM64" : "ARM";
        if (o.Contains("64")) return cpuArchitecture == 12 ? "ARM64" : "x64";
        if (o.Contains("32")) return cpuArchitecture == 5 ? "ARM" : "x86";
        return ProcessorArchitecture(cpuArchitecture);
    }

    /// <summary>SMBIOS memory type (Win32_PhysicalMemory.SMBIOSMemoryType); falls back to the legacy MemoryType only for its unambiguous values.</summary>
    public static string? MemoryTechnology(int? smbios, int? legacy)
    {
        var s = smbios switch
        {
            18 => "DDR", 19 => "DDR2", 20 => "DDR2 FB-DIMM", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2",
            29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => null,
        };
        if (s is not null) return s;
        return smbios is null or 0 or 1 or 2 ? legacy switch { 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", _ => null } : null;
    }

    /// <summary>HiberbootEnabled (REG_DWORD): any non-zero value is on; absent = unknown, never assumed.</summary>
    public static bool? FastStartup(int? hiberbootEnabled) => hiberbootEnabled is null ? null : hiberbootEnabled != 0;

    public static string UptimeBasis(bool? fastStartupEnabled) => fastStartupEnabled == true ? "fullBoot" : "boot";

    /// <summary>
    /// Parses a CIM datetime string ("yyyyMMddHHmmss.ffffff+UUU", UUU = offset in minutes) to UTC, independent of the thread culture.
    /// Null for anything malformed and for the zero date WMI uses for "unknown".
    /// </summary>
    public static DateTime? ParseCimDateTime(string? value)
    {
        var s = value?.Trim();
        if (s is null || s.Length != 25 || s[14] != '.' || s[21] is not ('+' or '-')) return null;
        static bool Num(string text, int start, int len, out int n) =>
            int.TryParse(text.AsSpan(start, len), NumberStyles.None, CultureInfo.InvariantCulture, out n);
        if (!Num(s, 0, 4, out var y) || !Num(s, 4, 2, out var mo) || !Num(s, 6, 2, out var d) || !Num(s, 8, 2, out var h)
            || !Num(s, 10, 2, out var mi) || !Num(s, 12, 2, out var sec) || !Num(s, 15, 6, out var micro) || !Num(s, 22, 3, out var offMin))
            return null;
        if (y <= 1601 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || sec > 59 || offMin > 14 * 60) return null;
        var local = new DateTime(y, mo, d, h, mi, sec, DateTimeKind.Unspecified).AddTicks(micro * 10L);
        return new DateTimeOffset(local, TimeSpan.FromMinutes(s[21] == '-' ? -offMin : offMin)).UtcDateTime;
    }

    /// <summary>The Windows release label comes from the registry (DisplayVersion, then the old ReleaseId); it is never inferred from the build number.</summary>
    public static string? WindowsVersion(string? displayVersion, string? releaseId) => Clean(displayVersion) ?? Clean(releaseId);

    public static string? FullBuild(string? currentBuild, int? ubr)
    {
        var b = Clean(currentBuild);
        if (b is null) return null;
        return ubr is >= 0 ? string.Create(CultureInfo.InvariantCulture, $"{b}.{ubr}") : b;
    }

    /// <summary>Empty = slot count unknown or inconsistent with the modules found; never a guess.</summary>
    public static (int? Slots, int Occupied, int? Empty) Slots(int? totalSlots, int moduleCount)
    {
        if (totalSlots is not > 0 || totalSlots < moduleCount) return (totalSlots is > 0 ? totalSlots : null, moduleCount, null);
        return (totalSlots, moduleCount, totalSlots - moduleCount);
    }
}

/// <summary>A CPU/RAM sample from monitoring or the on-screen performance reader. Null fields = not reported.</summary>
public sealed record LiveSampleDto(
    double? CpuLoadPercent, long? RamTotalBytes, long? RamUsedBytes, long? RamFreeBytes, double? RamPercentUsed, DateTime SampledUtc);

/// <param name="Stale">True when the inventory is the last known snapshot (the PC is offline or the latest read failed).</param>
/// <param name="UnavailableReason">Short generic text for the UI; never an exception message.</param>
public sealed record EndpointOverviewDto(
    bool Online, DateTime? LastSeenUtc, DateTime? CollectedUtc, bool Stale, string? UnavailableReason,
    HardwareInventoryDto? Inventory, LiveSampleDto? Live);

public interface IHardwareInventoryService
{
    /// <summary>Overview for the workstation page: stored/cached inventory (flagged stale when it could not be refreshed) plus the latest live sample. Rate-limited like <see cref="GetAsync"/>.</summary>
    Task<Result<EndpointOverviewDto>> GetOverviewAsync(Guid endpointId, CancellationToken ct = default);

    /// <param name="refresh">Collect now (rate-limited) instead of using the cached copy.</param>
    /// <param name="includeSerials">Serial numbers are returned only to Operator and above.</param>
    Task<Result<HardwareInventoryDto>> GetAsync(Guid endpointId, bool refresh, bool includeSerials, CancellationToken ct = default);
}

/// <summary>Holds the last inventory per workstation and serializes collection per workstation. Singleton.</summary>
public sealed class HardwareInventoryCache
{
    internal sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public HardwareInventoryDto? Value;
        public DateTime? LastAttemptUtc;
        public string? LastError;
        public bool Loaded;   // the stored snapshot has been read from the database into Value
    }

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    internal Entry For(Guid id) => _entries.GetOrAdd(id, _ => new Entry());
    public void Forget(Guid id) => _entries.TryRemove(id, out _);
}

public sealed class HardwareInventoryService(
    INexoraDb db, IHardwareInventoryCollector collector, HardwareInventoryCache cache, IOptions<NexoraOptions> options, ISystemClock clock,
    Microsoft.Extensions.Logging.ILogger<HardwareInventoryService> logger) : IHardwareInventoryService
{
    private const string OfflineNote = "The workstation is offline; the inventory was not refreshed.";
    private const string OfflineReason = "The workstation is offline.";
    private const string ReadFailedReason = "The inventory could not be read from the workstation right now.";
    private const string NotCollectedReason = "The inventory has not been collected yet.";

    private sealed record Resolved(ManagedEndpoint Endpoint, HardwareInventoryDto Dto, bool HasSnapshot, bool Stale, string? Reason);

    public async Task<Result<HardwareInventoryDto>> GetAsync(Guid endpointId, bool refresh, bool includeSerials, CancellationToken ct = default)
    {
        var r = await ResolveAsync(endpointId, refresh, ct);
        return r is null ? Result<HardwareInventoryDto>.Fail(ErrorKind.NotFound, "Endpoint not found.") : Result<HardwareInventoryDto>.Success(Mask(r.Dto, includeSerials));
    }

    public async Task<Result<EndpointOverviewDto>> GetOverviewAsync(Guid endpointId, CancellationToken ct = default)
    {
        var r = await ResolveAsync(endpointId, false, ct);
        if (r is null) return Result<EndpointOverviewDto>.Fail(ErrorKind.NotFound, "Endpoint not found.");
        var e = r.Endpoint;
        var inventory = r.HasSnapshot ? Mask(r.Dto, false) : null;   // the overview never carries serial numbers
        return Result<EndpointOverviewDto>.Success(new EndpointOverviewDto(
            e.Online, e.LastSeenUtc, inventory?.CollectedUtc, r.HasSnapshot && r.Stale, r.Reason, inventory, LiveOf(e)));
    }

    private static LiveSampleDto? LiveOf(ManagedEndpoint e)
    {
        if (!e.Online || e.LastSeenUtc is not { } sampled) return null;
        long? used = e.RamTotalBytes is { } t && e.RamFreeBytes is { } f && f >= 0 && f <= t ? t - f : null;
        return new LiveSampleDto(e.CpuPercent, e.RamTotalBytes, used, e.RamFreeBytes, e.RamPercent, sampled);
    }

    private async Task<Resolved?> ResolveAsync(Guid endpointId, bool refresh, CancellationToken ct)
    {
        var e = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == endpointId, ct);
        if (e is null) return null;

        var m = options.Value.Monitoring;
        var entry = cache.For(endpointId);
        await entry.Gate.WaitAsync(ct);   // one collection per workstation at a time; concurrent callers share its result
        try
        {
            if (!entry.Loaded)
            {
                entry.Value ??= await LoadStoredAsync(endpointId, ct);
                entry.Loaded = true;
            }

            if (e.Online && entry.LastError == OfflineNote) entry.LastError = null;   // it is back; that note no longer applies
            var now = clock.UtcNow;
            var fresh = entry.Value?.CollectedUtc is { } at && now - at < TimeSpan.FromMinutes(Math.Max(1, m.InventoryTtlMinutes));
            var tooSoon = entry.LastAttemptUtc is { } last && now - last < TimeSpan.FromSeconds(Math.Max(0, m.InventoryMinRefreshSeconds));
            var wantCollect = entry.Value is null ? !tooSoon : (refresh || !fresh) && !tooSoon;

            if (wantCollect && !e.Online)
            {
                // An offline PC is not queried: that would only wait for the timeout. Keep what we have.
                entry.LastError = OfflineNote;
                wantCollect = false;
            }

            var collectedNow = false;
            if (wantCollect)
            {
                entry.LastAttemptUtc = now;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, m.InventoryTimeoutSeconds)));
                    var collected = await collector.CollectHardwareAsync(e.Hostname, timeout.Token).WaitAsync(timeout.Token);
                    entry.Value = collected with { CollectedUtc = clock.UtcNow, Cached = false, AttemptedUtc = now };
                    entry.LastError = null;
                    collectedNow = true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    entry.LastError = $"Reading the inventory of {e.Hostname} timed out.";
                    Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, "Inventory of {Host} timed out", e.Hostname);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    entry.LastError = $"Could not read the inventory of {e.Hostname}. It may be off, unreachable, or refusing remote management.";
                    Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, ex, "Inventory of {Host} failed", e.Hostname);
                }
                if (collectedNow) await StoreAsync(endpointId, entry.Value!, ct);
            }

            var hasSnapshot = entry.Value is not null;
            var value = entry.Value is { } v
                ? v with { Cached = !collectedNow, Errors = entry.LastError is null ? v.Errors : [.. v.Errors, entry.LastError] }
                : new HardwareInventoryDto("Unavailable", null, false, false, [entry.LastError ?? NotCollectedReason], null, null, null, null, null, null, entry.LastAttemptUtc);
            var stale = !e.Online || entry.LastError is not null;
            var reason = hasSnapshot && !stale ? null : !e.Online ? OfflineReason : entry.LastError is not null ? ReadFailedReason : NotCollectedReason;
            return new Resolved(e, value, hasSnapshot, stale, reason);
        }
        finally { entry.Gate.Release(); }
    }

    private async Task<HardwareInventoryDto?> LoadStoredAsync(Guid endpointId, CancellationToken ct)
    {
        try
        {
            var row = await db.EndpointInventories.AsNoTracking().FirstOrDefaultAsync(x => x.EndpointId == endpointId, ct);
            if (row is null) return null;
            var dto = JsonSerializer.Deserialize<HardwareInventoryDto>(row.Json);
            return dto is null ? null : dto with { CollectedUtc = row.CollectedUtc, Cached = true, Errors = dto.Errors ?? [] };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, ex, "Stored inventory of {EndpointId} could not be read", endpointId);
            return null;
        }
    }

    /// <summary>Upserts the snapshot. A storage problem never fails the read: the in-memory copy is still served.</summary>
    private async Task StoreAsync(Guid endpointId, HardwareInventoryDto dto, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(dto);
            var at = dto.CollectedUtc ?? clock.UtcNow;
            var row = await db.EndpointInventories.FirstOrDefaultAsync(x => x.EndpointId == endpointId, ct);
            if (row is null) db.EndpointInventories.Add(new EndpointInventory { EndpointId = endpointId, Json = json, CollectedUtc = at });
            else { row.Json = json; row.CollectedUtc = at; }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, ex, "Inventory of {EndpointId} could not be stored", endpointId);
        }
    }

    private static HardwareInventoryDto Mask(HardwareInventoryDto v, bool includeSerials) =>
        includeSerials ? v with { SerialsHidden = false } : v with { SerialsHidden = true, Board = v.Board is null ? null : v.Board with { Serial = null } };
}
