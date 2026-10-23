using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nexora.Domain;

namespace Nexora.Application.Services;

/// <summary>One drive as the audit shows it: what the scan saw, what class it chose, and how its health was matched.</summary>
public sealed record AuditDrive(
    string Model, string? Serial, string? BusType, string MediaTypeStored, int? MediaTypeCode, long? SpindleSpeed,
    string Class, string? ClassSource, int? HealthPercent, string? HealthSource, string? PhysicalMatchedBy, bool? SmartMatched);

public sealed record DiskAuditFinding(string Code, string Message);

public sealed record DiskAuditPc(
    Guid EndpointId, string Hostname, int? SsdHealth, int? HddHealth, int? SmartRecords, int? DriveCount,
    IReadOnlyList<AuditDrive> Drives, IReadOnlyList<DiskAuditFinding> Findings);

public sealed record DiskAuditReport(
    DateTime GeneratedUtc, int Endpoints, int WithDrives, int ScannedWithMapping, int NeedRescan, int NvmeWithoutSmartLog,
    IReadOnlyDictionary<string, int> ByFinding, IReadOnlyList<DiskAuditPc> Flagged);

/// <summary>
/// Checks, for one PC, that the SSD/HDD mapping is trustworthy. Pure: it reads only what the scan stored, so it runs over the whole
/// fleet without touching a single PC. Finding codes:
/// <list type="bullet">
/// <item><c>UnknownType</c>: a drive no signal could classify (it counts for neither the SSD nor the HDD figure).</item>
/// <item><c>NoHealth</c>: a drive has no health value (no key matched, or nothing could be read).</item>
/// <item><c>UnjoinedSmart</c>: the host returned SMART records that no drive claimed by identity.</item>
/// <item><c>CountMismatch</c>: SMART records and drives that expose SMART (everything except NVMe) differ in number.</item>
/// <item><c>ClassConflict</c>: the chosen class contradicts the rotation rate (an SSD that spins, an HDD that does not).</item>
/// <item><c>NvmeNoSmartLog</c> (informational): an NVMe drive whose health is not from its own SMART log; Windows only exposes that log on the PC it is read from, so remote NVMe health rests on the Windows reliability counter.</item>
/// <item><c>WeakSignal</c>: the class rests on a stored media-type string only, from a scan that did not record the bus or rotation rate.</item>
/// </list>
/// </summary>
public static class DiskAudit
{
    public const string UnknownType = "UnknownType", NoHealth = "NoHealth", UnjoinedSmart = "UnjoinedSmart",
        CountMismatch = "CountMismatch", ClassConflict = "ClassConflict", WeakSignal = "WeakSignal", NvmeNoSmartLog = "NvmeNoSmartLog";

    /// <summary>Findings that describe a limit of the data rather than a wrong mapping; they do not put a PC on the flagged list.</summary>
    public static bool IsInformational(string code) => code is WeakSignal or NvmeNoSmartLog;

    public static DiskAuditPc? Evaluate(Guid endpointId, string hostname, IReadOnlyList<DiskHealthSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return null;

        var drives = snapshots.OrderBy(s => s.DiskKey, StringComparer.Ordinal).Select(s => new AuditDrive(
            s.Model, s.Serial, s.BusType, s.MediaType, s.MediaTypeCode, s.SpindleSpeed, DriveClassifier.Classify(s.MediaType, s.BusType),
            s.ClassSource, s.HealthPercent, s.HealthSource, s.PhysicalMatch, s.SmartMatched)).ToList();

        var findings = new List<DiskAuditFinding>();
        foreach (var d in drives)
        {
            var name = d.Serial is { Length: > 0 } ? $"{d.Model} ({d.Serial})" : d.Model;
            if (d.Class == DriveClassifier.Unknown)
                findings.Add(new(UnknownType, $"{name}: no signal says SSD or HDD (bus {d.BusType ?? "-"}, media {d.MediaTypeStored}, spindle {d.SpindleSpeed?.ToString() ?? "-"})."));
            if (d.HealthPercent is null)
                findings.Add(new(NoHealth, $"{name}: no health value (matched by {d.PhysicalMatchedBy ?? "-"}, source {d.HealthSource ?? "-"})."));
            if (d.SpindleSpeed is { } sp && sp != DriveClassifier.SpindleUnknown
                && ((d.Class == DriveClassifier.Ssd && sp > 0 && !string.Equals(d.BusType, "NVMe", StringComparison.OrdinalIgnoreCase)) || (d.Class == DriveClassifier.Hdd && sp == 0)))
                findings.Add(new(ClassConflict, $"{name}: classified {d.Class} but the rotation rate is {sp}."));
            if (d.ClassSource is not null && string.Equals(d.BusType, "NVMe", StringComparison.OrdinalIgnoreCase) && d.HealthSource is not ("nvme-smart-log" or "smart-attributes"))
                findings.Add(new(NvmeNoSmartLog, $"{name}: the native NVMe health log was unavailable during the last scan; only Windows evidence was collected. Refresh the drive scan to retry."));
            if (d.ClassSource is null && d.Class != DriveClassifier.Unknown)
                findings.Add(new(WeakSignal, $"{name}: classified {d.Class} from a stored media-type label only; rescan to record the bus and rotation rate."));
        }

        // The host-level counts exist only on rows scanned with the mapping recorded.
        var withMapping = snapshots.Where(s => s.HostSmartRecords.HasValue).ToList();
        int? smartRecords = withMapping.Count > 0 ? withMapping[0].HostSmartRecords : null;
        int? driveCount = withMapping.Count > 0 ? withMapping[0].HostDriveCount : null;
        if (smartRecords is { } records)
        {
            // NVMe drives normally have no ATA-style SMART record; one that does (behind a bridge) counts like any other.
            var smartCapable = snapshots.Count(s => !string.Equals(s.BusType, "NVMe", StringComparison.OrdinalIgnoreCase) || s.SmartMatched == true);
            var claimed = snapshots.Count(s => s.SmartMatched == true);
            if (records > claimed)
                findings.Add(new(UnjoinedSmart, $"{records - claimed} SMART record(s) matched no drive by serial, PnP id or disk number."));
            if (records != smartCapable)
                findings.Add(new(CountMismatch, $"{records} SMART record(s) for {smartCapable} drive(s) that expose SMART (NVMe drives without one excluded)."));
        }

        var (ssd, hdd) = DriveClassifier.Worst(snapshots.Select(s => new DriveHealthInput(s.MediaType, s.BusType, s.HealthPercent)));
        return new DiskAuditPc(endpointId, hostname, ssd, hdd, smartRecords, driveCount, drives, findings);
    }

    public static string Csv(IEnumerable<DiskAuditPc> flagged)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Hostname,Findings,SsdHealthWorst,HddHealthWorst,SmartRecords,PhysicalDrives,Drive,Model,SerialNumber,BusType,MediaTypeCode,MediaTypeStored,SpindleSpeed,Class,ClassSource,HealthPercent,HealthSource,PhysicalMatchedBy,SmartMatched\r\n");
        foreach (var pc in flagged)
        {
            var n = 0;
            foreach (var d in pc.Drives)
            {
                n++;
                string[] cells =
                [
                    EndpointCsv.Text(pc.Hostname), EndpointCsv.Text(string.Join(' ', pc.Findings.Select(f => f.Code).Distinct())),
                    Num(pc.SsdHealth), Num(pc.HddHealth), Num(pc.SmartRecords), Num(pc.DriveCount), n.ToString(CultureInfo.InvariantCulture),
                    EndpointCsv.Text(d.Model), EndpointCsv.Text(d.Serial), EndpointCsv.Text(d.BusType), Num(d.MediaTypeCode), EndpointCsv.Text(d.MediaTypeStored), Num(d.SpindleSpeed),
                    d.Class, EndpointCsv.Text(d.ClassSource), Num(d.HealthPercent), EndpointCsv.Text(d.HealthSource), EndpointCsv.Text(d.PhysicalMatchedBy), d.SmartMatched?.ToString().ToLowerInvariant() ?? "",
                ];
                sb.Append(string.Join(',', cells)).Append("\r\n");
            }
        }
        return sb.ToString();

        static string Num(IFormattable? v) => v is null ? "" : v.ToString(null, CultureInfo.InvariantCulture);
    }
}

public interface IDiskAuditService
{
    Task<DiskAuditReport> RunAsync(CancellationToken ct = default);
    /// <summary>Marks every PC's drives as due, so the monitor re-reads them on its next pass and records the mapping.</summary>
    Task<Result<int>> RescanAllAsync(Actor actor, CancellationToken ct = default);
}

public sealed class DiskAuditService(INexoraDb db, IAuditService audit, ISystemClock clock) : IDiskAuditService
{
    public async Task<DiskAuditReport> RunAsync(CancellationToken ct = default)
    {
        var endpoints = await db.Endpoints.AsNoTracking().Select(e => new { e.Id, e.Hostname, e.DisplayName }).ToListAsync(ct);
        var byEndpoint = (await db.DiskHealth.AsNoTracking().ToListAsync(ct)).ToLookup(s => s.EndpointId);

        var flagged = new List<DiskAuditPc>();
        int withDrives = 0, mapped = 0, legacy = 0, nvme = 0;
        foreach (var e in endpoints.OrderBy(x => x.DisplayName ?? x.Hostname, StringComparer.OrdinalIgnoreCase))
        {
            var snaps = byEndpoint[e.Id].ToList();
            if (snaps.Count == 0) continue;
            withDrives++;
            if (snaps.All(s => s.ClassSource is not null)) mapped++; else legacy++;
            var pc = DiskAudit.Evaluate(e.Id, e.DisplayName ?? e.Hostname, snaps);
            // A WeakSignal alone just means "not rescanned yet"; it is counted as legacy, not as a flagged PC.
            if (pc is null) continue;
            if (pc.Findings.Any(f => f.Code == DiskAudit.NvmeNoSmartLog)) nvme++;
            if (pc.Findings.Any(f => !DiskAudit.IsInformational(f.Code))) flagged.Add(pc);
        }

        var by = flagged.SelectMany(p => p.Findings.Select(f => f.Code).Distinct()).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        return new DiskAuditReport(clock.UtcNow, endpoints.Count, withDrives, mapped, legacy, nvme, by, flagged);
    }

    public async Task<Result<int>> RescanAllAsync(Actor actor, CancellationToken ct = default)
    {
        int n;
        try { n = await db.Endpoints.ExecuteUpdateAsync(s => s.SetProperty(e => e.LastDiskPollUtc, (DateTime?)null), ct); }
        catch (InvalidOperationException)
        {
            // Providers without set-based updates (the in-memory test provider).
            var all = await db.Endpoints.ToListAsync(ct);
            foreach (var e in all) e.LastDiskPollUtc = null;
            n = all.Count;
        }
        audit.Add(actor, "diskaudit.rescan", "Endpoint", null, n.ToString(CultureInfo.InvariantCulture), null, new { count = n });
        await db.SaveChangesAsync(ct);
        return Result<int>.Success(n);
    }
}
