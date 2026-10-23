using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Diagnostics;
using Nexora.Domain;

namespace Nexora.Application.Jobs;

/// <summary>
/// F5: starting a diagnosis. It is a normal diag.collect job (one target, the initiating operator's own helper, queue TTL, cancel, job audit),
/// created by the server on the operator's behalf with parameters the operator cannot influence: the collector set comes from the profile and
/// the Exchange hosts from the Admin setting. The generic job endpoints keep refusing diag.collect.
/// </summary>
public sealed partial class JobService : IDiagnosticJobStarter
{
    public async Task<Result<DiagStarted>> StartDiagnosisAsync(JobActor actor, Guid endpointId, string? profileId, CancellationToken ct = default)
    {
        const string stage = "diagnose";
        Task<Result<DiagStarted>> Deny(ErrorKind kind, string message) =>
            DeniedAsync(actor, stage, DiagnosticJob.ActionCode, [endpointId], null, Result<DiagStarted>.Fail(kind, message));

        var shownProfile = DiagText.Clean(profileId, 64);
        if (!features.Value.Diagnostics)
            return await Deny(ErrorKind.NotImplemented, $"Diagnose is not enabled on this server (profile '{shownProfile}'). An Admin must turn on Features:Diagnostics.");
        if (actor.Role is not { } role || (int)role < (int)Role.Operator)
            return await Deny(ErrorKind.Forbidden, "Diagnose needs the Operator role.");

        var profile = diagProfiles.Find(profileId);
        if (profile is null) return await Deny(ErrorKind.Validation, "Unknown diagnosis profile.");

        var endpoint = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(e => e.Id == endpointId, ct);
        if (endpoint is null) return await Deny(ErrorKind.Validation, "The workstation is not in the inventory.");

        var s = await settings.GetAsync(ct);
        if (ProtectedReason(endpoint, s) is { } protectedWhy)
            return await Deny(ErrorKind.Validation, $"Diagnose does not run on protected workstations ({protectedWhy}).");

        if (profile.Collectors.Count == 0 || DiagnosticJob.CollectorsJson(profile, diagCollectors) is null)
            return await Deny(ErrorKind.Unavailable, "This server has no collectors for that profile yet.");

        var hosts = s.DiagExchangeHosts.Where(Services.ExchangeHostRules.IsValid).Take(DiagnosticJob.MaxExchangeHosts).ToList();

        var now = clock.UtcNow;
        if (await HasLiveRunAsync(endpointId, now, ct))
            return await Deny(ErrorKind.Conflict, "A diagnosis is already running on this workstation. Wait for it to finish.");

        var runId = Guid.NewGuid();
        var parameters = new Dictionary<string, string>
        {
            ["profileId"] = profile.Id,
            ["runId"] = runId.ToString("D"),
            ["collectorIds"] = string.Join(",", profile.Collectors.Select(c => c.Id)),
            ["exchangeHosts"] = string.Join(",", hosts),
        };
        var job = new Job
        {
            Id = Guid.NewGuid(), ActionCode = DiagnosticJob.ActionCode, ParametersJson = JsonSerializer.Serialize(parameters),
            InitiatorSid = actor.Sid, InitiatorAccount = actor.Actor.Account, CreatedUtc = now, Status = JobState.Queued,
            ClientIp = actor.Actor.ClientIp, TargetCount = 1,
        };
        db.Jobs.Add(job);
        db.JobTargets.Add(new JobTarget { Id = Guid.NewGuid(), JobId = job.Id, EndpointId = endpoint.Id, EndpointName = endpoint.DisplayName ?? endpoint.Hostname, Host = endpoint.Hostname });
        db.DiagnosisRuns.Add(new DiagnosisRun
        {
            Id = runId, EndpointId = endpoint.Id, JobId = job.Id, ProfileId = profile.Id, ProfileVersion = profile.Version,
            InitiatedBySid = actor.Sid, Status = DiagRunStatus.Running, StartedUtc = now, SchemaVersion = DiagnosisResult.Schema.Version,
        });
        audit.Add(actor.Actor, "job.create", "Job", job.Id.ToString(), null, DiagnosticJob.ActionCode,
            new
            {
                action = DiagnosticJob.ActionCode, risk = RiskLevel.Low.ToString(), effectiveRisk = RiskLevel.Low.ToString(),
                targets = new[] { endpoint.DisplayName ?? endpoint.Hostname }, count = 1, parameters, diagnosisRunId = runId, profile = profile.Id,
                collectors = profile.Collectors.Count,
            });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException?.GetType().GetProperty("SqlState")?.GetValue(ex.InnerException) as string == "23505")
        {
            // Two starts raced past the check; the partial unique index (one Running run per workstation) let only one in.
            foreach (var e in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList()) e.State = EntityState.Detached;
            return await Deny(ErrorKind.Conflict, "A diagnosis is already running on this workstation. Wait for it to finish.");
        }

        signal.Poke();
        await events.JobChangedAsync(Summarize(job, [TargetState.Queued]));
        return Result<DiagStarted>.Success(new DiagStarted(runId, job.Id));
    }

    /// <summary>
    /// True when a run on this workstation is genuinely in progress. A "Running" run whose job target is already final (or gone), or that is older than
    /// the job's timeout plus the queue wait, can never finish by itself: it is healed to Failed here so it cannot block the workstation forever.
    /// </summary>
    private async Task<bool> HasLiveRunAsync(Guid endpointId, DateTime now, CancellationToken ct)
    {
        var running = await db.DiagnosisRuns.Where(r => r.EndpointId == endpointId && r.Status == DiagRunStatus.Running).ToListAsync(ct);
        if (running.Count == 0) return false;
        var maxAge = ActionCatalog.Find(DiagnosticJob.ActionCode)!.Timeout + TimeSpan.FromMinutes(15);
        var live = false;
        foreach (var run in running)
        {
            var states = await db.JobTargets.AsNoTracking().Where(t => t.JobId == run.JobId).Select(t => t.Status).ToListAsync(ct);
            var stale = states.Count == 0 || states.All(IsFinal) || now - run.StartedUtc > maxAge;
            if (!stale) { live = true; continue; }
            run.Status = DiagRunStatus.Failed;
            run.Complete = false;
            run.FinishedUtc = now;
            run.EvidenceJson = null;
            run.ResultJson = JsonSerializer.Serialize(new DiagnosisResult(DiagnosisResult.Schema.Version, run.ProfileId, run.ProfileVersion, false, [], [], []), DiagnosisJson.Options);
            audit.Add(new Actor(run.InitiatedBySid, "", null), "diag.complete", "Endpoint", endpointId.ToString(), null, DiagRunStatus.Failed,
                new { runId = run.Id, jobId = run.JobId, profile = run.ProfileId, status = DiagRunStatus.Failed, reason = "stale run healed" }, success: false);
        }
        await db.SaveChangesAsync(ct);
        return live;
    }
}
