using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Application.Jobs;

public sealed class JobsOptions
{
    /// <summary>Helper (the operator's own session; the real thing) or Fake (simulated, for tests and demos).</summary>
    public string Executor { get; set; } = "Helper";
    public int TickMs { get; set; } = 1000;
    /// <summary>Multiplies every action timeout. Tests use a small value; production leaves it at 1.</summary>
    public double TimeoutScale { get; set; } = 1.0;
    public int CancelGraceSeconds { get; set; } = 15;
    /// <summary>A target that has not started this long after the job was created is skipped, not run late (the world may have changed).</summary>
    public double QueueTtlMinutes { get; set; } = 10;
    /// <summary>Same for Power actions and Critical-risk actions, which go stale faster.</summary>
    public double PowerQueueTtlMinutes { get; set; } = 2;
    /// <summary>Finished jobs, their targets and their output are deleted after this many days. The audit log is kept.</summary>
    public int RetentionDays { get; set; } = 90;
}

/// <summary>
/// Moves jobs through their lifecycle: starts queued targets within the parallelism limit, enforces timeouts and cancel,
/// and finishes jobs. One tick is idempotent, so it can run on a timer and on demand.
/// </summary>
public sealed class JobOrchestrator(
    INexoraDb db,
    JobService jobs,
    ISettingsService settings,
    IUserAccessCache access,
    ISuperAdminChecker superAdmin,
    IJobExecutor executor,
    IOptions<NexoraOptions> options,
    IOptions<FeatureOptions> features,
    ISystemClock clock,
    ILogger<JobOrchestrator> logger)
{
    private static readonly ConcurrentDictionary<Guid, byte> CancelSent = new();

    public async Task TickAsync(CancellationToken ct)
    {
        var opts = options.Value.Jobs;
        var s = await settings.GetAsync(ct);
        var active = await db.Jobs
            .Where(j => j.Status == JobState.Queued || j.Status == JobState.WaitingForHelper || j.Status == JobState.Running)
            .OrderBy(j => j.CreatedUtc).Take(200).ToListAsync(ct);

        foreach (var job in active)
        {
            try { await ProcessJobAsync(job, s, opts, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Job {JobId} could not be processed", job.Id); }
        }
    }

    private async Task ProcessJobAsync(Job job, PlatformSettings settings, JobsOptions opts, CancellationToken ct)
    {
        var def = ActionCatalog.Find(job.ActionCode);
        var targets = await db.JobTargets.Where(t => t.JobId == job.Id).OrderBy(t => t.EndpointName).ToListAsync(ct);
        var now = clock.UtcNow;
        foreach (var done in targets.Where(t => t.Status != TargetState.Running)) CancelSent.TryRemove(done.Id, out _); // the set must not grow forever

        if (def is null)
        {
            foreach (var t in targets.Where(t => t.Status == TargetState.Queued))
                await jobs.CompletedAsync(t.Id, TargetState.Failed, null, "The action no longer exists in the catalog.", null);
            await jobs.RefreshJobStateAsync(job);
            return;
        }

        // ---- cancel
        if (job.CancelRequestedUtc is { } requested)
        {
            foreach (var t in targets.Where(t => t.Status == TargetState.Queued))
                await jobs.CompletedAsync(t.Id, TargetState.Cancelled, null, "Cancelled before it started.", null);

            foreach (var t in targets.Where(t => t.Status == TargetState.Running))
            {
                if (def.SupportsCancel && CancelSent.TryAdd(t.Id, 0)) await executor.CancelAsync(t.Id, job.InitiatorSid, ct);
                var forced = (now - requested).TotalSeconds > opts.CancelGraceSeconds;
                if (def.SupportsCancel && forced) await jobs.CompletedAsync(t.Id, TargetState.Cancelled, null, "Cancelled by the operator.", null);
            }
            await jobs.RefreshJobStateAsync(job);
            return;
        }

        // ---- timeouts
        var timeout = TimeSpan.FromSeconds(def.Timeout.TotalSeconds * Math.Max(0.001, opts.TimeoutScale));
        foreach (var t in targets.Where(t => t.Status == TargetState.Running && t.StartedUtc is { } st && now - st > timeout))
        {
            // Record the timeout first: the first final state wins, so the executor's own "cancelled" report cannot replace it.
            await jobs.CompletedAsync(t.Id, TargetState.TimedOut, null, $"Timed out after {Format(timeout)}.", null);
            try { await executor.CancelAsync(t.Id, job.InitiatorSid, ct); } catch (Exception ex) { logger.LogDebug(ex, "Cancel on timeout failed"); }
        }

        // ---- dispatch within the parallelism limit
        var running = targets.Count(t => t.Status == TargetState.Running);
        var free = Math.Max(1, settings.JobParallelism) - running;
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(job.ParametersJson) ?? [];

        // The queue TTL runs from the last sign of progress, so a long batch that is merely waiting for a free slot is not skipped.
        var lastProgress = targets.Select(t => t.FinishedUtc ?? t.StartedUtc).Where(d => d.HasValue).Select(d => d!.Value).DefaultIfEmpty(job.CreatedUtc).Max();
        if (lastProgress < job.CreatedUtc) lastProgress = job.CreatedUtc;
        // Profile deletions run one at a time per workstation, whichever job (single or batch) they belong to: nothing starts while another
        // profile.delete target of the same PC is running, and that job's progress counts as this job's own, so a long queue that is merely
        // waiting its turn is not skipped by the queue TTL.
        var siblingBusy = false;
        if (def.Code == "profile.delete")
        {
            var endpointIds = targets.Select(t => t.EndpointId).Distinct().ToList();
            var siblings = db.JobTargets.Where(t => endpointIds.Contains(t.EndpointId) && t.JobId != job.Id && db.Jobs.Any(o => o.Id == t.JobId && o.ActionCode == "profile.delete"));
            siblingBusy = await siblings.AnyAsync(t => t.Status == TargetState.Running, ct);
            // Only live siblings of this PC (queued or running), and the finished ones of this job's own batch, extend the wait; old history does not.
            var live = siblings.Where(t => t.Status == TargetState.Queued || t.Status == TargetState.Running);
            var batch = job.BatchId is { } bid ? siblings.Where(t => db.Jobs.Any(o => o.Id == t.JobId && o.BatchId == bid)) : siblings.Where(t => false);
            var marks = new[] { await live.MaxAsync(t => t.StartedUtc, ct), await batch.MaxAsync(t => t.FinishedUtc, ct), await batch.MaxAsync(t => t.StartedUtc, ct) };
            foreach (var d in marks.Where(d => d.HasValue)) if (d!.Value > lastProgress) lastProgress = d.Value;
            if (siblingBusy) free = 0;
        }
        var anyRunning = running > 0 || siblingBusy;

        foreach (var target in targets.Where(t => t.Status == TargetState.Queued))
        {
            if (free <= 0) break;

            // Re-checked at the moment of dispatch: a queued job may have waited for a helper while roles, protection or the catalog changed.
            if (await SkipReasonAsync(job, def, target, settings, opts, now, anyRunning ? now : lastProgress, ct) is { } why)
            {
                await jobs.CompletedAsync(target.Id, TargetState.Skipped, null, why, null);
                continue;
            }

            target.Status = TargetState.Running;
            target.StartedUtc = now;
            await db.SaveChangesAsync(ct);

            var outcome = await executor.StartAsync(new JobStartContext(job, target, def, parameters, timeout), ct);
            if (outcome.Started) { free--; continue; }

            if (outcome.WaitForHelper)
            {
                // The operator's helper is offline: put the target back and wait. Nothing runs under any other identity.
                target.Status = TargetState.Queued;
                target.StartedUtc = null;
                job.Status = JobState.WaitingForHelper;
                await db.SaveChangesAsync(ct);
                break;
            }
            await jobs.CompletedAsync(target.Id, TargetState.Failed, null, outcome.Error ?? "Could not start the job.", null);
        }

        await jobs.RefreshJobStateAsync(job);
    }

    /// <summary>Why a still-queued target must not start now, or null when it may. Nothing is ever run on stale authority.</summary>
    public async Task<string?> SkipReasonAsync(Job job, ActionDefinition def, JobTarget target, PlatformSettings settings, JobsOptions opts, DateTime now, DateTime lastProgress, CancellationToken ct)
    {
        var ttl = TimeSpan.FromMinutes(def.IsPower || def.Risk == RiskLevel.Critical ? opts.PowerQueueTtlMinutes : opts.QueueTtlMinutes);
        if (now - lastProgress > ttl)
            return $"Not started within {Format(ttl)} (the operator's helper was not available in time). Run it again if it is still needed.";

        // diag.collect is not "Implemented" for the generic job endpoints (they refuse it); it runs only when the diagnosis feature is on.
        var diagnosis = def.Code == Diagnostics.DiagnosticJob.ActionCode && features.Value.Diagnostics;
        if (!def.Implemented && !def.Demo && !diagnosis) return "The action is not available any more.";

        // The SuperAdmin is identified by the configured SID, not by a row in the users table, so the table cannot be asked about them.
        Role? role = superAdmin.IsSuperAdmin(job.InitiatorSid) ? Role.Admin : (await access.GetAsync(job.InitiatorSid, null, ct)).Role;
        if (role is null || (int)role.Value < (int)def.MinRole)
            return $"Skipped: the operator no longer has the {def.MinRole} role needed for '{def.DisplayName}'.";

        var endpoint = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(e => e.Id == target.EndpointId, ct);
        if (endpoint is null) return "Skipped: the workstation was removed from the inventory.";
        if (!string.Equals(endpoint.Hostname, target.Host, StringComparison.OrdinalIgnoreCase))
            return "Skipped: the workstation address changed after this job was created. Run it again for the current address.";
        if ((def.ChecksProtected || diagnosis) && !job.ProtectedOverride && JobService.ProtectedReason(endpoint, settings) is { } protectedWhy)
            return $"Skipped: the workstation is now protected ({protectedWhy}).";
        return null;
    }

    private static string Format(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0.#} min" : $"{t.TotalSeconds:0.#} s";
}

/// <summary>
/// Simulates execution on the server. Used by the tests and available as <c>Jobs:Executor = Fake</c> for demos; the default
/// executor sends the job to the initiator's helper instead. Output, progress, cancel and timeout behave like the real thing.
/// </summary>
public sealed class FakeJobExecutor(IServiceScopeFactory scopes) : IJobExecutor
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public Task<StartOutcome> StartAsync(JobStartContext c, CancellationToken ct)
    {
        var cts = new CancellationTokenSource();
        _running[c.Target.Id] = cts;
        _ = c.Action.Code == Diagnostics.DiagnosticJob.ActionCode
            ? Task.Run(() => RunDiagnosisAsync(c.Target.Id, c.Parameters))
            : Task.Run(() => RunAsync(c.Target.Id, c.Parameters, cts.Token));
        return Task.FromResult(new StartOutcome(true));
    }

    /// <summary>
    /// Stands in for the helper's diag.collect: prints the lines an optional <see cref="IFakeDiagnosticOutput"/> supplies (tests), otherwise one
    /// "incomplete" line per requested collector. Never runs anything.
    /// </summary>
    private async Task RunDiagnosisAsync(Guid targetId, IReadOnlyDictionary<string, string> p)
    {
        using var scope = scopes.CreateScope();
        var reports = scope.ServiceProvider.GetRequiredService<IJobReports>();
        try
        {
            await reports.StartedAsync(targetId, null);
            var fake = scope.ServiceProvider.GetService<IFakeDiagnosticOutput>()?.Run(p);
            var lines = fake?.Lines ?? (p.GetValueOrDefault("collectorIds") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => $"##NXC {id} {{\"incomplete\":true,\"reason\":\"simulated\"}}").ToList();
            if (lines.Count > 0) await reports.LogAsync(targetId, "stdout", lines, null);
            await reports.CompletedAsync(targetId, fake?.State ?? TargetState.Succeeded, 0, "Simulated diagnosis collection.", null);
        }
        finally { _running.TryRemove(targetId, out _); }
    }

    public Task CancelAsync(Guid targetId, string initiatorSid, CancellationToken ct)
    {
        if (_running.TryGetValue(targetId, out var cts)) cts.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunAsync(Guid targetId, IReadOnlyDictionary<string, string> p, CancellationToken token)
    {
        int Int(string key, int fallback) => p.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;
        var steps = Int("steps", 5);
        var delay = Int("delayMs", 400);
        var outcome = p.GetValueOrDefault("outcome", "success");

        async Task<T> With<T>(Func<IJobReports, Task<T>> action)
        {
            using var scope = scopes.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<IJobReports>());
        }

        try
        {
            await With(r => r.StartedAsync(targetId, null));
            await With(r => r.LogAsync(targetId, "system", ["[simulated] started"], null));
            for (var i = 1; i <= steps; i++)
            {
                await Task.Delay(delay, token);
                await With(r => r.LogAsync(targetId, "stdout", [$"step {i} of {steps}"], null));
                await With(r => r.ProgressAsync(targetId, i * 100 / steps, null));
            }
            if (outcome == "hang") await Task.Delay(Timeout.Infinite, token);

            var (state, code, summary) = outcome switch
            {
                "fail" => (TargetState.Failed, 1, "Simulated failure."),
                "warning" => (TargetState.Warning, 0, "Finished with a simulated warning."),
                _ => (TargetState.Succeeded, 0, "Simulated run completed."),
            };
            await With(r => r.CompletedAsync(targetId, state, code, summary, null));
        }
        catch (OperationCanceledException)
        {
            await With(r => r.CompletedAsync(targetId, TargetState.Cancelled, null, "Cancelled.", null));
        }
        finally
        {
            _running.TryRemove(targetId, out _);
        }
    }
}


/// <summary>The output of a simulated diag.collect run; <see cref="State"/> must be a final state.</summary>
public sealed record FakeDiagnosticRun(IReadOnlyList<string> Lines, TargetState State = TargetState.Succeeded);

/// <summary>Optional test hook for <see cref="FakeJobExecutor"/>: what the simulated helper prints for a diag.collect job.</summary>
public interface IFakeDiagnosticOutput
{
    FakeDiagnosticRun Run(IReadOnlyDictionary<string, string> jobParameters);
}
