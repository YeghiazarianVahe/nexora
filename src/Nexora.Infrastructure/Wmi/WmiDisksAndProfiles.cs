using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using System.Security.Principal;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Nexora.Application;
using Nexora.Application.Services;

namespace Nexora.Infrastructure.Wmi;

/// <summary>Drive health (SMART / reliability counters) and local user profiles. Strictly read-only.</summary>
public sealed partial class WmiEndpointCollector
{
    private const string StorageNs = @"root\Microsoft\Windows\Storage";
    private const string WmiNs = @"root\wmi";

    // ---------- drives ----------

    public Task<IReadOnlyList<DiskRaw>> CollectDisksAsync(string host, CancellationToken ct) =>
        Task.Run(() => ReadDisks(host, ct), ct);

    private IReadOnlyList<DiskRaw> ReadDisks(string host, CancellationToken ct)
    {
        using var session = Open(host);
        var op = Operation();

        var drives = Query(session, op, "SELECT Index, Model, SerialNumber, PNPDeviceID, Size, InterfaceType FROM Win32_DiskDrive");
        var physical = SafeQuery(session, StorageNs, "SELECT * FROM MSFT_PhysicalDisk", op);
        var predict = SafeQuery(session, WmiNs, "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus", op);
        var smart = SafeQuery(session, WmiNs, "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_ATAPISmartData", op);
        // Drivers expose either class, and some expose different disks through each.
        // Prefer the existing row when both providers describe the same device.
        smart = smart.Concat(SafeQuery(session, WmiNs, "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData", op))
            .GroupBy(row => (DriveMatching.NormalizePnp(Str(row, "InstanceName")), SmartEvidenceAccounting.Fingerprint(row.CimInstanceProperties["VendorSpecific"]?.Value as byte[])))
            .Select(group => group.First()).ToList();
        var thresholds = SafeQuery(session, WmiNs, "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds", op);

        IReadOnlyList<RemoteNvmeReader.NativeDisk> nativeDisks = IsLocal(host) ? RemoteNvmeReader.ReadLocalCache() : [];
        if (!IsLocal(host))
        {
            var indexes = drives.Where(d =>
            {
                var (matched, _) = DriveMatching.Find(physical, new DriveKeys(Str(d, "SerialNumber"), Num(d, "Index")?.ToString(), Str(d, "PNPDeviceID")), p => new DriveKeys(Str(p, "SerialNumber"), Str(p, "DeviceId"), null));
                var keys = new DriveKeys(Str(d, "SerialNumber"), Num(d, "Index")?.ToString(), Str(d, "PNPDeviceID"));
                var (smartMatch, _) = DriveMatching.Find(smart, keys, row => new DriveKeys(null, null, Str(row, "InstanceName")));
                return Num(matched, "BusType") == DriveClassifier.BusTypeNvme || smartMatch is null;
            }).Select(d => (int)(Num(d, "Index") ?? -1)).Where(i => i >= 0).ToArray();
            if (indexes.Length > 0)
            {
                try { nativeDisks = RemoteNvmeReader.Read(host, indexes, session, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "Native NVMe read of {Host} unavailable; using only supported WMI evidence", host); }
            }
        }

        var result = new List<DiskRaw>();
        var replacedWmi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nativeAtaIndexes = new HashSet<string>();
        foreach (var drive in drives)
        {
            ct.ThrowIfCancellationRequested();
            var index = (int)(Num(drive, "Index") ?? 0);
            var model = (Str(drive, "Model") ?? "Unknown drive").Trim();
            var pnp = Str(drive, "PNPDeviceID") ?? "";
            var serial = Str(drive, "SerialNumber")?.Trim().TrimEnd('.');
            var size = (long)(Num(drive, "Size") ?? 0);

            // Records about this drive are paired by identity (serial, then PnP id, then disk number), never by position.
            var keys = new DriveKeys(serial, index.ToString(), pnp);
            var (pd, pdLevel) = DriveMatching.Find(physical, keys, p => new DriveKeys(Str(p, "SerialNumber"), Str(p, "DeviceId"), null));
            var busCode = (int?)Num(pd, "BusType");
            var mediaCode = (int?)Num(pd, "MediaType");
            long? spindle = Num(pd, "SpindleSpeed") is { } sp ? (long)sp : null;
            var (cls, classSource) = DriveClassifier.FromFactsWithSource(busCode, mediaCode, spindle);
            // NVMe keeps its own label (the health evaluator treats it like an SSD); everything else is SSD, HDD or Unknown.
            var media = busCode == DriveClassifier.BusTypeNvme ? "NVMe" : cls;
            var health = (int?)Num(pd, "HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };

            CimInstance? counter = null;
            if (pd is not null)
            {
                try
                {
                    counter = session.EnumerateAssociatedInstances(StorageNs, pd, null, "MSFT_StorageReliabilityCounter", null, null).FirstOrDefault();
                }
                catch (CimException) { }
            }

            DriveKeys ByInstance(CimInstance s) => new(null, null, Str(s, "InstanceName"));
            var (smartRow, _) = DriveMatching.Find(smart, keys, ByInstance);
            var (predictRow, _) = DriveMatching.Find(predict, keys, ByInstance);
            var (thresholdRow, _) = DriveMatching.Find(thresholds, keys, ByInstance);
            var attrs = SmartParser.Parse(
                smartRow?.CimInstanceProperties["VendorSpecific"]?.Value as byte[],
                thresholdRow?.CimInstanceProperties["VendorSpecific"]?.Value as byte[]);
            var native = nativeDisks.FirstOrDefault(d => RemoteNvmeReader.Matches(d, index, serial, model));
            var nativeAta = false;
            if (attrs.Count == 0 && !string.IsNullOrEmpty(native?.Ata))
            {
                try
                {
                    attrs = SmartParser.Parse(Convert.FromBase64String(native.Ata), string.IsNullOrEmpty(native.Thresholds) ? null : Convert.FromBase64String(native.Thresholds));
                    nativeAta = attrs.Count > 0;
                    if (nativeAta)
                    {
                        nativeAtaIndexes.Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        if (smartRow is not null) replacedWmi.Add(Str(smartRow,"InstanceName") ?? "");
                    }
                }
                catch (FormatException) { }
            }

            int? temp = Positive(Num(counter, "Temperature")) ?? SmartParser.Temperature(attrs);
            long? poh = Positive(Num(counter, "PowerOnHours")) is { } h ? h : SmartParser.Raw(attrs, 9) is { } r ? r & 0xFFFFFFFF : null;
            // Total errors include corrected retries and are not evidence of media failure.
            long? readErr = (long?)Num(counter, "ReadErrorsUncorrected");
            long? writeErr = (long?)Num(counter, "WriteErrorsUncorrected");
            int? wear = media == "HDD" ? null : (int?)Num(counter, "Wear");

            // Native logs are local IOCTL reads, either here or through the bounded DCOM reader on the target.
            var localNvme = busCode == DriveClassifier.BusTypeNvme && IsLocal(host);
            var nvme = localNvme ? NvmeSmart.TryRead(index) : null;
            var nvmeId = localNvme ? NvmeSmart.TryReadIdentify(index) : null;
            var nativeNvme = false;
            if (nvme is null && !string.IsNullOrEmpty(native?.Health))
            {
                try
                {
                    nvme = NvmeSmart.ParseHealthLog(Convert.FromBase64String(native.Health));
                    nvmeId = string.IsNullOrEmpty(native.Identify) ? null : NvmeSmart.ParseIdentify(Convert.FromBase64String(native.Identify));
                    nativeNvme = nvme is not null;
                }
                catch (FormatException) { nvme = null; nvmeId = null; }
            }
            if (poh is null && nvme is { PowerOnHours: > 0 }) poh = nvme.PowerOnHours;
            if (nvme is { TemperatureC: > 0 and < 150 }) temp = nvme.TemperatureC;
            var healthStatus = nvme is not null && DiskHealthEvaluator.HasNvmeHealthWarning(nvme.CriticalWarning) && health != "Unhealthy" ? "Warning" : health;

            var healthSource = nvme is not null ? "nvme-smart-log" : nativeAta ? "native-ata-smart" : smartRow is not null ? "smart-attributes" : counter is not null ? "reliability-counter" : pd is not null ? "windows-health-status" : "none";
            var localSourceMissing = IsLocal(host) && attrs.Count == 0 && nvme is null && wear is null;
            logger.LogDebug(
                "Disk mapping {Host}: PhysicalDrive{Index} model={Model} serial={Serial} bus={Bus} mediaType={MediaType} spindle={Spindle} class={Class} storedAs={Stored} physicalDiskMatchedBy={PhysicalMatch} smartMatched={Smart} healthSource={HealthSource}",
                host, index, model, serial ?? "-", busCode?.ToString() ?? "-", mediaCode?.ToString() ?? "-", spindle?.ToString() ?? "-", cls, media, pdLevel, smartRow is not null, healthSource);

            result.Add(new DiskRaw(
                Key: string.IsNullOrWhiteSpace(serial) ? $"disk{index}" : serial,
                Model: model, Serial: serial, MediaType: media,
                BusType: busCode switch { 17 => "NVMe", 11 => "SATA", 3 => "ATA", 7 => "USB", 8 => "RAID", _ => Str(drive, "InterfaceType") },
                SizeBytes: size, HealthStatus: healthStatus,
                PredictFailure: predictRow?.CimInstanceProperties["PredictFailure"]?.Value is true,
                TemperatureC: temp, MaxTemperatureC: Positive(Num(counter, "TemperatureMax")),
                PowerOnHours: poh, WearPercent: wear, LifeLeftPercent: nvme?.LifeLeftPercent ?? SmartParser.LifeLeft(attrs, media is "SSD" or "NVMe", model),
                ReadErrors: readErr, WriteErrors: writeErr,
                Reallocated: SmartParser.Raw(attrs, 5), Pending: SmartParser.Raw(attrs, 197),
                OfflineUncorrectable: SmartParser.Raw(attrs, 198), CrcErrors: SmartParser.Raw(attrs, 199),
                Attributes: attrs,
                Mapping: new DiskMapping(busCode, mediaCode, spindle, classSource, pdLevel.ToString(), healthSource, smartRow is not null || nativeAta,
                    smart.Count, drives.Count),
                Nvme: nvme is null ? null : new NvmeInfo(nvme.CriticalWarning, nvme.PercentageUsed, nvme.AvailableSpare, nvme.SpareThreshold, nvme.MediaErrors),
                DriveTempWarnC: nvmeId?.WarningTempC, DriveTempCritC: nvmeId?.CriticalTempC,
                SourceSampledUtc: nativeAta || nativeNvme ? native?.SourceSampledUtc : null,
                SourceStale: localSourceMissing || ((nativeAta || nativeNvme) && native?.SourceStale == true),
                SourceNote: localSourceMissing ? "Local native evidence is unavailable. Check the disk telemetry task and permissions." : nativeAta || nativeNvme ? native?.SourceNote : null));
        }
        var records = SmartEvidenceAccounting.CountProviders(smart.Select(s => new SmartEvidenceAccounting.ProviderRecord(Str(s,"InstanceName") ?? "", SmartEvidenceAccounting.Fingerprint(s.CimInstanceProperties["VendorSpecific"]?.Value as byte[]))),
            nativeAtaIndexes, replacedWmi);
        return result.Select(d => d with { Mapping = d.Mapping! with { HostSmartRecords = records } }).ToList();
    }

    private static List<CimInstance> SafeQuery(CimSession session, string ns, string wql, CimOperationOptions op)
    {
        try { return session.QueryInstances(ns, "WQL", wql, op).ToList(); }
        catch (CimException) { return []; }
    }

    private static int? Positive(double? v) => v is > 0 ? (int)v.Value : null;

    // ---------- user profiles ----------

    private const string RegNs = @"root\default";
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private const int MaxAccountCache = 5_000, MaxSignedInSessions = 64;

    /// <summary>Only names that were found are kept (see <see cref="AccountResolution"/>); instance state, bounded.</summary>
    private readonly ConcurrentDictionary<string, string?> _accountCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record ProfileRow(ProfileInfo Info, DateTime? Load, DateTime? Unload, DateTime? Wmi);

    /// <summary>
    /// All profiles Windows knows, system ones included (the caller filters). Last use is chosen by <see cref="ProfileLastUsed.Choose"/>: the
    /// ProfileList load/unload time, then Win32_UserProfile.LastUseTime. No file on the endpoint is read by the server.
    /// </summary>
    public async Task<IReadOnlyList<ProfileInfo>> CollectProfilesAsync(string host, CancellationToken ct)
    {
        var rows = await Task.Run(() => ReadProfileRows(host, ct), ct);
        var result = new List<ProfileInfo>(rows.Count);
        foreach (var r in rows)
        {
            ct.ThrowIfCancellationRequested();
            var (utc, source) = ProfileLastUsed.Choose(r.Load, r.Unload, r.Wmi);
            result.Add(r.Info with { LastUseUtc = utc, LastUseSource = source });
        }
        return result.OrderByDescending(p => p.LastUseUtc ?? DateTime.MinValue).ToList();
    }

    private List<ProfileRow> ReadProfileRows(string host, CancellationToken ct)
    {
        using var session = Open(host);
        var op = Operation();
        if (_accountCache.Count > MaxAccountCache) _accountCache.Clear();
        var rows = Query(session, op, "SELECT SID, LocalPath, LastUseTime, Loaded, Special, RoamingConfigured, Status FROM Win32_UserProfile");
        var result = new List<ProfileRow>(rows.Count);
        foreach (var r in rows)
        {
            ct.ThrowIfCancellationRequested();
            var sid = Str(r, "SID") ?? "";
            // Names and registry times are only looked up for real user SIDs: system profiles are filtered out later and need neither.
            var user = ProfileRules.IsUserSid(sid);
            var (load, unload) = user ? ReadProfileListTimes(session, op, sid, ct) : (null, null);
            var info = new ProfileInfo(
                sid, user ? ResolveAccount(sid, session) : null, Str(r, "LocalPath") ?? "", null,
                r.CimInstanceProperties["Loaded"]?.Value is true,
                r.CimInstanceProperties["Special"]?.Value is true,
                r.CimInstanceProperties["RoamingConfigured"]?.Value is true,
                StatusText(Str(r, "Status")));
            result.Add(new ProfileRow(info, load, unload, CimTime(r.CimInstanceProperties["LastUseTime"]?.Value)));
        }
        return result;
    }

    /// <summary>A CIM datetime is already a DateTime through the CIM API; a text one is parsed by the shared, culture-free parser.</summary>
    private static DateTime? CimTime(object? v) => v switch
    {
        DateTime dt => dt.ToUniversalTime(),
        string s => HardwareMapping.ParseCimDateTime(s),
        _ => null,
    };

    /// <summary>LocalProfileLoadTime / LocalProfileUnloadTime of the SID's ProfileList key (a FILETIME as two DWORDs). A value that cannot be read is null.</summary>
    private static (DateTime? Load, DateTime? Unload) ReadProfileListTimes(CimSession session, CimOperationOptions op, string sid, CancellationToken ct)
    {
        var key = ProfileListKey + "\\" + sid;   // the SID passed ProfileRules.IsUserSid: digits and dashes only
        DateTime? Time(string prefix)
        {
            ct.ThrowIfCancellationRequested();
            var high = RegDword(session, op, key, prefix + "High");
            var low = RegDword(session, op, key, prefix + "Low");
            return high is { } h && low is { } l ? ProfileLastUsed.FromFileTime(h, l) : null;
        }
        return (Time("LocalProfileLoadTime"), Time("LocalProfileUnloadTime"));
    }

    private static uint? RegDword(CimSession session, CimOperationOptions op, string subKey, string name)
    {
        try
        {
            var p = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("hDefKey", HklmKey, CimType.UInt32, CimFlags.In),
                CimMethodParameter.Create("sSubKeyName", subKey, CimType.String, CimFlags.In),
                CimMethodParameter.Create("sValueName", name, CimType.String, CimFlags.In),
            };
            var r = session.InvokeMethod(RegNs, "StdRegProv", "GetDWORDValue", p, op);
            if (Convert.ToInt32(r.ReturnValue?.Value ?? -1, System.Globalization.CultureInfo.InvariantCulture) != 0) return null;
            return r.OutParameters["uValue"]?.Value is { } v ? Convert.ToUInt32(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        catch (Exception ex) when (ex is CimException or FormatException or OverflowException or InvalidCastException) { return null; }
    }

    /// <summary>
    /// Who is signed in: the console user, and every account of an interactive-type logon session (interactive, unlock, remote interactive and their
    /// cached forms), with SIDs. Throws when WMI cannot answer, so the caller never mistakes "unknown" for "nobody".
    /// </summary>
    public Task<SignedInUsers> CollectSignedInAsync(string host, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var session = Open(host);
            var op = Operation();
            var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var console = Str(Query(session, op, "SELECT UserName FROM Win32_ComputerSystem").FirstOrDefault(), "UserName");
            if (!string.IsNullOrWhiteSpace(console)) accounts.Add(console.Trim());

            var interactive = Query(session, op, "SELECT LogonId, LogonType FROM Win32_LogonSession")
                .Where(s => Num(s, "LogonType") is 2 or 7 or 10 or 11 or 12 or 13)
                .Select(s => Str(s, "LogonId") ?? "")
                .Where(id => id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit))   // goes into a query below: digits only
                .ToList();
            if (interactive.Count > MaxSignedInSessions) throw new InvalidOperationException("Too many logon sessions to check who is signed in.");   // unknown, never "nobody"
            foreach (var id in interactive)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var account in Query(session, op, $"ASSOCIATORS OF {{Win32_LogonSession.LogonId='{id}'}} WHERE AssocClass=Win32_LoggedOnUser Role=Dependent ResultRole=Antecedent"))
                {
                    if (Str(account, "SID") is { Length: > 0 } sid) sids.Add(sid);
                    var name = Str(account, "Name");
                    var domain = Str(account, "Domain");
                    if (!string.IsNullOrWhiteSpace(name)) accounts.Add(string.IsNullOrWhiteSpace(domain) ? name : $@"{domain}\{name}");
                }
            }
            return new SignedInUsers(sids.ToList(), accounts.ToList());
        }, ct);

    /// <summary>Win32_UserProfile.Status is a bit field: 1 temporary, 2 roaming, 4 mandatory, 8 corrupted.</summary>
    internal static string? StatusText(string? raw)
    {
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var flags) || flags == 0) return null;
        var parts = new List<string>();
        if ((flags & 1) != 0) parts.Add("Temporary");
        if ((flags & 2) != 0) parts.Add("Roaming");
        if ((flags & 4) != 0) parts.Add("Mandatory");
        if ((flags & 8) != 0) parts.Add("Corrupted");
        return parts.Count == 0 ? flags.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Join(", ", parts);
    }

    private static readonly System.Text.RegularExpressions.Regex SidShape = new(@"^S-1-\d{1,3}(-\d{1,10}){1,15}\z", System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private string? ResolveAccount(string sid, CimSession session)
    {
        // (the size of the cache is checked once per read, in ReadProfileRows)
        return AccountResolution.Resolve(sid, _accountCache,
            s => { try { return new SecurityIdentifier(s).Translate(typeof(NTAccount)).Value; } catch (Exception) { return null; } },
            s =>
            {
                if (!SidShape.IsMatch(s)) return null;
                try
                {
                    var row = SafeQuery(session, @"root\cimv2", $"SELECT AccountName, ReferencedDomainName FROM Win32_SID WHERE SID='{s}'", Operation()).FirstOrDefault();
                    var account = row?.CimInstanceProperties["AccountName"]?.Value as string;
                    var domain = row?.CimInstanceProperties["ReferencedDomainName"]?.Value as string;
                    return string.IsNullOrEmpty(account) ? null : string.IsNullOrEmpty(domain) ? account : $@"{domain}\{account}";
                }
                catch (Exception) { return null; }
            });
    }
}

/// <summary>
/// SID to DOMAIN\user. Only a name that was found is remembered; a failed lookup is never cached (a domain controller that was briefly
/// unreachable must not turn a live account into a "deleted" one for the life of the process). The server tries first (domain accounts), then
/// asks the TARGET PC, which is the only machine that knows its own local accounts.
/// </summary>
public static class AccountResolution
{
    public static string? Resolve(string sid, System.Collections.Concurrent.ConcurrentDictionary<string, string?> cache, Func<string, string?> onServer, Func<string, string?> onTarget)
    {
        if (cache.TryGetValue(sid, out var cached) && cached is not null) return cached;
        var name = onServer(sid) ?? onTarget(sid);
        if (name is not null) cache[sid] = name;
        return name;
    }
}
