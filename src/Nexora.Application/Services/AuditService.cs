using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Domain;

namespace Nexora.Application.Services;

public interface IAuditService
{
    /// <summary>Stages an audit entry on the current unit of work; the caller's SaveChanges commits it atomically with the change.</summary>
    void Add(Actor actor, string action, string? targetType = null, string? targetId = null,
        string? oldValue = null, string? newValue = null, object? details = null, bool success = true);

    Task<PagedResult<AuditEntryDto>> QueryAsync(AuditQuery query, CancellationToken ct = default);
}

public sealed class AuditService(INexoraDb db, ISystemClock clock) : IAuditService
{
    public void Add(Actor actor, string action, string? targetType = null, string? targetId = null,
        string? oldValue = null, string? newValue = null, object? details = null, bool success = true) =>
        db.AuditEntries.Add(new AuditEntry
        {
            TimestampUtc = clock.UtcNow,
            ActorSid = actor.Sid,
            ActorAccount = actor.Account,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            OldValue = oldValue,
            NewValue = newValue,
            ClientIp = actor.ClientIp,
            Details = details is null ? null : JsonSerializer.Serialize(details),
            Success = success,
        });

    public async Task<PagedResult<AuditEntryDto>> QueryAsync(AuditQuery q, CancellationToken ct = default)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);

        IQueryable<AuditEntry> query = db.AuditEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.ActorSid)) query = query.Where(e => e.ActorSid == q.ActorSid);
        if (!string.IsNullOrWhiteSpace(q.Action)) query = query.Where(e => e.Action.StartsWith(q.Action));
        if (q.FromUtc is { } from) query = query.Where(e => e.TimestampUtc >= from);
        if (q.ToUtc is { } to) query = query.Where(e => e.TimestampUtc <= to);
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var t = q.Text.Trim().ToLower();
            query = query.Where(e => e.ActorAccount.ToLower().Contains(t)
                || (e.TargetId != null && e.TargetId.ToLower().Contains(t))
                || e.Action.ToLower().Contains(t));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(e => e.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<AuditEntryDto>(items.Select(e => e.ToDto()).ToList(), total, page, size);
    }
}
