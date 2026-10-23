using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Jobs;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Application.Diagnostics;

// ---------------- DTOs (camelCase on the wire) ----------------

public sealed record DiagProfileInfo(string Id, string Name, string Description, int Collectors);

/// <param name="Status">Running, Complete, Incomplete or Failed.</param>
public sealed record DiagRunDto(
    Guid Id, Guid EndpointId, string Hostname, string ProfileId, string ProfileName, string Status, bool Complete,
    DateTime StartedUtc, DateTime? FinishedUtc, Guid JobId, int SchemaVersion,
    IReadOnlyList<Finding> Findings, IReadOnlyList<CollectorStatus> Collectors, IReadOnlyList<string> NotEvaluated);

public sealed record DiagRunListItem(
    Guid Id, Guid EndpointId, string ProfileId, string ProfileName, string Status, bool Complete,
    DateTime StartedUtc, DateTime? FinishedUtc, Guid JobId, int FindingCount, string? TopSeverity);

public sealed record DiagStarted(Guid RunId, Guid JobId);

// ---------------- ports ----------------

/// <summary>Starts a diagnosis as a normal diag.collect job. Implemented by the job service so every job rule (denial audit, protected hosts, queueing) applies.</summary>
public interface IDiagnosticJobStarter
{
    Task<Result<DiagStarted>> StartDiagnosisAsync(JobActor actor, Guid endpointId, string? profileId, CancellationToken ct = default);
}

/// <summary>Called when a diag.collect target reaches a final state: parses the output, evaluates the rules, stores the run.</summary>
public interface IDiagnosisCompletion
{
    Task OnTargetFinishedAsync(Job job, JobTarget target, TargetState state, CancellationToken ct = default);
}

/// <summary>Real-time push once a run is stored. Sent to the job's group, which the initiating operator joins through SubscribeJob.</summary>
public interface IDiagnosisEvents
{
    Task DiagnosisCompletedAsync(Guid jobId, Guid runId);
}

public interface IDiagnosisService
{
    IReadOnlyList<DiagProfileInfo> Profiles();
    Task<Result<DiagRunDto>> GetRunAsync(Guid runId, CancellationToken ct = default);
    /// <summary>The raw collector outputs of a run: <c>{ "collector-id": { ... } }</c>.</summary>
    Task<Result<IReadOnlyDictionary<string, JsonElement>>> EvidenceAsync(Actor actor, Guid runId, CancellationToken ct = default);
    Task<Result<IReadOnlyList<DiagRunListItem>>> HistoryAsync(Guid endpointId, CancellationToken ct = default);
}

public static class DiagnosisJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
}

public static class DiagRunStatus
{
    public const string Running = "Running", Complete = "Complete", Incomplete = "Incomplete", Failed = "Failed";
}

// ---------------- reads ----------------

public sealed class DiagnosisService(INexoraDb db, IDiagnosticProfiles profiles, IAuditService audit, ILogger<DiagnosisService> logger) : IDiagnosisService
{
    public const int HistoryLimit = 50;

    public IReadOnlyList<DiagProfileInfo> Profiles() =>
        profiles.All.Select(p => new DiagProfileInfo(p.Id, p.Name, p.Description, p.Collectors.Count)).ToList();

    public async Task<Result<DiagRunDto>> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await db.DiagnosisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return Result<DiagRunDto>.Fail(ErrorKind.NotFound, "Diagnosis not found.");
        var host = await db.Endpoints.AsNoTracking().Where(e => e.Id == run.EndpointId).Select(e => e.Hostname).FirstOrDefaultAsync(ct) ?? "";
        var result = ReadResult(run);
        return Result<DiagRunDto>.Success(new DiagRunDto(
            run.Id, run.EndpointId, host, run.ProfileId, profiles.Find(run.ProfileId)?.Name ?? run.ProfileId, run.Status, run.Complete,
            run.StartedUtc, run.FinishedUtc, run.JobId, run.SchemaVersion,
            result?.Findings ?? [], result?.Collectors ?? [], result?.NotEvaluated ?? []));
    }

    public async Task<Result<IReadOnlyDictionary<string, JsonElement>>> EvidenceAsync(Actor actor, Guid runId, CancellationToken ct = default)
    {
        var run = await db.DiagnosisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return Result<IReadOnlyDictionary<string, JsonElement>>.Fail(ErrorKind.NotFound, "Diagnosis not found.");
        // Raw evidence holds file paths and add-in names: every read is on record.
        audit.Add(actor, "diag.evidence.read", "DiagnosisRun", runId.ToString(), null, null, new { runId, endpointId = run.EndpointId, profile = run.ProfileId });
        await db.SaveChangesAsync(ct);
        if (string.IsNullOrEmpty(run.EvidenceJson)) return Result<IReadOnlyDictionary<string, JsonElement>>.Success(new Dictionary<string, JsonElement>());
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(run.EvidenceJson, DiagnosisJson.Options) ?? [];
            return Result<IReadOnlyDictionary<string, JsonElement>>.Success(map);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Evidence of diagnosis {RunId} is not readable", runId);
            return Result<IReadOnlyDictionary<string, JsonElement>>.Success(new Dictionary<string, JsonElement>());
        }
    }

    public async Task<Result<IReadOnlyList<DiagRunListItem>>> HistoryAsync(Guid endpointId, CancellationToken ct = default)
    {
        if (!await db.Endpoints.AsNoTracking().AnyAsync(e => e.Id == endpointId, ct))
            return Result<IReadOnlyList<DiagRunListItem>>.Fail(ErrorKind.NotFound, "Workstation not found.");
        var runs = await db.DiagnosisRuns.AsNoTracking().Where(r => r.EndpointId == endpointId)
            .OrderByDescending(r => r.StartedUtc).Take(HistoryLimit).ToListAsync(ct);
        var items = runs.Select(r =>
        {
            var result = ReadResult(r);
            var top = result?.Findings.OrderByDescending(f => Severities.Weight(f.Severity)).FirstOrDefault()?.Severity;
            return new DiagRunListItem(r.Id, r.EndpointId, r.ProfileId, profiles.Find(r.ProfileId)?.Name ?? r.ProfileId, r.Status, r.Complete,
                r.StartedUtc, r.FinishedUtc, r.JobId, result?.Findings.Count ?? 0, top);
        }).ToList();
        return Result<IReadOnlyList<DiagRunListItem>>.Success(items);
    }

    private DiagnosisResult? ReadResult(DiagnosisRun run)
    {
        if (string.IsNullOrEmpty(run.ResultJson)) return null;
        try { return JsonSerializer.Deserialize<DiagnosisResult>(run.ResultJson, DiagnosisJson.Options); }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Result of diagnosis {RunId} is not readable", run.Id);
            return null;
        }
    }
}

// ---------------- completion ----------------

public sealed class DiagnosisCompletion(
    INexoraDb db, IDiagnosticProfiles profiles, IRuleEngine engine, IAuditService audit, IDiagnosisEvents events,
    ISystemClock clock, ILogger<DiagnosisCompletion> logger) : IDiagnosisCompletion
{
    /// <summary>The raw outputs kept with a run; anything beyond is left out (the cap protects the database, not the result).</summary>
    public const int MaxEvidenceBytes = 200 * 1024;
    private const int MaxLines = 5_000;

    public async Task OnTargetFinishedAsync(Job job, JobTarget target, TargetState state, CancellationToken ct = default)
    {
        var run = await db.DiagnosisRuns.FirstOrDefaultAsync(r => r.JobId == job.Id, ct);
        if (run is null || run.Status != DiagRunStatus.Running) return;

        try { await FinishAsync(run, job, target, state, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Diagnosis {RunId} of job {JobId} could not be evaluated", run.Id, job.Id);
            foreach (var e in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added && e.Entity is AuditEntry a && a.Action == "diag.complete").ToList()) e.State = EntityState.Detached;
            await StoreFailureAsync(run, job, target, ct);
        }
    }

    private async Task FinishAsync(DiagnosisRun run, Job job, JobTarget target, TargetState state, CancellationToken ct)
    {
        var profile = profiles.Find(run.ProfileId);
        DiagnosisResult result;
        string? evidence = null;
        string status;
        var ignored = 0;

        if (profile is null)
        {
            result = new DiagnosisResult(DiagnosisResult.Schema.Version, run.ProfileId, run.ProfileVersion, false, [], [], []);
            status = DiagRunStatus.Failed;
        }
        else if (state is TargetState.Succeeded or TargetState.Warning)
        {
            var lines = await db.JobLogChunks.AsNoTracking().Where(c => c.JobTargetId == target.Id && c.Stream == "stdout")
                .OrderBy(c => c.Seq).Select(c => c.Text).Take(MaxLines).ToListAsync(ct);
            var parsed = NxcParser.Parse(lines, profile.Collectors.Select(c => c.Id).ToList());
            ignored = parsed.Ignored;
            var results = parsed.Results.ToList();
            results.Add(await DiskHealthSource.BuildAsync(db, run.EndpointId, ct, clock.UtcNow));
            result = engine.Evaluate(profile, results);
            evidence = BuildEvidence(parsed.Results);
            status = parsed.Results.Any(r => r.Status == CollectorStates.Ok) ? (result.Complete ? DiagRunStatus.Complete : DiagRunStatus.Incomplete) : DiagRunStatus.Failed;
        }
        else
        {
            var reason = DiagText.Clean(target.SkipReason ?? target.ResultSummary ?? state.ToString(), 120);
            result = engine.Evaluate(profile, profile.Collectors.Select(c => CollectorResult.Failed(c.Id, reason)).ToList());
            status = DiagRunStatus.Failed;
        }

        run.Status = status;
        run.Complete = status == DiagRunStatus.Complete;
        run.FinishedUtc = clock.UtcNow;
        run.ResultJson = JsonSerializer.Serialize(result, DiagnosisJson.Options);
        run.EvidenceJson = evidence;
        run.SchemaVersion = result.SchemaVersion;

        audit.Add(new Actor(run.InitiatedBySid, job.InitiatorAccount, job.ClientIp), "diag.complete", "Endpoint", target.Host, null, status,
            new
            {
                runId = run.Id, jobId = job.Id, profile = run.ProfileId, profileVersion = run.ProfileVersion, status, complete = run.Complete, targetState = state.ToString(),
                findings = result.Findings.Select(f => new { f.Id, f.Severity, f.Confidence }),
                collectors = result.Collectors.Select(c => new { c.Id, c.Status, c.Reason }),
                notEvaluated = result.NotEvaluated, ignoredLines = ignored,
            }, success: status != DiagRunStatus.Failed);
        await db.SaveChangesAsync(ct);
        await PushAsync(job.Id, run.Id);
    }

    /// <summary>Last resort: the run is marked failed so it is never left "Running" forever, with the failure audited.</summary>
    private async Task StoreFailureAsync(DiagnosisRun run, Job job, JobTarget target, CancellationToken ct)
    {
        try
        {
            // Whatever made the first save fail must not be saved again: forget every other pending change, and keep the evidence out.
            foreach (var e in db.ChangeTracker.Entries().Where(e => !ReferenceEquals(e.Entity, run) && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
                e.State = EntityState.Detached;
            run.Status = DiagRunStatus.Failed;
            run.Complete = false;
            run.EvidenceJson = null;
            run.FinishedUtc = clock.UtcNow;
            run.ResultJson = JsonSerializer.Serialize(new DiagnosisResult(DiagnosisResult.Schema.Version, run.ProfileId, run.ProfileVersion, false, [], [], []), DiagnosisJson.Options);
            audit.Add(new Actor(run.InitiatedBySid, job.InitiatorAccount, job.ClientIp), "diag.complete", "Endpoint", target.Host, null, DiagRunStatus.Failed,
                new { runId = run.Id, jobId = job.Id, profile = run.ProfileId, status = DiagRunStatus.Failed, reason = "evaluation error" }, success: false);
            await db.SaveChangesAsync(ct);
            await PushAsync(job.Id, run.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Diagnosis {RunId} could not be marked failed", run.Id);
        }
    }

    private async Task PushAsync(Guid jobId, Guid runId)
    {
        try { await events.DiagnosisCompletedAsync(jobId, runId); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "DiagnosisCompleted push failed"); }
    }

    /// <summary>The raw JSON of the collectors that delivered data, as one object, within <see cref="MaxEvidenceBytes"/>.</summary>
    public static string? BuildEvidence(IReadOnlyList<CollectorResult> results)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var size = 2;
        foreach (var r in results.Where(r => r.Data is not null).OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            var clean = JsonSanitizer.Clean(r.Data!.Value); // jsonb refuses U+0000 and unpaired surrogates
            var raw = clean.GetRawText();
            if (size + raw.Length + r.Id.Length + 6 > MaxEvidenceBytes) continue;
            size += raw.Length + r.Id.Length + 6;
            map[r.Id] = clean;
        }
        return map.Count == 0 ? null : JsonSerializer.Serialize(map, DiagnosisJson.Options);
    }
}

/// <summary>
/// The server's own last drive-health picture of a PC (F4), offered to the rules as the source <c>server:diskHealth</c>. It never comes from the
/// collector output, so a PC cannot influence it.
/// </summary>
public static class DiskHealthSource
{
    public static async Task<CollectorResult> BuildAsync(INexoraDb db, Guid endpointId, CancellationToken ct, DateTime? nowUtc = null)
    {
        var rows = await db.DiskHealth.AsNoTracking().Where(d => d.EndpointId == endpointId).ToListAsync(ct);
        if (rows.Count == 0) return CollectorResult.Missing(DiagPath.DiskHealthSource);
        var now = nowUtc ?? DateTime.UtcNow;
        if (rows.Any(r => now - r.UpdatedUtc > TimeSpan.FromMinutes(30) || r.UpdatedUtc > now.AddMinutes(2))
            || rows.All(r => r.Status == DiskStatus.Unknown))
            return CollectorResult.Incomplete(DiagPath.DiskHealthSource, "Drive health is stale or unavailable. Refresh Storage before drawing conclusions.");
        var worst = rows.Select(r => r.Status).OrderByDescending(Rank).First();
        var doc = new
        {
            worstStatus = worst,
            count = rows.Count,
            drives = rows.Select(r => new
            {
                serial = r.Serial, model = r.Model, mediaType = r.MediaType, status = r.Status, healthPercent = r.HealthPercent,
                temperatureC = r.TemperatureC, predictFailure = r.PredictFailure, summary = r.Summary, updatedUtc = r.UpdatedUtc,
            }),
        };
        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(doc, DiagnosisJson.Options));
        return CollectorResult.Ok(DiagPath.DiskHealthSource, parsed.RootElement.Clone());
    }

    private static int Rank(string status) => status switch { "Critical" => 3, "Warning" => 2, "OK" => 1, _ => 0 };
}
