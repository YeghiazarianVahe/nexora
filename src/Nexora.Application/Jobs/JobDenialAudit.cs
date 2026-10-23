using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Application.Jobs;

/// <summary>
/// Lets the risk of a request be raised above the catalog level (for example by what the targets are). The confirmation tier follows the
/// returned level. The default keeps the catalog risk, so no existing action changes.
/// </summary>
public interface IRiskEscalator
{
    Task<RiskLevel> EffectiveRiskAsync(ActionDefinition def, IReadOnlyList<ManagedEndpoint> targets, IReadOnlyDictionary<string, string> parameters, CancellationToken ct);
}

public sealed class CatalogRiskEscalator : IRiskEscalator
{
    public Task<RiskLevel> EffectiveRiskAsync(ActionDefinition def, IReadOnlyList<ManagedEndpoint> targets, IReadOnlyDictionary<string, string> parameters, CancellationToken ct) =>
        Task.FromResult(def.Risk);
}

/// <summary>Said once per operator per window when denials start being dropped (and again, with the final count, when the next window opens).</summary>
public sealed record SuppressionNotice(int Dropped, string Kinds, bool WindowClosed);

/// <summary>One request can close the previous window and open a new one at once; both notices are returned (closing first).</summary>
public sealed record DenialPermit(bool Write, IReadOnlyList<SuppressionNotice> Notices);

/// <summary>
/// Per-operator cap on denial audit rows, so a refused-request loop cannot flood the audit log. Forbidden denials (the security-relevant ones)
/// have their own, much higher cap, so validation noise can never hide them. Singleton; state is instance-held.
/// </summary>
public interface IDenialRateLimiter
{
    DenialPermit TryAcquire(string sid, ErrorKind kind, DateTime nowUtc);
    long Dropped { get; }
}

public sealed class DenialRateLimiter(ILogger<DenialRateLimiter> logger) : IDenialRateLimiter
{
    public const int MaxValidationPerMinute = 20;
    public const int MaxForbiddenPerMinute = 200;
    private const int MaxTrackedOperators = 2_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private sealed class State
    {
        public readonly Queue<DateTime> Validation = new(), Forbidden = new();
        public readonly Dictionary<ErrorKind, int> Pending = [];
        public DateTime? SuppressedSince;
    }

    private readonly Dictionary<string, State> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private long _dropped;

    public long Dropped => Interlocked.Read(ref _dropped);

    private static string Describe(Dictionary<ErrorKind, int> pending) =>
        string.Join(", ", pending.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

    public DenialPermit TryAcquire(string sid, ErrorKind kind, DateTime nowUtc)
    {
        var notices = new List<SuppressionNotice>(2);
        bool write;
        lock (_gate)
        {
            if (_state.Count > MaxTrackedOperators)
                foreach (var k in _state.Where(kv => kv.Value.Pending.Count == 0 && kv.Value.Validation.Count == 0 && kv.Value.Forbidden.Count == 0
                             || (kv.Value.Pending.Count == 0 && Last(kv.Value) <= nowUtc - Window)).Select(kv => kv.Key).ToList()) _state.Remove(k);

            if (!_state.TryGetValue(sid, out var st)) _state[sid] = st = new State();
            Trim(st.Validation, nowUtc); Trim(st.Forbidden, nowUtc);

            // The window that dropped rows has ended: report its final count with the next row.
            if (st.SuppressedSince is { } since && nowUtc - since >= Window)
            {
                notices.Add(new SuppressionNotice(st.Pending.Values.Sum(), Describe(st.Pending), WindowClosed: true));
                st.Pending.Clear(); st.SuppressedSince = null;
            }

            var queue = kind == ErrorKind.Forbidden ? st.Forbidden : st.Validation;
            var cap = kind == ErrorKind.Forbidden ? MaxForbiddenPerMinute : MaxValidationPerMinute;
            if (queue.Count < cap) { queue.Enqueue(nowUtc); write = true; }
            else
            {
                write = false;
                st.Pending[kind] = st.Pending.GetValueOrDefault(kind) + 1;
                if (st.SuppressedSince is null)
                {
                    st.SuppressedSince = nowUtc;
                    notices.Add(new SuppressionNotice(1, Describe(st.Pending), WindowClosed: false));
                }
            }
        }
        if (!write)
        {
            var total = Interlocked.Increment(ref _dropped);
            if (notices.Any(n => !n.WindowClosed))
                logger.LogWarning("Denial audit cap reached for {Sid} ({Kind}); further denials this minute are dropped (total dropped {Total})", sid, kind, total);
        }
        return new DenialPermit(write, notices);
    }

    private static void Trim(Queue<DateTime> q, DateTime now) { while (q.Count > 0 && q.Peek() <= now - Window) q.Dequeue(); }

    private static DateTime Last(State s) =>
        new[] { s.Validation.Count > 0 ? s.Validation.Last() : DateTime.MinValue, s.Forbidden.Count > 0 ? s.Forbidden.Last() : DateTime.MinValue }.Max();
}

/// <summary>Writes the "job.denied" audit entry. Never throws into the request path.</summary>
public interface IJobDenialAuditor
{
    Task RecordAsync(Actor actor, string stage, string? actionCode, IEnumerable<Guid>? endpointIds,
        Dictionary<string, JsonElement>? parameters, ErrorKind kind, string? reason);
}

public sealed partial class JobDenialAuditor(
    INexoraDb db, IAuditService audit, IDenialRateLimiter limiter, ISystemClock clock, ILogger<JobDenialAuditor> logger) : IJobDenialAuditor
{
    public const string AuditAction = "job.denied";
    public const string SuppressedAction = "job.denied.suppressed";
    private const int MaxParams = 20, MaxValueLength = 200, MaxNameLength = 64, MaxTotalLength = 2_000, MaxTargets = 20, MaxReasonLength = 300, MaxActionLength = 100;

    [GeneratedRegex(@"(?i)pass|pwd|secret|token|key|credential")]
    private static partial Regex SecretName();

    public async Task RecordAsync(Actor actor, string stage, string? actionCode, IEnumerable<Guid>? endpointIds,
        Dictionary<string, JsonElement>? parameters, ErrorKind kind, string? reason)
    {
        try
        {
            var permit = limiter.TryAcquire(actor.Sid, kind, clock.UtcNow);
            foreach (var n in permit.Notices)
                audit.Add(actor, SuppressedAction, "Job", null, null, null,
                    new { dropped = n.Dropped, kinds = n.Kinds, windowClosed = n.WindowClosed, capValidationPerMinute = DenialRateLimiter.MaxValidationPerMinute, capForbiddenPerMinute = DenialRateLimiter.MaxForbiddenPerMinute }, success: false);
            if (!permit.Write)
            {
                if (permit.Notices.Count > 0) await SaveAsync();
                return;
            }

            var ids = (endpointIds ?? []).Distinct().ToList();
            var (capped, truncated) = CapParameters(parameters);
            var code = Cap(actionCode, MaxActionLength);
            audit.Add(actor, AuditAction, "Endpoint", ids.Count > 0 ? ids[0].ToString() : null, null, code,
                new
                {
                    stage, kind = kind.ToString(), reason = Cap(reason, MaxReasonLength), action = code,
                    targetIds = ids.Take(MaxTargets), targetCount = ids.Count, parameters = capped, parametersTruncated = truncated,
                }, success: false);

            await SaveAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not write the {Action} audit entry for {Sid}", AuditAction, actor.Sid);
            foreach (var e in db.ChangeTracker.Entries<AuditEntry>().Where(e => e.State == EntityState.Added && e.Entity.Action.StartsWith(AuditAction, StringComparison.Ordinal)).ToList())
                e.State = EntityState.Detached; // a failed denial row must not poison the next save of this request
        }
    }

    /// <summary>
    /// Own time budget: a client that disconnected must not lose the denial record, and a stuck database must not hold the request for long.
    /// Uses the request's scoped context (no IDbContextFactory is registered); nothing else is staged on the denial paths, and a failed
    /// save detaches its own rows.
    /// </summary>
    private async Task SaveAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await db.SaveChangesAsync(cts.Token);
    }

    private static string? Cap(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max] + "...";

    private static (Dictionary<string, string> Values, bool Truncated) CapParameters(Dictionary<string, JsonElement>? parameters)
    {
        var result = new Dictionary<string, string>();
        if (parameters is null) return (result, false);
        var truncated = parameters.Count > MaxParams;
        var total = 0;
        foreach (var (name, value) in parameters.Take(MaxParams))
        {
            var key = Cap(name, MaxNameLength)!;
            string text;
            try
            {
                text = SecretName().IsMatch(name) ? "[redacted]"
                    : Cap(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(), MaxValueLength) ?? "";
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { text = "[unreadable]"; }
            total += key.Length + text.Length;
            if (total > MaxTotalLength) { truncated = true; break; }
            result[key] = text;
        }
        return (result, truncated);
    }
}
