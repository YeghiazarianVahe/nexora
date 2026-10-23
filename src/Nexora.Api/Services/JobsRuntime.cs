using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Nexora.Api.Hubs;
using Nexora.Application;
using Nexora.Application.Diagnostics;
using Nexora.Application.Jobs;
using Nexora.Contracts;

namespace Nexora.Api.Services;

public static class ScriptParams
{
    /// <summary>The enabled catalog script with this id, or null (unknown id, malformed id, disabled).</summary>
    public static async Task<Nexora.Domain.ScriptDefinition?> LoadAsync(INexoraDb db, string? scriptId, CancellationToken ct) =>
        Guid.TryParse(scriptId, out var id)
            ? await db.Scripts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.Enabled, ct)
            : null;
}

public sealed class JobSignal : IJobSignal
{
    private readonly SemaphoreSlim _gate = new(0, 1);

    public void Poke()
    {
        try { _gate.Release(); } catch (SemaphoreFullException) { /* already pending */ }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _gate.WaitAsync(timeout, ct);
}

/// <summary>Drives <see cref="JobOrchestrator"/> on a timer and whenever something pokes it.</summary>
public sealed class JobRunner(
    IServiceScopeFactory scopes, JobSignal signal, IOptions<NexoraOptions> options, ILogger<JobRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var tick = TimeSpan.FromMilliseconds(Math.Max(20, options.Value.Jobs.TickMs));
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<JobOrchestrator>().TickAsync(stop);
                await signal.WaitAsync(tick, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job orchestration tick failed");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stop); } catch (OperationCanceledException) { return; }
            }
        }
    }
}

/// <summary>
/// Sends a target to the initiating operator's helper, signed with the server's key (which the helper has pinned).
/// If that operator's helper is not connected the target waits; it is never run under any other identity.
/// </summary>
public sealed class HelperJobExecutor(
    HelperRegistry registry, ServerTrust trust, IHubContext<HelperHub> hub, IServiceScopeFactory scopes,
    IOptions<FeatureOptions> features, IDiagnosticProfiles diagProfiles, IDiagnosticCollectors diagCollectors, ILogger<HelperJobExecutor> logger) : IJobExecutor
{
    public async Task<StartOutcome> StartAsync(JobStartContext c, CancellationToken ct)
    {
        var connection = registry.Find(c.Job.InitiatorSid);
        if (connection is null) return new StartOutcome(false, WaitForHelper: true);

        var parameters = new Dictionary<string, string>(c.Parameters);
        if (c.Action.Code == "script.run")
        {
            // The approved script travels inside the signed job, with its hash; the helper checks the hash again before running anything.
            using var scope = scopes.CreateScope();
            var script = await ScriptParams.LoadAsync(scope.ServiceProvider.GetRequiredService<INexoraDb>(), parameters.GetValueOrDefault("scriptId"), ct);
            if (script is null) return new StartOutcome(false, Error: "The script is not in the catalog or is disabled.");
            parameters["scriptBody"] = script.Body;
            parameters["scriptSha256"] = script.Sha256;
        }

        if (c.Action.Code == DiagnosticJob.ActionCode)
        {
            // The approved collector scripts and their hashes travel inside the signed job; the helper checks its own allow-list and every hash again.
            // The profile and the Exchange hosts were fixed by the server when the job was created.
            if (!features.Value.Diagnostics) return new StartOutcome(false, Error: "Diagnose is not enabled on this server.");
            var profile = diagProfiles.Find(parameters.GetValueOrDefault("profileId"));
            var collectors = profile is null ? null : DiagnosticJob.CollectorsJson(profile, diagCollectors);
            if (collectors is null) return new StartOutcome(false, Error: "The diagnosis profile or its collectors are not available on this server.");
            parameters["collectors"] = collectors;
        }

        var now = DateTime.UtcNow;
        var payload = new JobPayload(
            c.Job.Id, c.Target.Id, c.Action.Code, c.Target.Host, c.Job.InitiatorSid, parameters,
            (int)c.Timeout.TotalSeconds, now, now.AddMinutes(2), Guid.NewGuid().ToString("N"));
        try
        {
            await hub.Clients.Client(connection).SendAsync("ExecuteJobV2", trust.Sign(payload), ct);
            return new StartOutcome(true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not send job {JobId} to the helper", c.Job.Id);
            return new StartOutcome(false, WaitForHelper: true);
        }
    }

    public async Task CancelAsync(Guid targetId, string initiatorSid, CancellationToken ct)
    {
        var connection = registry.Find(initiatorSid);
        if (connection is not null) await hub.Clients.Client(connection).SendAsync("CancelJobV2", targetId, ct);
    }
}

/// <summary>Live updates to the Jobs pages and a toast for the initiator when a job finishes.</summary>
public sealed class SignalRJobEvents(IHubContext<NexoraHub> hub) : IJobEvents
{
    public async Task JobChangedAsync(JobSummary job)
    {
        await hub.Clients.Groups(HubGroups.User(job.InitiatorSid), HubGroups.JobsAdmin).SendAsync("JobChanged", job);
        await hub.Clients.Group(HubGroups.Job(job.Id)).SendAsync("JobChanged", job);
    }

    public Task TargetChangedAsync(JobTargetInfo target, string initiatorSid) =>
        hub.Clients.Group(HubGroups.Job(target.JobId)).SendAsync("JobTargetChanged", target);

    public Task LogAppendedAsync(Guid jobId, Guid targetId, string initiatorSid, IReadOnlyList<LogLine> lines) =>
        hub.Clients.Group(HubGroups.Job(jobId)).SendAsync("JobLog", new { jobId, targetId, lines });

    public Task JobFinishedAsync(JobSummary job) =>
        hub.Clients.Group(HubGroups.User(job.InitiatorSid)).SendAsync("JobFinished", job);
}

/// <summary>Tells the initiating operator's browser that a diagnosis was stored. Sent to the job's group, which that operator (or an Admin) joins with SubscribeJob.</summary>
public sealed class SignalRDiagnosisEvents(IHubContext<NexoraHub> hub) : IDiagnosisEvents
{
    public Task DiagnosisCompletedAsync(Guid jobId, Guid runId) =>
        hub.Clients.Group(HubGroups.Job(jobId)).SendAsync("DiagnosisCompleted", runId);
}
