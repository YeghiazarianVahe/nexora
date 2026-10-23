using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Nexora.Application.Services;

/// <summary>Filter shared by the list and export. Every field is optional.</summary>
public sealed record EndpointFilter(
    string? Search = null, bool? Online = null, Guid? GroupId = null,
    string? Manufacturer = null, string? Model = null, string? Ou = null,
    /// <summary>"attention" (an online PC whose weakest drive is Warning or Critical: the dashboard's drive-health alert) or "ok".</summary>
    string? Health = null)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Search) && Online is null && GroupId is null
        && string.IsNullOrEmpty(Manufacturer) && string.IsNullOrEmpty(Model) && string.IsNullOrEmpty(Ou) && string.IsNullOrEmpty(Health);
}

/// <summary>Sort keys match the table's columns.</summary>
public sealed record EndpointSort(string? Key = null, string? Dir = null);

public sealed record ExportRequest(IReadOnlyList<Guid>? Ids, EndpointFilter? Filter, EndpointSort? Sort);

/// <param name="Mode">selected, filtered or all.</param>
public sealed record ExportResult(string Mode, IReadOnlyList<ExportRow> Rows);

/// <summary>Filtering and ordering shared by the list and the export.</summary>
public static class EndpointFilters
{
    /// <summary>Upper bound of rows the list endpoint returns (the pinned Nexora server is always among them).</summary>
    public const int ListLimit = 5000;

    /// <summary>Database-side part of the filter. Used by the list endpoint and by export, so both agree.</summary>
    public static IQueryable<Nexora.Domain.ManagedEndpoint> Apply(IQueryable<Nexora.Domain.ManagedEndpoint> q, EndpointFilter f, INexoraDb db)
    {
        if (f.Online is { } o) q = q.Where(e => e.Online == o);
        if (f.GroupId is { } g) q = q.Where(e => db.GroupMembers.Any(m => m.GroupId == g && m.EndpointId == e.Id));
        if (!string.IsNullOrEmpty(f.Manufacturer)) q = q.Where(e => e.Manufacturer == f.Manufacturer);
        if (!string.IsNullOrEmpty(f.Model)) q = q.Where(e => e.Model == f.Model);
        if (!string.IsNullOrEmpty(f.Ou)) q = q.Where(e => e.OrganizationalUnit == f.Ou);
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var t = f.Search.Trim().ToLower();
            q = q.Where(e => e.Hostname.ToLower().Contains(t)
                || (e.DisplayName != null && e.DisplayName.ToLower().Contains(t))
                || (e.LastUser != null && e.LastUser.ToLower().Contains(t))
                || (e.IpAddress != null && e.IpAddress.Contains(t))
                || (e.Manufacturer != null && e.Manufacturer.ToLower().Contains(t))
                || (e.Model != null && e.Model.ToLower().Contains(t))
                || (e.OrganizationalUnit != null && e.OrganizationalUnit.ToLower().Contains(t)));
        }
        return q;
    }

    public static bool HealthMatches(EndpointSummaryDto d, string? health) => health switch
    {
        "attention" => Mapping.HasDriveHealthAlert(d.Online, d.DiskHealth),
        "ok" => d.DiskHealth == "OK",
        _ => true,
    };

    /// <summary>Natural, case-insensitive ordering ("LAB-2" before "LAB-10"), empty values last.</summary>
    public static List<EndpointSummaryDto> Sort(IEnumerable<EndpointSummaryDto> rows, EndpointSort? sort)
    {
        var key = sort?.Key ?? "name";
        var desc = string.Equals(sort?.Dir, "desc", StringComparison.OrdinalIgnoreCase);
        object? Value(EndpointSummaryDto e) => key switch
        {
            "status" => e.Online ? 1 : 0,
            "address" => e.Hostname,
            "ou" => e.OrganizationalUnit,
            "model" => string.Join(' ', new[] { e.Manufacturer, e.Model }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } m ? m : null,
            "user" => e.LastUser,
            "uptime" => e.UptimeSeconds,
            "cpu" => e.CpuPercent,
            "ram" => e.RamPercent,
            "disk" => e.DiskPercent,
            "health" => e.DiskHealthPercent,
            "ssd" => e.SsdHealthPercent,
            "hdd" => e.HddHealthPercent,
            "free" => SystemVolume.Of(e.Volumes)?.FreeBytes,
            "seen" => e.LastSeenUtc,
            _ => e.Name,
        };

        var list = rows.ToList();
        list.Sort((a, b) =>
        {
            var x = Value(a);
            var y = Value(b);
            var xe = x is null or "";
            var ye = y is null or "";
            if (xe || ye) return xe && ye ? 0 : xe ? 1 : -1;
            var c = Compare(x!, y!);
            return desc ? -c : c;
        });
        return list;
    }

    private static int Compare(object x, object y) => (x, y) switch
    {
        (string s, string t) => NaturalCompare(s, t),
        (IComparable c, _) => c.CompareTo(Convert.ChangeType(y, x.GetType(), CultureInfo.InvariantCulture)),
        _ => 0,
    };

    private static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                var si = i; var sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0'); var nb = b[sj..j].TrimStart('0');
                var byLength = na.Length.CompareTo(nb.Length);
                if (byLength != 0) return byLength;
                var byDigits = string.CompareOrdinal(na, nb);
                if (byDigits != 0) return byDigits;
            }
            else
            {
                var c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}

public interface IEndpointExportService
{
    /// <summary>Resolves the rows to export (and audits it unless <paramref name="preview"/>). Validation failures come back as Validation results.</summary>
    Task<Result<ExportResult>> PrepareAsync(Actor actor, ExportRequest request, bool preview, CancellationToken ct = default);
}

public sealed class EndpointExportService(INexoraDb db, IEndpointDtoFactory dtos, IAuditService audit) : IEndpointExportService
{
    public const int MaxIds = 5000;

    public async Task<Result<ExportResult>> PrepareAsync(Actor actor, ExportRequest request, bool preview, CancellationToken ct = default)
    {
        var hasIds = request.Ids is not null;
        var hasFilter = request.Filter is not null;
        if (hasIds == hasFilter)
            return Result<ExportResult>.Fail(ErrorKind.Validation, "Send exactly one of 'ids' or 'filter'.");
        if (hasIds && request.Ids!.Count == 0)
            return Result<ExportResult>.Fail(ErrorKind.Validation, "'ids' is empty.");
        if (hasIds && request.Ids!.Count > MaxIds)
            return Result<ExportResult>.Fail(ErrorKind.Validation, $"At most {MaxIds} ids can be exported at once.");
        if (request.Filter?.Health is { Length: > 0 } h && h is not ("attention" or "ok"))
            return Result<ExportResult>.Fail(ErrorKind.Validation, "Health must be 'attention' or 'ok'.");

        List<Nexora.Domain.ManagedEndpoint> entities;
        string mode;
        if (hasIds)
        {
            var ids = request.Ids!.Distinct().ToList();
            entities = await db.Endpoints.AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(ct);
            mode = "selected";
        }
        else
        {
            var filter = request.Filter!;
            entities = await EndpointFilters.Apply(db.Endpoints.AsNoTracking(), filter, db).ToListAsync(ct);
            mode = filter.IsEmpty ? "all" : "filtered";
        }

        var rows = await dtos.BuildAsync(entities, ct);
        if (hasFilter) rows = rows.Where(r => EndpointFilters.HealthMatches(r, request.Filter!.Health)).ToList();
        var sorted = EndpointFilters.Sort(rows, request.Sort);
        var exportRows = await WithDrivesAsync(sorted, ct);

        if (!preview)
        {
            audit.Add(actor, "endpoint.export", "Endpoint", null, null, mode, new { mode, count = sorted.Count });
            await db.SaveChangesAsync(ct);
        }
        return Result<ExportResult>.Success(new ExportResult(mode, exportRows));
    }

    /// <summary>Adds each PC's stored drives (the same snapshots the detail view reads) so the CSV can list them.</summary>
    private async Task<IReadOnlyList<ExportRow>> WithDrivesAsync(IReadOnlyList<EndpointSummaryDto> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.Id).ToList();
        var snapshots = (await db.DiskHealth.AsNoTracking().Where(d => ids.Contains(d.EndpointId)).ToListAsync(ct)).ToLookup(d => d.EndpointId);
        return rows.Select(r =>
        {
            var drives = snapshots[r.Id]
                .Select(d => new ExportDrive(d.Model, d.Serial, DriveClassifier.Classify(d.MediaType, d.BusType), d.HealthPercent, d.TemperatureC, d.PowerOnHours, d.SizeBytes))
                .ToList();
            return new ExportRow(r, drives);
        }).ToList();
    }
}

/// <summary>
/// The export: RFC 4180 CSV, ten human-friendly columns, one row per PC. Percentages are whole numbers, space is written with its unit in
/// one cell, and anything not known is blank (never 0). Text cells are protected against spreadsheet formulas.
/// </summary>
public static class EndpointCsv
{
    public static readonly string[] Header =
    [
        "PC Name", "IP Address", "Logged-in User", "Last Seen Online", "CPU %", "RAM %",
        "SSD Free / Total", "SSD Health %", "HDD Free / Total", "HDD Health %",
    ];

    public static TimeZoneInfo ExportZone { get; } = FindZone();

    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "Caucasus Standard Time", "Asia/Yerevan" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        return TimeZoneInfo.Utc;
    }

    public static async Task WriteAsync(TextWriter w, IEnumerable<ExportRow> rows, TimeZoneInfo zone, CancellationToken ct = default)
    {
        await w.WriteAsync(string.Join(',', Header) + "\r\n");
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            await w.WriteAsync(string.Join(',', Cells(row, zone)) + "\r\n");
        }
    }

    /// <summary>The PC's host name: the name it reported about itself, else the name Nexora reaches it by. (Not the display name typed into the inventory.)</summary>
    public static string? HostName(EndpointSummaryDto e) =>
        !string.IsNullOrWhiteSpace(e.ReportedHostname) ? e.ReportedHostname : e.Hostname;

    /// <summary>The cells of one row, in <see cref="Header"/> order.</summary>
    public static IReadOnlyList<string> Cells(ExportRow row, TimeZoneInfo zone)
    {
        var e = row.E;
        var ip = !string.IsNullOrWhiteSpace(e.IpAddress) ? e.IpAddress
            : System.Net.IPAddress.TryParse(e.Hostname, out _) ? e.Hostname : null;
        // The last time Nexora saw this PC online (the poll does not record a logon time, so the column says so); dd.MM.yyyy HH:mm, 24 hour, Yerevan time.
        var seen = e.LastSeenUtc is { } utc ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "";

        // CPU and RAM are live figures: blank for a PC that is offline or was never polled.
        var live = e.Online;
        var layout = DriveExport.Layout(row.Drives, e.Volumes);
        string Space(string type) => DriveExport.Space(layout, type) is { } s ? DriveExport.FormatSpace(s.Free, s.Total) : "";

        return
        [
            Text(HostName(e)), Text(ip), Text(e.LastUser), seen,
            live ? Whole(e.CpuPercent) : "", live ? Whole(e.RamPercent) : "",
            Space(DriveClassifier.Ssd), Whole(e.SsdHealthPercent), Space(DriveClassifier.Hdd), Whole(e.HddHealthPercent),
        ];
    }

    /// <summary>A whole number, rounded half away from zero; blank when unknown.</summary>
    private static string Whole(double? v) => v is { } d ? Math.Round(d, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) : "";
    private static string Whole(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>Text cell: neutralizes spreadsheet formulas (a cell that starts with = + - @, tab, CR, LF or a space), then quotes if needed.</summary>
    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n' or ' ') value = "'" + value;
        return Quote(value);
    }

    private static string Quote(string s) =>
        s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static string FileName(string mode, DateTime utcNow)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, ExportZone);
        return $"nexora-endpoints-{mode}-{local:yyyyMMdd-HHmm}.csv";
    }
}
