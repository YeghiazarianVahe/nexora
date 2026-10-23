using Microsoft.EntityFrameworkCore;
using Nexora.Domain;

namespace Nexora.Application.Services;

// ---------- Groups ----------

public interface IGroupService
{
    Task<IReadOnlyList<GroupDto>> ListAsync(CancellationToken ct = default);
    Task<Result<GroupDto>> CreateAsync(Actor actor, GroupInput input, CancellationToken ct = default);
    Task<Result<GroupDto>> UpdateAsync(Actor actor, Guid id, GroupInput input, CancellationToken ct = default);
    Task<Result> DeleteAsync(Actor actor, Guid id, CancellationToken ct = default);
    Task<Result<GroupDto>> SetMembersAsync(Actor actor, Guid id, IReadOnlyList<Guid> endpointIds, CancellationToken ct = default);
    /// <summary>Adds and removes the listed workstations only. Members nobody mentioned (for example added by another operator since the editor opened) are untouched.</summary>
    Task<Result<GroupDto>> ChangeMembersAsync(Actor actor, Guid id, IReadOnlyList<Guid> add, IReadOnlyList<Guid> remove, CancellationToken ct = default);
}

public sealed class GroupService(INexoraDb db, IAuditService audit, ISystemClock clock) : IGroupService
{
    public async Task<IReadOnlyList<GroupDto>> ListAsync(CancellationToken ct = default)
    {
        var groups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct);
        var members = await db.GroupMembers.AsNoTracking().ToListAsync(ct);
        var online = (await db.Endpoints.AsNoTracking().Where(e => e.Online).Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        return groups.Select(g => ToDto(g, members.Where(m => m.GroupId == g.Id).Select(m => m.EndpointId).ToList(), online)).ToList();
    }

    public async Task<Result<GroupDto>> CreateAsync(Actor actor, GroupInput input, CancellationToken ct = default)
    {
        if (Validate(input) is { } err) return Result<GroupDto>.Fail(ErrorKind.Validation, err);
        var name = input.Name.Trim();
        if (await db.Groups.AnyAsync(g => g.Name.ToLower() == name.ToLower(), ct))
            return Result<GroupDto>.Fail(ErrorKind.Conflict, "A group with that name already exists.");

        var g = new EndpointGroup { Id = Guid.NewGuid(), Name = name, Description = input.Description?.Trim() ?? "", CreatedUtc = clock.UtcNow };
        db.Groups.Add(g);
        audit.Add(actor, "group.create", "Group", g.Id.ToString(), null, g.Name);
        try { await db.SaveChangesAsync(ct); }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (DiskHealthService.IsUniqueViolation(ex))
        {   // two operators created the same name at once: the unique index decided
            db.ChangeTracker.Clear();
            return Result<GroupDto>.Fail(ErrorKind.Conflict, "A group with that name already exists.");
        }
        return Result<GroupDto>.Success(ToDto(g, [], []));
    }

    public async Task<Result<GroupDto>> UpdateAsync(Actor actor, Guid id, GroupInput input, CancellationToken ct = default)
    {
        if (Validate(input) is { } err) return Result<GroupDto>.Fail(ErrorKind.Validation, err);
        var g = await db.Groups.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return Result<GroupDto>.Fail(ErrorKind.NotFound, "Group not found.");
        var name = input.Name.Trim();
        if (await db.Groups.AnyAsync(x => x.Name.ToLower() == name.ToLower() && x.Id != id, ct))
            return Result<GroupDto>.Fail(ErrorKind.Conflict, "A group with that name already exists.");

        var old = g.Name;
        g.Name = name;
        g.Description = input.Description?.Trim() ?? "";
        audit.Add(actor, "group.update", "Group", id.ToString(), old, g.Name);
        try { await db.SaveChangesAsync(ct); }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (DiskHealthService.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<GroupDto>.Fail(ErrorKind.Conflict, "A group with that name already exists.");
        }
        return Result<GroupDto>.Success(await LoadAsync(g, ct));
    }

    public async Task<Result> DeleteAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        var g = await db.Groups.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return Result.Fail(ErrorKind.NotFound, "Group not found.");
        db.GroupMembers.RemoveRange(db.GroupMembers.Where(m => m.GroupId == id));
        db.Groups.Remove(g);
        audit.Add(actor, "group.delete", "Group", id.ToString(), g.Name);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<GroupDto>> SetMembersAsync(Actor actor, Guid id, IReadOnlyList<Guid> endpointIds, CancellationToken ct = default)
    {
        var g = await db.Groups.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return Result<GroupDto>.Fail(ErrorKind.NotFound, "Group not found.");

        var wanted = endpointIds.Distinct().ToList();
        if (wanted.Count > 2000) return Result<GroupDto>.Fail(ErrorKind.Validation, "Too many members.");
        var known = (await db.Endpoints.Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        if (wanted.Any(w => !known.Contains(w))) return Result<GroupDto>.Fail(ErrorKind.Validation, "Unknown workstation in the list.");

        var current = await db.GroupMembers.Where(m => m.GroupId == id).ToListAsync(ct);
        var currentIds = current.Select(m => m.EndpointId).ToHashSet();
        db.GroupMembers.RemoveRange(current.Where(m => !wanted.Contains(m.EndpointId)));
        foreach (var e in wanted.Where(w => !currentIds.Contains(w)))
            db.GroupMembers.Add(new EndpointGroupMember { GroupId = id, EndpointId = e });

        audit.Add(actor, "group.members", "Group", id.ToString(), currentIds.Count.ToString(), wanted.Count.ToString(), new { g.Name });
        await db.SaveChangesAsync(ct);
        return Result<GroupDto>.Success(await LoadAsync(g, ct));
    }

    public async Task<Result<GroupDto>> ChangeMembersAsync(Actor actor, Guid id, IReadOnlyList<Guid> add, IReadOnlyList<Guid> remove, CancellationToken ct = default)
    {
        var toAdd = add.Distinct().ToList();
        var toRemove = remove.Distinct().ToList();
        if (toAdd.Count == 0 && toRemove.Count == 0) return Result<GroupDto>.Fail(ErrorKind.Validation, "Nothing to change.");
        if (toAdd.Intersect(toRemove).Any()) return Result<GroupDto>.Fail(ErrorKind.Validation, "A workstation cannot be both added and removed.");
        if (toAdd.Count + toRemove.Count > 5000) return Result<GroupDto>.Fail(ErrorKind.Validation, "Too many changes in one step.");

        for (var attempt = 0; ; attempt++)
        {
            var g = await db.Groups.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (g is null) return Result<GroupDto>.Fail(ErrorKind.NotFound, "Group not found.");
            var known = (await db.Endpoints.Where(e => toAdd.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct)).ToHashSet();
            if (toAdd.Any(a => !known.Contains(a))) return Result<GroupDto>.Fail(ErrorKind.Validation, "A workstation in the list no longer exists. Reload the list and try again.");

            var current = await db.GroupMembers.Where(m => m.GroupId == id).ToListAsync(ct);
            var currentIds = current.Select(m => m.EndpointId).ToHashSet();
            db.GroupMembers.RemoveRange(current.Where(m => toRemove.Contains(m.EndpointId)));
            foreach (var e in toAdd.Where(a => !currentIds.Contains(a)))
                db.GroupMembers.Add(new EndpointGroupMember { GroupId = id, EndpointId = e });
            audit.Add(actor, "group.members", "Group", id.ToString(), currentIds.Count.ToString(),
                currentIds.Union(toAdd).Except(toRemove).Count().ToString(), new { g.Name, added = toAdd.Count, removed = toRemove.Count });
            try { await db.SaveChangesAsync(ct); }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (attempt == 0 && DiskHealthService.IsUniqueViolation(ex))
            {
                // another operator added the same member at the same moment: forget our half-built state and recompute once
                db.ChangeTracker.Clear();
                continue;
            }
            return Result<GroupDto>.Success(await LoadAsync(g, ct));
        }
    }

    private async Task<GroupDto> LoadAsync(EndpointGroup g, CancellationToken ct)
    {
        var ids = await db.GroupMembers.AsNoTracking().Where(m => m.GroupId == g.Id).Select(m => m.EndpointId).ToListAsync(ct);
        var online = (await db.Endpoints.AsNoTracking().Where(e => e.Online).Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        return ToDto(g, ids, online);
    }

    private static GroupDto ToDto(EndpointGroup g, IReadOnlyList<Guid> memberIds, HashSet<Guid> online) =>
        new(g.Id, g.Name, g.Description, memberIds, memberIds.Count(online.Contains));

    private static string? Validate(GroupInput i)
    {
        if (string.IsNullOrWhiteSpace(i.Name) || i.Name.Trim().Length > 60) return "Group name is required (max 60 characters).";
        if (i.Description is { Length: > 300 }) return "Description is too long (max 300).";
        return null;
    }
}

// ---------- Metric history (CPU / RAM charts) ----------

public interface IMetricsService
{
    Task<IReadOnlyList<MetricPoint>> EndpointHistoryAsync(Guid endpointId, string? range, CancellationToken ct = default);
    Task<IReadOnlyList<FleetPoint>> FleetHistoryAsync(string? range, CancellationToken ct = default);
}

public sealed class MetricsService(INexoraDb db, ISystemClock clock) : IMetricsService
{
    public async Task<IReadOnlyList<MetricPoint>> EndpointHistoryAsync(Guid endpointId, string? range, CancellationToken ct = default)
    {
        var to = clock.UtcNow;
        var from = to - HistoryRange.Parse(range);
        var rows = await db.MetricSamples.AsNoTracking()
            .Where(m => m.EndpointId == endpointId && m.TimestampUtc >= from && m.TimestampUtc <= to)
            .OrderBy(m => m.TimestampUtc).ToListAsync(ct);
        if (rows.Count == 0) return [];

        // Each series is bucketed with identical boundaries, so the three lists line up index by index.
        var cpu = HistoryRange.Bucket(rows.Select(r => (r.TimestampUtc, r.Cpu)), from, to);
        var ram = HistoryRange.Bucket(rows.Select(r => (r.TimestampUtc, r.Ram)), from, to);
        var disk = HistoryRange.Bucket(rows.Select(r => (r.TimestampUtc, r.Disk)), from, to);
        var latest = rows[^1];
        return cpu.Select((c, i) => i == cpu.Count - 1
            ? new MetricPoint(latest.TimestampUtc, latest.Cpu, latest.Ram, latest.Disk)
            : new MetricPoint(c.Time, c.Value, ram[i].Value, disk[i].Value)).ToList();
    }

    public async Task<IReadOnlyList<FleetPoint>> FleetHistoryAsync(string? range, CancellationToken ct = default)
    {
        var to = clock.UtcNow;
        var from = to - HistoryRange.Parse(range);
        var rows = await db.FleetSamples.AsNoTracking().Where(f => f.TimestampUtc >= from && f.TimestampUtc <= to).OrderBy(f => f.TimestampUtc).ToListAsync(ct);
        if (rows.Count == 0) return [];

        var cpu = HistoryRange.Bucket(rows.Select(r => (r.TimestampUtc, r.AvgCpu)), from, to);
        var ram = HistoryRange.Bucket(rows.Select(r => (r.TimestampUtc, r.AvgRam)), from, to);
        var online = HistoryRange.Bucket(rows.Select(r => (r.TimestampUtc, (double?)r.Online)), from, to);
        var latest = rows[^1];
        return cpu.Select((c, i) => i == cpu.Count - 1
            ? new FleetPoint(latest.TimestampUtc, latest.AvgCpu, latest.AvgRam, latest.Online)
            : new FleetPoint(c.Time, c.Value, ram[i].Value, (int)Math.Round(online[i].Value ?? 0))).ToList();
    }
}
