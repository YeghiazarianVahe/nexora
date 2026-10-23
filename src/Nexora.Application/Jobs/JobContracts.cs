using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexora.Domain;

namespace Nexora.Application.Jobs;

// ---------------- requests ----------------

public sealed record CreateJobRequest(
    string ActionCode, List<Guid>? EndpointIds, Dictionary<string, JsonElement>? Parameters,
    string? Reason, string? ConfirmToken, bool OverrideProtected = false);

public sealed record ConfirmRequest(
    string ActionCode, List<Guid>? EndpointIds, Dictionary<string, JsonElement>? Parameters,
    string? Reason, string? Typed, bool OverrideProtected = false);

public sealed record ConfirmResponse(string Token, DateTime ExpiresUtc);

/// <param name="Parameters">Optional. When given they are validated like a real request, so the effective risk (dependent services of a stop or restart) can be shown before confirming.</param>
public sealed record PreflightRequest(string ActionCode, List<Guid>? EndpointIds, Dictionary<string, JsonElement>? Parameters = null);

/// <summary>What a bulk run would meet, shown before the Run button is enabled. Counts are of inventory state; the server re-checks on submit.</summary>
public sealed record PreflightResult(
    int Total, int Reachable, IReadOnlyList<string> Offline, IReadOnlyList<PreflightProtected> Protected, IReadOnlyList<string> Busy, bool OverrideNeeded,
    /// <summary>The risk the confirmation will follow (Low, Medium, High, Critical): the catalog level, raised by what the request would disturb. Null only from older servers.</summary>
    string? EffectiveRisk = null);

public sealed record PreflightProtected(string Name, string Reason);

public sealed record JobListQuery(
    string? Status = null, string? Action = null, string? Operator = null,
    DateTime? FromUtc = null, DateTime? ToUtc = null, int Page = 1, int PageSize = 50, Guid? EndpointId = null);

// ---------------- responses ----------------

public sealed record JobSummary(
    Guid Id, string ActionCode, string ActionName, string InitiatorSid, string InitiatorAccount, string Status,
    DateTime CreatedUtc, DateTime? FinishedUtc, string? Reason, int TargetCount,
    int Queued, int Running, int Succeeded, int Warning, int Failed, int Cancelled, int TimedOut, int Skipped,
    bool ProtectedOverride, string Risk);

public sealed record JobTargetInfo(
    Guid Id, Guid JobId, Guid EndpointId, string EndpointName, string Host, string Status,
    DateTime? StartedUtc, DateTime? FinishedUtc, double? DurationSeconds, int? ExitCode, string? ResultSummary,
    string? SkipReason, int? Progress, int LogLines, bool HasOutputFile, string? OutputSha256);

public sealed record JobDetail(JobSummary Job, string ParametersJson, IReadOnlyList<JobTargetInfo> Targets);

public sealed record LogLine(int Seq, DateTime TimestampUtc, string Stream, string Text);

public sealed record ParameterInfo(
    string Name, string Type, string Label, bool Required, int? Min, int? Max, int MaxLength,
    IReadOnlyList<string>? Allowed, string? Default, string? Help);

public sealed record CatalogEntry(
    string Code, string DisplayName, string Category, string Risk, string MinRole, string Method,
    IReadOnlyList<ParameterInfo> Parameters, int TimeoutSeconds, string Description, string? Command,
    bool SupportsCancel, bool SupportsBulk, bool RequiresUserNotice,
    bool NeedsConfirmation, bool NeedsReason, bool NeedsTypedConfirmation, bool Heavy, bool IsPower, bool Implemented,
    /// <summary>Protected workstations (the Nexora server, server OS, protected list) need an Admin override with a reason.</summary>
    bool ChecksProtected,
    /// <summary>Whether the caller's role may run it. The server enforces this again on every request.</summary>
    bool Allowed);

/// <summary>The authenticated caller of a job operation.</summary>
public sealed record JobActor(Actor Actor, Role? Role, bool IsAdmin)
{
    public string Sid => Actor.Sid;
}

// ---------------- ports ----------------

/// <summary>Per-target lifecycle events from whoever executes the job (the operator's helper, or the fake executor).</summary>
public interface IJobReports
{
    /// <param name="reporterSid">When set, must equal the job initiator's SID (a helper may only report its own operator's jobs).</param>
    Task<Result> StartedAsync(Guid targetId, string? reporterSid);
    Task<Result> LogAsync(Guid targetId, string stream, IReadOnlyList<string> lines, string? reporterSid);
    Task<Result> ProgressAsync(Guid targetId, int percent, string? reporterSid);
    Task<Result> CompletedAsync(Guid targetId, TargetState state, int? exitCode, string? summary, string? reporterSid);
}

public sealed record JobStartContext(
    Job Job, JobTarget Target, ActionDefinition Action, IReadOnlyDictionary<string, string> Parameters, TimeSpan Timeout);

/// <param name="Started">The executor accepted the target.</param>
/// <param name="WaitForHelper">The initiator's helper is not connected; try again later.</param>
public sealed record StartOutcome(bool Started, bool WaitForHelper = false, string? Error = null);

public interface IJobExecutor
{
    Task<StartOutcome> StartAsync(JobStartContext context, CancellationToken ct);
    Task CancelAsync(Guid targetId, string initiatorSid, CancellationToken ct);
}

/// <summary>Real-time pushes for the Jobs UI.</summary>
public interface IJobEvents
{
    Task JobChangedAsync(JobSummary job);
    Task TargetChangedAsync(JobTargetInfo target, string initiatorSid);
    Task LogAppendedAsync(Guid jobId, Guid targetId, string initiatorSid, IReadOnlyList<LogLine> lines);
    /// <summary>The job reached a final state: toast for the initiator.</summary>
    Task JobFinishedAsync(JobSummary job);
}

/// <summary>Wakes the orchestrator when there is new work.</summary>
public interface IJobSignal
{
    void Poke();
}

// ---------------- helpers ----------------

public static partial class OutputRedactor
{
    [GeneratedRegex(@"(?i)\b(pass(?:word|wd)?|pwd|secret|token|api[-_]?key|client[-_]?secret)(\s*[=:]\s*)(\S+)")]
    private static partial Regex Pattern();

    /// <summary>Masks the value of obvious secrets ("password=hunter2" becomes "password=***"). Everything else is kept as is.</summary>
    public static string Redact(string line) => Pattern().Replace(line, "$1$2***");
}

public static class ParameterValidator
{
    /// <summary>Validates against the action's schema. Unknown names are rejected; defaults are filled in. Everything comes back as strings.</summary>
    public static (Dictionary<string, string>? Values, string? Error) Validate(ActionDefinition def, Dictionary<string, JsonElement>? input)
    {
        input ??= [];
        var known = def.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = input.Keys.FirstOrDefault(k => !known.Contains(k));
        if (unknown is not null) return (null, $"Unknown parameter '{unknown}'.");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var spec in def.Parameters)
        {
            if (!input.TryGetValue(spec.Name, out var el) || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (spec.Required) return (null, $"'{spec.Label}' is required.");
                if (spec.Default is not null) result[spec.Name] = spec.Default;
                continue;
            }
            var (value, error) = Normalize(spec, el);
            if (error is not null) return (null, error);
            if (value is not null && !(value.Length == 0 && !spec.Required)) result[spec.Name] = value;
            else if (spec.Required) return (null, $"'{spec.Label}' is required.");
        }
        return (result, null);
    }

    private static (string? Value, string? Error) Normalize(ParameterSpec spec, JsonElement el)
    {
        switch (spec.Type)
        {
            case ParamType.Int:
                if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var n))
                    return (null, $"'{spec.Label}' must be a whole number.");
                if ((spec.Min is { } lo && n < lo) || (spec.Max is { } hi && n > hi))
                    return (null, $"'{spec.Label}' must be between {spec.Min} and {spec.Max}.");
                return (n.ToString(System.Globalization.CultureInfo.InvariantCulture), null);

            case ParamType.Bool:
                return el.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? (el.GetBoolean() ? "true" : "false", null)
                    : (null, $"'{spec.Label}' must be true or false.");

            case ParamType.Enum:
                var e = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
                return e is not null && spec.Allowed!.Contains(e, StringComparer.Ordinal)
                    ? (e, null)
                    : (null, $"'{spec.Label}' must be one of: {string.Join(", ", spec.Allowed!)}.");

            case ParamType.StringList:
                var items = new List<string>();
                if (el.ValueKind == JsonValueKind.Array) items.AddRange(el.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "\0"));
                else if (el.ValueKind == JsonValueKind.String) items.AddRange((el.GetString() ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries));
                else return (null, $"'{spec.Label}' must be a list.");
                items = items.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (items.Count > 100) return (null, $"'{spec.Label}' has too many entries (max 100).");
                foreach (var item in items)
                    if (item.Contains('\0') || (spec.Pattern is not null && !Matches(item, spec.Pattern)))
                        return (null, $"'{item}' is not a valid value for '{spec.Label}'.");
                return (string.Join(",", items), null);

            default:
                if (el.ValueKind != JsonValueKind.String) return (null, $"'{spec.Label}' must be text.");
                var s = (el.GetString() ?? "").Trim();
                if (s.Length > spec.MaxLength) return (null, $"'{spec.Label}' is too long (max {spec.MaxLength}).");
                if (s.Any(char.IsControl)) return (null, $"'{spec.Label}' contains control characters.");
                if (s.Length > 0 && spec.Pattern is not null && !Matches(s, spec.Pattern))
                    return (null, $"'{spec.Label}' has an invalid format.");
                if (spec.Deny is not null && spec.Deny.Contains(s, StringComparer.OrdinalIgnoreCase))
                    return (null, $"'{s}' is protected and cannot be targeted by this action.");
                return (s, null);
        }
    }

    /// <summary>A value that makes the pattern run out of time is simply not valid (it used to surface as a server error).</summary>
    private static bool Matches(string value, string pattern)
    {
        try { return Regex.IsMatch(value, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>Stable hash of the normalized parameters (used to bind a confirmation token to exactly what was confirmed).</summary>
    public static string Hash(IReadOnlyDictionary<string, string> values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", values.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")))));
}

/// <summary>
/// Short-lived, single-use proof that the operator went through the confirmation dialog for exactly this action, these
/// targets and these parameters. Held in memory with a per-process key, so a restart invalidates outstanding tokens.
/// </summary>
public interface IConfirmTokens
{
    ConfirmResponse Issue(string sid, string action, string targetsHash, string paramsHash, bool overrideProtected, DateTime nowUtc);
    bool Consume(string? token, string sid, string action, string targetsHash, string paramsHash, bool overrideProtected, DateTime nowUtc);
}

public sealed class ConfirmTokens : IConfirmTokens
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, DateTime> _used = [];
    private readonly Lock _gate = new();

    public ConfirmResponse Issue(string sid, string action, string targetsHash, string paramsHash, bool overrideProtected, DateTime nowUtc)
    {
        var exp = nowUtc + Ttl;
        var body = $"{Guid.NewGuid():N}|{sid.ToUpperInvariant()}|{action}|{targetsHash}|{paramsHash}|{(overrideProtected ? 1 : 0)}|{exp.Ticks}";
        var mac = Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(body)));
        return new ConfirmResponse(Convert.ToBase64String(Encoding.UTF8.GetBytes(body)) + "." + mac, exp);
    }

    public bool Consume(string? token, string sid, string action, string targetsHash, string paramsHash, bool overrideProtected, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 2) return false;
        string body;
        try { body = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])); } catch (FormatException) { return false; }
        var expected = Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(body)));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[1].ToUpperInvariant()))) return false;

        var f = body.Split('|');
        if (f.Length != 7 || !long.TryParse(f[6], out var ticks)) return false;
        if (new DateTime(ticks, DateTimeKind.Utc) < nowUtc) return false;
        if (f[1] != sid.ToUpperInvariant() || f[2] != action || f[3] != targetsHash || f[4] != paramsHash || f[5] != (overrideProtected ? "1" : "0")) return false;

        lock (_gate)
        {
            foreach (var old in _used.Where(kv => kv.Value < nowUtc).Select(kv => kv.Key).ToList()) _used.Remove(old);
            return _used.TryAdd(f[0], new DateTime(ticks, DateTimeKind.Utc)); // single use
        }
    }
}

public static class TargetsHash
{
    public static string Of(IEnumerable<Guid> ids) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", ids.OrderBy(i => i).Select(i => i.ToString("N"))))));
}
