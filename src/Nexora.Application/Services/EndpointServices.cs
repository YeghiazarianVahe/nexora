using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nexora.Domain;

namespace Nexora.Application.Services;

// ---------- Collection contract (implemented over WMI/CIM in Infrastructure) ----------

/// <summary>A fixed volume read during the poll.</summary>
public sealed record VolumeSample(string Letter, string? FileSystem, long TotalBytes, long FreeBytes);

public sealed record EndpointSample(
    string? IpAddress, string? LastUser, string? OsCaption, string? OsVersion, DateTime? BootTimeUtc,
    double? CpuPercent, double? RamPercent, double? DiskPercent, string? ReportedHostname = null,
    string? Manufacturer = null, string? Model = null,
    long? RamTotalBytes = null, IReadOnlyList<VolumeSample>? Volumes = null, long? RamFreeBytes = null);

public sealed record ServiceInfo(string Name, string DisplayName, string State, string StartMode);
public sealed record ProcessInfo(int Pid, string Name, long WorkingSetBytes, string? Owner, double? CpuPercent = null);
public sealed record DiskInfo(string Drive, string? Label, long SizeBytes, long FreeBytes);
public sealed record SoftwareInfo(string Name, string? Version, string? Publisher);
public sealed record EventInfo(DateTime TimeUtc, string Source, int EventId, string Log, string Message);

public sealed record EndpointDetailDto(
    IReadOnlyList<ServiceInfo> Services, IReadOnlyList<ProcessInfo> Processes, IReadOnlyList<DiskInfo> Disks,
    IReadOnlyList<SoftwareInfo> Software, IReadOnlyList<EventInfo> Errors, IReadOnlyList<string> Warnings);

public interface IEndpointCollector
{
    Task<EndpointSample> CollectSampleAsync(string host, CancellationToken ct);
    Task<EndpointDetailDto> CollectDetailAsync(string host, CancellationToken ct);
    /// <summary>Physical drives with SMART / reliability data. Read-only.</summary>
    Task<IReadOnlyList<DiskRaw>> CollectDisksAsync(string host, CancellationToken ct);
    /// <summary>Local user profiles (System Properties > Advanced > User Profiles). Read-only.</summary>
    Task<IReadOnlyList<ProfileInfo>> CollectProfilesAsync(string host, CancellationToken ct);
}

// ---------- Settings ----------

public interface ISettingsService
{
    Task<PlatformSettings> GetAsync(CancellationToken ct = default);
    Task<Result<PlatformSettings>> UpdateAsync(Actor actor, PlatformSettings settings, CancellationToken ct = default);
}

public sealed class SettingsService(INexoraDb db, IAuditService audit, ISystemClock clock) : ISettingsService
{
    public async Task<PlatformSettings> GetAsync(CancellationToken ct = default)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null) return new PlatformSettings();
        try { return JsonSerializer.Deserialize<PlatformSettings>(row.Json) ?? new PlatformSettings(); }
        catch (JsonException) { return new PlatformSettings(); }
    }

    public async Task<Result<PlatformSettings>> UpdateAsync(Actor actor, PlatformSettings settings, CancellationToken ct = default)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        var old = row?.Json;
        if (!settings.ProtectedServicesSpecified && old is not null)
        {
            // A save that does not mention the list keeps what is stored.
            try { settings.ProtectedServices = JsonSerializer.Deserialize<PlatformSettings>(old)?.ProtectedServices ?? []; }
            catch (JsonException) { /* an unreadable stored row has nothing to keep */ }
        }
        if (!settings.DiagExchangeHostsSpecified && old is not null)
        {
            // Same for the diagnosis Exchange hosts: a save that does not mention them keeps what is stored.
            try { settings.DiagExchangeHosts = JsonSerializer.Deserialize<PlatformSettings>(old)?.DiagExchangeHosts ?? []; }
            catch (JsonException) { /* an unreadable stored row has nothing to keep */ }
        }
        if (old is not null)
        {
            // Limits the document does not mention keep their stored values.
            try { if (JsonSerializer.Deserialize<PlatformSettings>(old) is { } stored) settings.KeepStoredTempLimits(stored); }
            catch (JsonException) { /* nothing readable to keep */ }
        }
        settings.Normalize();
        if (settings.Validate() is { } error) return Result<PlatformSettings>.Fail(ErrorKind.Validation, error);

        var json = JsonSerializer.Serialize(settings);
        if (row is null) db.Settings.Add(new SettingsRow { Id = 1, Json = json, UpdatedUtc = clock.UtcNow });
        else { row.Json = json; row.UpdatedUtc = clock.UtcNow; }

        audit.Add(actor, "settings.update", "Settings", "platform", old, json);
        await db.SaveChangesAsync(ct);
        return Result<PlatformSettings>.Success(settings);
    }
}

// ---------- Endpoint DTO assembly ----------

public interface IEndpointDtoFactory
{
    Task<IReadOnlyList<EndpointSummaryDto>> BuildAsync(IReadOnlyList<ManagedEndpoint> endpoints, CancellationToken ct = default);
    Task<EndpointSummaryDto> BuildOneAsync(ManagedEndpoint endpoint, CancellationToken ct = default);
}

public sealed class EndpointDtoFactory(INexoraDb db, ISettingsService settings, ISystemClock clock) : IEndpointDtoFactory
{
    public async Task<IReadOnlyList<EndpointSummaryDto>> BuildAsync(IReadOnlyList<ManagedEndpoint> endpoints, CancellationToken ct = default)
    {
        if (endpoints.Count == 0) return [];
        var s = await settings.GetAsync(ct);
        // Only the rows of the endpoints asked for: one poll result must not read every group member and drive of the fleet.
        var ids = endpoints.Select(e => e.Id).ToList();

        var groups = (await db.GroupMembers.AsNoTracking().Where(m => ids.Contains(m.EndpointId)).ToListAsync(ct))
            .GroupBy(m => m.EndpointId).ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(m => m.GroupId).ToList());

        var diskRows = await db.DiskHealth.AsNoTracking().Where(d => ids.Contains(d.EndpointId))
            .Select(d => new { d.EndpointId, d.Status, d.HealthPercent, d.MediaType, d.BusType, d.Model }).ToListAsync(ct);
        var disks = diskRows.GroupBy(d => d.EndpointId)
            .ToDictionary(g => g.Key, g => g.Select(d => d.Status).OrderByDescending(DiskStatus.Rank).First());
        // The PC's drive health is its weakest drive.
        var health = diskRows.Where(d => d.HealthPercent.HasValue).GroupBy(d => d.EndpointId)
            .ToDictionary(g => g.Key, g => (int?)g.Min(d => d.HealthPercent!.Value));

        var split = diskRows.GroupBy(d => d.EndpointId)
            .ToDictionary(g => g.Key, g => DriveClassifier.Worst(g.Select(d => new DriveHealthInput(d.MediaType, d.BusType, d.HealthPercent))));

        var now = clock.UtcNow;
        return endpoints.Select(e =>
        {
            var (ssd, hdd) = split.GetValueOrDefault(e.Id);
            return e.ToDto(s, now, groups.GetValueOrDefault(e.Id), disks.GetValueOrDefault(e.Id), health.GetValueOrDefault(e.Id), ssd, hdd);
        }).ToList();
    }

    public async Task<EndpointSummaryDto> BuildOneAsync(ManagedEndpoint endpoint, CancellationToken ct = default) =>
        (await BuildAsync([endpoint], ct))[0];
}

// ---------- Endpoints ----------

public static partial class HostnameRules
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{0,62}\z")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"^[\p{L}\p{N} ._\-()#]{1,100}\z")]
    private static partial Regex NamePattern();

    /// <summary>A host name or an IPv4 address (dots and digits are legal host-name characters).</summary>
    public static bool IsValid(string? host) => host is not null && Pattern().IsMatch(host);

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);
}

/// <summary>A host name as the diagnosis may test it: letters, digits, dots and hyphens, 253 characters at most, no scheme, path, port or address list.</summary>
public static partial class ExchangeHostRules
{
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9.\-]{0,251}[A-Za-z0-9])?\z")]
    private static partial Regex Pattern();

    public static bool IsValid(string? host) => host is not null && Pattern().IsMatch(host);
}

public interface IEndpointService
{
    Task<IReadOnlyList<EndpointSummaryDto>> ListAsync(string? search, bool? online, Guid? groupId, CancellationToken ct = default);
    Task<EndpointSummaryDto?> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Case-insensitive lookup by host name. NotFound when unknown; Conflict when two rows differ only by case.</summary>
    Task<Result<EndpointSummaryDto>> GetByHostAsync(string host, CancellationToken ct = default);
    Task<Result<EndpointDetailDto>> GetDetailAsync(Guid id, CancellationToken ct = default);
    Task<Result<IReadOnlyList<EndpointSummaryDto>>> AddAsync(Actor actor, IReadOnlyList<EndpointInput> items, IReadOnlyList<Guid>? groupIds, CancellationToken ct = default);
    Task<Result<EndpointSummaryDto>> UpdateAsync(Actor actor, Guid id, string? name, string? address, IReadOnlyList<Guid>? groupIds, CancellationToken ct = default);
    Task<Result> RemoveAsync(Actor actor, Guid id, CancellationToken ct = default);
    /// <summary>Adds the workstations to some groups and removes them from others, in one audited step.</summary>
    Task<Result<int>> BulkGroupsAsync(Actor actor, IReadOnlyList<Guid> ids, IReadOnlyList<Guid>? addGroupIds, IReadOnlyList<Guid>? removeGroupIds, CancellationToken ct = default);
    /// <summary>Removes the given workstations, or every workstation when <paramref name="ids"/> is null. Returns how many were removed.</summary>
    Task<Result<int>> RemoveManyAsync(Actor actor, IReadOnlyList<Guid>? ids, int? expectedCount = null, CancellationToken ct = default);
    /// <summary>How many workstations "delete all" would remove right now (everything except the pinned Nexora server).</summary>
    Task<int> CountRemovableAsync(CancellationToken ct = default);
    Task<Result<IReadOnlyList<EndpointSummaryDto>>> ImportFromDirectoryAsync(Actor actor, string? prefix, CancellationToken ct = default);
}

public sealed class EndpointService(
    INexoraDb db,
    IEndpointCollector collector,
    IDirectoryLookup directory,
    IEndpointDtoFactory dtos,
    IAuditService audit,
    INexoraNotifier notifier,
    ISystemClock clock,
    Microsoft.Extensions.Logging.ILogger<EndpointService> logger, DetailReadGate detailReads) : IEndpointService
{
    public async Task<IReadOnlyList<EndpointSummaryDto>> ListAsync(string? search, bool? online, Guid? groupId, CancellationToken ct = default)
    {
        var q = EndpointFilters.Apply(db.Endpoints.AsNoTracking(), new EndpointFilter(search, online, groupId), db);
        // Pinned endpoints (the Nexora server) come first, so no cap or sort order can ever push them out of the list.
        var rows = await q.OrderByDescending(e => e.IsPinned).ThenBy(e => e.DisplayName ?? e.Hostname).Take(EndpointFilters.ListLimit).ToListAsync(ct);
        return await dtos.BuildAsync(rows, ct);
    }

    public async Task<EndpointSummaryDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var e = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? null : await dtos.BuildOneAsync(e, ct);
    }

    public async Task<Result<EndpointSummaryDto>> GetByHostAsync(string host, CancellationToken ct = default)
    {
        if (!HostnameRules.IsValid(host)) return Result<EndpointSummaryDto>.Fail(ErrorKind.Validation, "Invalid host name.");
        var key = host.ToLowerInvariant();
        var rows = await db.Endpoints.AsNoTracking().Where(x => x.Hostname.ToLower() == key).Take(2).ToListAsync(ct);
        if (rows.Count == 0) return Result<EndpointSummaryDto>.Fail(ErrorKind.NotFound, "Endpoint not found.");
        if (rows.Count > 1) return Result<EndpointSummaryDto>.Fail(ErrorKind.Conflict, "More than one workstation matches that name. Use the workstation id instead.");
        return Result<EndpointSummaryDto>.Success(await dtos.BuildOneAsync(rows[0], ct));
    }

    public async Task<Result<EndpointDetailDto>> GetDetailAsync(Guid id, CancellationToken ct = default)
    {
        var e = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return Result<EndpointDetailDto>.Fail(ErrorKind.NotFound, "Endpoint not found.");
        try { return Result<EndpointDetailDto>.Success(await detailReads.RunAsync(c => collector.CollectDetailAsync(e.Hostname, c), ct)); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, ex, "Detail query of {Host} failed", e.Hostname);
            return Result<EndpointDetailDto>.Fail(ErrorKind.Unavailable, $"Could not query {e.Hostname}. It may be off, unreachable, or refusing remote management. Details are in the server log.");
        }
    }

    public async Task<Result<IReadOnlyList<EndpointSummaryDto>>> AddAsync(
        Actor actor, IReadOnlyList<EndpointInput> items, IReadOnlyList<Guid>? groupIds, CancellationToken ct = default)
    {
        if (items.Count is 0 or > 500)
            return Result<IReadOnlyList<EndpointSummaryDto>>.Fail(ErrorKind.Validation, "Provide 1-500 workstations.");

        var cleaned = new List<EndpointInput>();
        foreach (var item in items)
        {
            var address = item.Address?.Trim() ?? "";
            if (!HostnameRules.IsValid(address))
                return Result<IReadOnlyList<EndpointSummaryDto>>.Fail(ErrorKind.Validation, $"Invalid IP address or host name: '{address}'.");
            var name = item.Name?.Trim();
            if (!string.IsNullOrEmpty(name) && !HostnameRules.IsValidName(name))
                return Result<IReadOnlyList<EndpointSummaryDto>>.Fail(ErrorKind.Validation, $"Invalid workstation name: '{name}'.");
            cleaned.Add(new EndpointInput(string.IsNullOrEmpty(name) ? null : name, address));
        }
        cleaned = cleaned.DistinctBy(c => c.Address, StringComparer.OrdinalIgnoreCase).ToList();

        var groups = await ResolveGroupsAsync(groupIds, ct);
        if (groups is null) return Result<IReadOnlyList<EndpointSummaryDto>>.Fail(ErrorKind.Validation, "Unknown group.");

        return await InsertAsync(actor, cleaned, groups, "endpoint.add", ct);
    }

    public async Task<Result<EndpointSummaryDto>> UpdateAsync(
        Actor actor, Guid id, string? name, string? address, IReadOnlyList<Guid>? groupIds, CancellationToken ct = default)
    {
        var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return Result<EndpointSummaryDto>.Fail(ErrorKind.NotFound, "Endpoint not found.");

        var changes = new List<string>();
        if (name is not null)
        {
            name = name.Trim();
            if (name.Length > 0 && !HostnameRules.IsValidName(name))
                return Result<EndpointSummaryDto>.Fail(ErrorKind.Validation, "Invalid workstation name.");
            var newName = name.Length == 0 ? null : name;
            if (newName != e.DisplayName) { changes.Add($"name {e.DisplayName ?? "-"} -> {newName ?? "-"}"); e.DisplayName = newName; }
        }
        if (address is not null && !string.Equals(address.Trim(), e.Hostname, StringComparison.Ordinal))
        {
            address = address.Trim();
            if (!HostnameRules.IsValid(address))
                return Result<EndpointSummaryDto>.Fail(ErrorKind.Validation, "Invalid IP address or host name.");
            if (await db.Endpoints.AnyAsync(x => x.Hostname.ToLower() == address.ToLower() && x.Id != id, ct))
                return Result<EndpointSummaryDto>.Fail(ErrorKind.Conflict, "Another workstation already uses that address.");
            changes.Add($"address {e.Hostname} -> {address}");
            e.Hostname = address;
            e.Online = false; e.ConsecutiveFailures = 0; e.LastError = null; e.LastPolledUtc = null;
        }
        if (groupIds is not null)
        {
            var groups = await ResolveGroupsAsync(groupIds, ct);
            if (groups is null) return Result<EndpointSummaryDto>.Fail(ErrorKind.Validation, "Unknown group.");
            var current = await db.GroupMembers.Where(m => m.EndpointId == id).ToListAsync(ct);
            var wanted = groups.Select(g => g.Id).ToHashSet();
            db.GroupMembers.RemoveRange(current.Where(m => !wanted.Contains(m.GroupId)));
            foreach (var gid in wanted.Except(current.Select(m => m.GroupId)))
                db.GroupMembers.Add(new EndpointGroupMember { GroupId = gid, EndpointId = id });
            changes.Add($"groups {current.Count} -> {wanted.Count}");
        }

        if (changes.Count > 0)
        {
            audit.Add(actor, "endpoint.update", "Endpoint", e.Hostname, null, string.Join("; ", changes));
            await db.SaveChangesAsync(ct);
            await notifier.DashboardChangedAsync();
        }
        return Result<EndpointSummaryDto>.Success(await dtos.BuildOneAsync(e, ct));
    }

    public async Task<Result> RemoveAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        var e = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return Result.Fail(ErrorKind.NotFound, "Endpoint not found.");
        if (e.IsPinned) return Result.Fail(ErrorKind.Conflict, "The Nexora server is always part of the inventory and cannot be removed.");

        // Set-based deletes, all-or-nothing together with the audit row: no history is loaded into memory, and a failure leaves everything as it was.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await DeleteRowsAsync([id], ct);
        audit.Add(actor, "endpoint.remove", "Endpoint", id.ToString(), e.Hostname, null, new { mode = "single", count = 1, idsSha256 = IdsHash([id]) });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await notifier.DashboardChangedAsync();
        return Result.Success();
    }

    /// <summary>Largest explicit selection one call may remove.</summary>
    public const int MaxRemovePerCall = 500;
    private const int ChunkSize = 500;

    /// <summary>SHA-256 over the sorted ids, so an audit entry can later prove exactly which workstations a delete covered.</summary>
    public static string IdsHash(IEnumerable<Guid> ids) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(string.Join(',', ids.OrderBy(i => i).Select(i => i.ToString("N"))))));

    public async Task<Result<int>> BulkGroupsAsync(
        Actor actor, IReadOnlyList<Guid> ids, IReadOnlyList<Guid>? addGroupIds, IReadOnlyList<Guid>? removeGroupIds, CancellationToken ct = default)
    {
        var add = (addGroupIds ?? []).Distinct().ToList();
        var remove = (removeGroupIds ?? []).Distinct().ToList();
        if (ids.Count == 0 || (add.Count == 0 && remove.Count == 0)) return Result<int>.Fail(ErrorKind.Validation, "Pick workstations and at least one group change.");
        if (add.Intersect(remove).Any()) return Result<int>.Fail(ErrorKind.Validation, "A group cannot be both added and removed.");
        if (ids.Count > 5000) return Result<int>.Fail(ErrorKind.Validation, "Too many workstations in one step.");

        var wanted = add.Concat(remove).ToList();
        var groups = await db.Groups.Where(g => wanted.Contains(g.Id)).ToListAsync(ct);
        if (groups.Count != wanted.Count) return Result<int>.Fail(ErrorKind.Validation, "Unknown group.");

        var known = (await db.Endpoints.Where(e => ids.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        var existing = (await db.GroupMembers.Where(m => wanted.Contains(m.GroupId)).ToListAsync(ct))
            .Where(m => known.Contains(m.EndpointId)).ToList();

        foreach (var g in add)
            foreach (var e in known.Where(e => !existing.Any(m => m.GroupId == g && m.EndpointId == e)))
                db.GroupMembers.Add(new EndpointGroupMember { GroupId = g, EndpointId = e });
        db.GroupMembers.RemoveRange(existing.Where(m => remove.Contains(m.GroupId)));

        audit.Add(actor, "endpoint.bulk_groups", "Endpoint", null, null, null,
            new { count = known.Count, added = groups.Where(g => add.Contains(g.Id)).Select(g => g.Name), removed = groups.Where(g => remove.Contains(g.Id)).Select(g => g.Name) });
        await db.SaveChangesAsync(ct);
        await notifier.DashboardChangedAsync();
        return Result<int>.Success(known.Count);
    }

    public async Task<int> CountRemovableAsync(CancellationToken ct = default) =>
        await db.Endpoints.CountAsync(e => !e.IsPinned, ct);

    public async Task<Result<int>> RemoveManyAsync(Actor actor, IReadOnlyList<Guid>? ids, int? expectedCount = null, CancellationToken ct = default)
    {
        var all = ids is null;
        if (ids is { Count: 0 }) return Result<int>.Fail(ErrorKind.Validation, "Nothing selected.");
        if (ids is not null)
        {
            ids = ids.Distinct().ToList();
            if (ids.Count > MaxRemovePerCall)
                return Result<int>.Fail(ErrorKind.Validation, $"At most {MaxRemovePerCall} workstations can be removed in one request ({ids.Count} were sent). Remove them in smaller steps, or use delete all.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        List<(Guid Id, string Hostname)> targets;
        if (all)
        {
            targets = (await db.Endpoints.Where(e => !e.IsPinned).Select(e => new { e.Id, e.Hostname }).ToListAsync(ct)).Select(e => (e.Id, e.Hostname)).ToList();
            // Delete-all must be exactly what the person saw and confirmed: if the inventory changed in between, nothing is removed.
            if (expectedCount is null || expectedCount != targets.Count)
                return Result<int>.Fail(ErrorKind.Conflict, $"The inventory changed: {targets.Count} workstations would be removed now, not {expectedCount?.ToString() ?? "the number confirmed"}. Reload and confirm again.");
        }
        else
        {
            var wanted = ids!;
            targets = (await db.Endpoints.Where(e => wanted.Contains(e.Id) && !e.IsPinned).Select(e => new { e.Id, e.Hostname }).ToListAsync(ct)).Select(e => (e.Id, e.Hostname)).ToList();
        }
        if (targets.Count == 0) return Result<int>.Success(0);
        var keys = targets.Select(t => t.Id).ToList();

        // Chunked set-based deletes (bounded parameter lists, no history loaded) and the audit row, in ONE transaction: all of it or none of it.
        foreach (var chunk in keys.Chunk(ChunkSize)) await DeleteRowsAsync(chunk, ct);

        audit.Add(actor, all ? "endpoint.remove_all" : "endpoint.remove_many", "Endpoint", null, targets.Count.ToString(), null,
            new { mode = all ? "all" : "selected", count = targets.Count, idsSha256 = IdsHash(keys), hosts = targets.Select(t => t.Hostname).Take(50) });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await notifier.DashboardChangedAsync();
        return Result<int>.Success(targets.Count);
    }

    private async Task DeleteRowsAsync(IReadOnlyCollection<Guid> keys, CancellationToken ct)
    {
        await DeleteWhereAsync(db.MetricSamples, m => keys.Contains(m.EndpointId), ct);
        await DeleteWhereAsync(db.DiskTemps, m => keys.Contains(m.EndpointId), ct);
        await DeleteWhereAsync(db.DiskHealth, m => keys.Contains(m.EndpointId), ct);
        await DeleteWhereAsync(db.EndpointInventories, m => keys.Contains(m.EndpointId), ct);
        await DeleteWhereAsync(db.GroupMembers, m => keys.Contains(m.EndpointId), ct);
        await DeleteWhereAsync(db.Endpoints, m => keys.Contains(m.Id), ct);
    }

    private static async Task DeleteWhereAsync<T>(DbSet<T> set, System.Linq.Expressions.Expression<Func<T, bool>> where, CancellationToken ct)
        where T : class
    {
        try { await set.Where(where).ExecuteDeleteAsync(ct); }
        catch (InvalidOperationException)
        {
            // Providers without set-based deletes (the in-memory test provider): tracked deletes, saved with the audit entry.
            set.RemoveRange(await set.Where(where).ToListAsync(ct));
        }
    }

    public async Task<Result<IReadOnlyList<EndpointSummaryDto>>> ImportFromDirectoryAsync(
        Actor actor, string? prefix, CancellationToken ct = default)
    {
        IReadOnlyList<DirectoryComputer> computers;
        try { computers = directory.SearchComputers(prefix, 1000); }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, ex, "Directory query failed");
            return Result<IReadOnlyList<EndpointSummaryDto>>.Fail(ErrorKind.Unavailable, "The directory (Active Directory) could not be queried. Details are in the server log.");
        }
        var valid = computers.Where(c => HostnameRules.IsValid(c.Hostname)).ToList();
        var items = valid.Select(c => new EndpointInput(null, c.Hostname)).ToList();
        var inserted = await InsertAsync(actor, items, [], "endpoint.import", ct);
        if (!inserted.Ok) return inserted;
        var added = inserted.Value!;

        // Remember each computer's OU right away (also fixes ones imported earlier).
        var ou = valid.Where(c => c.OrganizationalUnit != null).ToDictionary(c => c.Hostname, c => c.OrganizationalUnit!, StringComparer.OrdinalIgnoreCase);
        if (ou.Count > 0)
        {
            var rows = await db.Endpoints.Where(e => e.OrganizationalUnit == null).ToListAsync(ct);
            foreach (var e in rows.Where(r => ou.ContainsKey(r.Hostname)))
            {
                e.OrganizationalUnit = ou[e.Hostname];
                e.OuCheckedUtc = clock.UtcNow;
                e.OuLookupStatus = OuStatuses.Found;
                e.OuLookupError = null;
            }
            await db.SaveChangesAsync(ct);
        }
        return Result<IReadOnlyList<EndpointSummaryDto>>.Success(added);
    }

    private async Task<List<EndpointGroup>?> ResolveGroupsAsync(IReadOnlyList<Guid>? groupIds, CancellationToken ct)
    {
        if (groupIds is null || groupIds.Count == 0) return [];
        var ids = groupIds.Distinct().ToList();
        var groups = await db.Groups.Where(g => ids.Contains(g.Id)).ToListAsync(ct);
        return groups.Count == ids.Count ? groups : null;
    }

    private async Task<Result<IReadOnlyList<EndpointSummaryDto>>> InsertAsync(
        Actor actor, List<EndpointInput> items, List<EndpointGroup> groups, string action, CancellationToken ct)
    {
        var existing = (await db.Endpoints.Select(e => e.Hostname).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<ManagedEndpoint>();
        foreach (var item in items.Where(i => !existing.Contains(i.Address)))
        {
            var e = new ManagedEndpoint { Id = Guid.NewGuid(), Hostname = item.Address, DisplayName = item.Name, CreatedUtc = clock.UtcNow };
            db.Endpoints.Add(e);
            foreach (var g in groups) db.GroupMembers.Add(new EndpointGroupMember { GroupId = g.Id, EndpointId = e.Id });
            added.Add(e);
        }
        if (added.Count > 0)
        {
            audit.Add(actor, action, "Endpoint", null, null, string.Join(",", added.Select(a => a.DisplayName ?? a.Hostname).Take(50)),
                new { count = added.Count, groups = groups.Select(g => g.Name) });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Two requests added the same host name (any case) at once; the unique indexes decide.
                // Detach only what this attempt added (endpoints, memberships, its audit entry); anything else tracked stays.
                var mine = added.Select(a => a.Id).ToHashSet();
                foreach (var entry in db.ChangeTracker.Entries().Where(en => en.State == EntityState.Added && (
                             (en.Entity is ManagedEndpoint me && mine.Contains(me.Id))
                             || (en.Entity is EndpointGroupMember gm && mine.Contains(gm.EndpointId))
                             || (en.Entity is AuditEntry ae && ae.Action == action))).ToList())
                    entry.State = EntityState.Detached;
                await AuditFailureAsync(actor, action, added, groups);
                return Result<IReadOnlyList<EndpointSummaryDto>>.Fail(ErrorKind.Conflict, "A workstation with that address already exists.");
            }
            await notifier.DashboardChangedAsync();
        }
        return Result<IReadOnlyList<EndpointSummaryDto>>.Success(await dtos.BuildAsync(added, ct));
    }

    /// <summary>The refused add/import leaves a success=false audit row of its own (own time budget; a failing write never breaks the response).</summary>
    private async Task AuditFailureAsync(Actor actor, string action, List<ManagedEndpoint> attempted, List<EndpointGroup> groups)
    {
        try
        {
            audit.Add(actor, action, "Endpoint", null, null, string.Join(",", attempted.Select(a => a.DisplayName ?? a.Hostname).Take(50)),
                new { count = attempted.Count, groups = groups.Select(g => g.Name), error = "A workstation with that address already exists." }, success: false);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await db.SaveChangesAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(logger, ex, "Could not write the failed {Action} audit entry", action);
            foreach (var e in db.ChangeTracker.Entries<AuditEntry>().Where(e => e.State == EntityState.Added && e.Entity.Action == action && !e.Entity.Success).ToList())
                e.State = EntityState.Detached;
        }
    }

    /// <summary>PostgreSQL 23505, read without a provider reference (the Application layer does not depend on Npgsql).</summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.GetType().GetProperty("SqlState")?.GetValue(ex.InnerException) as string == "23505";
}

// ---------- Dashboard ----------

public interface IDashboardService
{
    Task<DashboardDto> GetAsync(CancellationToken ct = default);
}

public sealed class DashboardService(INexoraDb db, IEndpointDtoFactory dtoFactory, ISettingsService settings) : IDashboardService
{
    /// <summary>
    /// Counts are SQL aggregates and only the (at most 50) alerting endpoints are loaded and mapped, so the cost does not grow with the whole fleet.
    /// The alert definitions are the ones in <see cref="Mapping.AlertsFor"/> plus the shared drive-health alert (worst drive Warning or Critical on an online PC).
    /// </summary>
    public async Task<DashboardDto> GetAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        var endpoints = db.Endpoints.AsNoTracking();

        var total = await endpoints.CountAsync(ct);
        var online = await endpoints.CountAsync(e => e.Online, ct);
        var cpu = await endpoints.CountAsync(e => e.Online && e.CpuPercent >= s.CpuAlertPercent, ct);
        var ram = await endpoints.CountAsync(e => e.Online && e.RamPercent >= s.RamAlertPercent, ct);
        var disk = await endpoints.CountAsync(e => e.Online && e.DiskPercent >= s.DiskAlertPercent, ct);
        var diskHealth = await endpoints.CountAsync(e => e.Online && db.DiskHealth.Any(d => d.EndpointId == e.Id && (d.Status == DiskStatus.Warning || d.Status == DiskStatus.Critical)), ct);

        var anyAlert = endpoints
            .Where(e => e.Online && (e.CpuPercent >= s.CpuAlertPercent || e.RamPercent >= s.RamAlertPercent || e.DiskPercent >= s.DiskAlertPercent
                                     || db.DiskHealth.Any(d => d.EndpointId == e.Id && (d.Status == DiskStatus.Warning || d.Status == DiskStatus.Critical))));
        var alertTotal = await anyAlert.CountAsync(ct);
        var alertingRows = await anyAlert
            .OrderByDescending(e => (e.CpuPercent >= s.CpuAlertPercent ? 1 : 0) + (e.RamPercent >= s.RamAlertPercent ? 1 : 0) + (e.DiskPercent >= s.DiskAlertPercent ? 1 : 0)
                                    + (db.DiskHealth.Any(d => d.EndpointId == e.Id && (d.Status == DiskStatus.Warning || d.Status == DiskStatus.Critical)) ? 1 : 0))
            .ThenBy(e => e.Hostname)
            .Take(50).ToListAsync(ct);
        var alerting = (await dtoFactory.BuildAsync(alertingRows, ct)).Where(d => d.Alerts.Count > 0).ToList();

        var recent = await db.AuditEntries.AsNoTracking()
            .Where(a => a.Action.StartsWith("job.") || a.Action.StartsWith("action."))
            .OrderByDescending(a => a.Id).Take(10).ToListAsync(ct);

        return new DashboardDto(total, online, total - online, cpu, ram, disk, diskHealth, alerting, recent.Select(r => r.ToDto()).ToList(), alertTotal);
    }
}
