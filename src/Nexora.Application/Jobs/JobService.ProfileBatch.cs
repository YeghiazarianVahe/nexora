using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Application.Jobs;

// ---------- Profile deletion in batches (Admin only) and the server-side size store ----------
// A batch is a convenience over the single profile.delete catalog action, never a bypass: every profile gets its own job (and so its own audit rows),
// is re-verified against the PC at that moment, and nothing is created without the single-use confirmation token AND the typed workstation name.

public sealed record ProfileBatchConfirmRequest(List<string>? Sids);

public sealed record ProfileBatchDeleteRequest(List<string>? Sids, string? Token, string? Typed, string? Reason);

/// <param name="SizeBytes">The server's own last measurement, or null when the profile was never measured.</param>
public sealed record ProfileBatchItem(string Sid, string? Account, string Path, long? SizeBytes);

public sealed record ProfileBatchConfirmResponse(string Token, string ExpectedHostname, int Count, IReadOnlyList<ProfileBatchItem> Items);

/// <param name="JobIds">In the same order as <see cref="ProfileBatchConfirmResponse.Items"/> (SIDs sorted ordinally).</param>
public sealed record ProfileBatchCreated(Guid BatchId, IReadOnlyList<Guid> JobIds);

public sealed record SidReason(string Sid, string Reason);

/// <summary>A result plus, when a whole batch was refused because of individual profiles, why each one failed.</summary>
public sealed record BatchResult<T>(Result<T> Result, IReadOnlyList<SidReason> Reasons) where T : class;

public interface IProfileBatchService
{
    Task<BatchResult<ProfileBatchConfirmResponse>> ConfirmAsync(JobActor actor, Guid endpointId, ProfileBatchConfirmRequest request, CancellationToken ct = default);
    Task<BatchResult<ProfileBatchCreated>> DeleteAsync(JobActor actor, Guid endpointId, ProfileBatchDeleteRequest request, CancellationToken ct = default);
}

public sealed partial class JobService : IProfileBatchService
{
    public const int MaxBatchProfiles = 50;
    private const int MaxRawBatchSids = 500;
    private const string BatchTokenAction = "profile.delete.batch";
    private const string BatchFailurePolicy = "continue";   // documented in the report: a failed profile never stops the others

    [GeneratedRegex(@"^S-1-(5-21|12-1)(-\d{1,10}){3,15}\z")]
    private static partial Regex BatchSid();

    private sealed record BatchPlan(ManagedEndpoint Endpoint, List<string> Sids, List<ProfileInfo> Profiles, Dictionary<string, ProfileSize> Sizes, RiskLevel Risk);

    private static BatchResult<T> Refuse<T>(ErrorKind kind, string message, IReadOnlyList<SidReason>? reasons = null) where T : class =>
        new(Result<T>.Fail(kind, message), reasons ?? []);

    /// <summary>Audits a refused batch request as job.denied. Never throws.</summary>
    private async Task<BatchResult<T>> DeniedBatchAsync<T>(JobActor actor, string stage, Guid endpointId, IReadOnlyCollection<string> sids, BatchResult<T> failure) where T : class
    {
        if (failure.Result.Kind is ErrorKind.Validation or ErrorKind.Forbidden or ErrorKind.NotImplemented or ErrorKind.Conflict or ErrorKind.Unavailable)
        {
            var reasons = failure.Reasons.Count == 0 ? failure.Result.Message
                : failure.Result.Message + " " + string.Join("; ", failure.Reasons.Take(5).Select(r => $"{r.Sid}: {r.Reason}"));
            var parameters = new Dictionary<string, JsonElement>
            {
                ["sidCount"] = JsonSerializer.SerializeToElement(sids.Count),
                ["sids"] = JsonSerializer.SerializeToElement(string.Join(",", sids.Take(5))),
            };
            await denials.RecordAsync(actor.Actor, stage, "profile.delete", [endpointId], parameters, failure.Result.Kind, reasons);
        }
        return failure;
    }

    /// <summary>What a batch token is bound to: the workstation name, the sorted SIDs and the effective risk. The operator is bound by the token itself.</summary>
    private static string BatchBinding(string hostname, IEnumerable<string> sortedSids, RiskLevel risk) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            hostname.ToUpperInvariant() + "|" + string.Join(",", sortedSids) + "|risk:" + ((int)risk).ToString(CultureInfo.InvariantCulture))));

    /// <summary>The checks shared by confirm and delete. On success every requested profile has just been verified against the PC.</summary>
    private async Task<(BatchPlan? Plan, BatchResult<T>? Failure)> PlanBatchAsync<T>(JobActor actor, Guid endpointId, List<string>? requested, string stage, CancellationToken ct) where T : class
    {
        var raw = (requested ?? []).Select(s => (s ?? "").Trim().ToUpperInvariant()).ToList();
        async Task<(BatchPlan?, BatchResult<T>?)> Fail(ErrorKind kind, string message, IReadOnlyList<SidReason>? reasons = null) =>
            (null, await DeniedBatchAsync(actor, stage, endpointId, raw, Refuse<T>(kind, message, reasons)));

        if (actor.Role is not { } role || (int)role < (int)Role.Admin) return await Fail(ErrorKind.Forbidden, "Deleting profiles needs the Admin role.");
        if (raw.Count == 0) return await Fail(ErrorKind.Validation, "Pick at least one profile.");
        if (raw.Count > MaxRawBatchSids) return await Fail(ErrorKind.Validation, "Too many profiles in one request.");

        var sids = raw.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (sids.Count > MaxBatchProfiles) return await Fail(ErrorKind.Validation, $"At most {MaxBatchProfiles} profiles per batch.");
        var malformed = sids.Where(s => !BatchSid().IsMatch(s)).Take(10).Select(s => new SidReason(s.Length > 200 ? s[..200] : s, "Not a user account SID.")).ToList();
        if (malformed.Count > 0) return await Fail(ErrorKind.Validation, "A SID is not a user account SID.", malformed);

        var endpoint = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(e => e.Id == endpointId, ct);
        if (endpoint is null) return (null, Refuse<T>(ErrorKind.NotFound, "Endpoint not found."));
        if (ProtectedReason(endpoint, await settings.GetAsync(ct)) is { } why)
            return await Fail(ErrorKind.Validation, $"Protected workstation ({why}): profiles cannot be deleted here.");

        // One profile deletion at a time on a PC: the jobs of a batch run in turn, and a second batch must wait for the first.
        if (await ProfileDeletionActiveAsync(endpointId, ct)) return await Fail(ErrorKind.Conflict, "A profile deletion is already queued or running on this workstation. Wait for it to finish.");

        // Fresh read, never the cache: the world may have changed since the list was shown.
        var read = await profileReader.ReadAsync(endpoint.Hostname, TimeSpan.Zero, ct);
        if (!read.Ok) return await Fail(ErrorKind.Unavailable, $"The profile list of {endpoint.DisplayName ?? endpoint.Hostname} could not be read, so the deletion cannot be checked. The PC must be reachable.");

        var profiles = new List<ProfileInfo>();
        var reasons = new List<SidReason>();
        foreach (var sid in sids)
        {
            var judged = JudgeProfile(read.Value!, sid, null, actor.Sid);
            if (judged.Ok) profiles.Add(judged.Value!);
            else reasons.Add(new SidReason(sid, judged.Message!));
        }
        if (reasons.Count > 0)
            return await Fail(ErrorKind.Validation, $"{reasons.Count} of {sids.Count} profile(s) cannot be deleted; nothing was changed.", reasons);

        var sizes = (await db.ProfileSizes.AsNoTracking().Where(s => s.EndpointId == endpointId).ToListAsync(ct)).ToDictionary(s => s.Sid, StringComparer.OrdinalIgnoreCase);
        var def = ActionCatalog.Find("profile.delete")!;
        var risk = await EffectiveRiskAsync(def, [endpoint], [], ct);
        return (new BatchPlan(endpoint, sids, profiles, sizes, risk), null);
    }

    public async Task<BatchResult<ProfileBatchConfirmResponse>> ConfirmAsync(JobActor actor, Guid endpointId, ProfileBatchConfirmRequest request, CancellationToken ct = default)
    {
        var (plan, failure) = await PlanBatchAsync<ProfileBatchConfirmResponse>(actor, endpointId, request.Sids, "profile-batch-confirm", ct);
        if (plan is null) return failure!;

        var token = tokens.Issue(actor.Sid, BatchTokenAction, TargetsHash.Of([plan.Endpoint.Id]), BatchBinding(plan.Endpoint.Hostname, plan.Sids, plan.Risk), false, clock.UtcNow);
        var items = plan.Profiles.Select(p => new ProfileBatchItem(p.Sid, string.IsNullOrWhiteSpace(p.Account) ? null : p.Account, p.LocalPath, plan.Sizes.GetValueOrDefault(p.Sid)?.SizeBytes)).ToList();
        return new(Result<ProfileBatchConfirmResponse>.Success(new ProfileBatchConfirmResponse(token.Token, plan.Endpoint.Hostname, items.Count, items)), []);
    }

    public async Task<BatchResult<ProfileBatchCreated>> DeleteAsync(JobActor actor, Guid endpointId, ProfileBatchDeleteRequest request, CancellationToken ct = default)
    {
        const string stage = "profile-batch-delete";
        var (plan, failure) = await PlanBatchAsync<ProfileBatchCreated>(actor, endpointId, request.Sids, stage, ct);
        if (plan is null) return failure!;
        var sids = plan.Sids;

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason)) return await DeniedBatchAsync(actor, stage, endpointId, sids, Refuse<ProfileBatchCreated>(ErrorKind.Validation, "A reason is required for this action."));
        if (reason.Length > 500) return await DeniedBatchAsync(actor, stage, endpointId, sids, Refuse<ProfileBatchCreated>(ErrorKind.Validation, "The reason is too long (max 500)."));

        // The workstation's host name, typed by the person: the account or the number of profiles do not count.
        if (!string.Equals(request.Typed?.Trim(), plan.Endpoint.Hostname, StringComparison.OrdinalIgnoreCase))
            return await DeniedBatchAsync(actor, stage, endpointId, sids, Refuse<ProfileBatchCreated>(ErrorKind.Validation, "Type the workstation name to confirm."));

        // Checks that can still fail are done; only now is the single-use token spent.
        if (!tokens.Consume(request.Token, actor.Sid, BatchTokenAction, TargetsHash.Of([plan.Endpoint.Id]), BatchBinding(plan.Endpoint.Hostname, sids, plan.Risk), false, clock.UtcNow))
            return await DeniedBatchAsync(actor, stage, endpointId, sids, Refuse<ProfileBatchCreated>(ErrorKind.Validation, "Confirmation is missing, expired or does not match this request. Confirm again."));

        var now = clock.UtcNow;
        var batchId = Guid.NewGuid();
        var created = new List<Job>();
        for (var i = 0; i < plan.Profiles.Count; i++)
        {
            var p = plan.Profiles[i];
            var stored = plan.Sizes.GetValueOrDefault(p.Sid);
            var parameters = new Dictionary<string, string> { ["sid"] = p.Sid, ["localPath"] = p.LocalPath };
            if (stored is not null) parameters["sizeBytes"] = stored.SizeBytes.ToString(CultureInfo.InvariantCulture);

            var job = new Job
            {
                Id = Guid.NewGuid(), ActionCode = "profile.delete", ParametersJson = JsonSerializer.Serialize(parameters),
                InitiatorSid = actor.Sid, InitiatorAccount = actor.Actor.Account,
                CreatedUtc = now.AddMilliseconds(i),   // the orchestrator starts a batch in this order
                Status = JobState.Queued, Reason = reason, ProtectedOverride = false, ClientIp = actor.Actor.ClientIp, TargetCount = 1, BatchId = batchId,
            };
            db.Jobs.Add(job);
            db.JobTargets.Add(new JobTarget { Id = Guid.NewGuid(), JobId = job.Id, EndpointId = plan.Endpoint.Id, EndpointName = plan.Endpoint.DisplayName ?? plan.Endpoint.Hostname, Host = plan.Endpoint.Hostname });
            audit.Add(actor.Actor, "job.create", "Job", job.Id.ToString(), null, "profile.delete",
                new { action = "profile.delete", risk = RiskLevel.Critical.ToString(), effectiveRisk = plan.Risk.ToString(), targets = new[] { plan.Endpoint.DisplayName ?? plan.Endpoint.Hostname }, count = 1,
                      parameters, reason, protectedOverride = false, batchId, batchIndex = i + 1, batchCount = plan.Profiles.Count, failurePolicy = BatchFailurePolicy,
                      profile = ProfileAudit(p, stored) });
            created.Add(job);
        }
        await db.SaveChangesAsync(ct);

        signal.Poke();
        foreach (var job in created) await events.JobChangedAsync(Summarize(job, [TargetState.Queued]));
        return new(Result<ProfileBatchCreated>.Success(new ProfileBatchCreated(batchId, created.Select(j => j.Id).ToList())), []);
    }

    // ---------- shared with the single-job path ----------

    /// <summary>True while any profile.delete job (single or batch) is queued or running on this workstation: one profile deletion at a time per PC.</summary>
    private Task<bool> ProfileDeletionActiveAsync(Guid endpointId, CancellationToken ct) =>
        db.JobTargets.AsNoTracking().AnyAsync(t => t.EndpointId == endpointId && (t.Status == TargetState.Queued || t.Status == TargetState.Running)
            && db.Jobs.Any(j => j.Id == t.JobId && j.ActionCode == "profile.delete"), ct);

    /// <summary>The parameters stored with the job: the validated ones, plus the server's own last size measurement for profile.delete (never a client value).</summary>
    private static Dictionary<string, string> JobParameters(Checked c)
    {
        if (c.Def.Code != "profile.delete" || c.StoredSize is not { } size) return c.Parameters;
        return new Dictionary<string, string>(c.Parameters) { ["sizeBytes"] = size.SizeBytes.ToString(CultureInfo.InvariantCulture) };
    }

    private static object? ProfileAudit(ProfileInfo? p, ProfileSize? stored) =>
        p is null ? null : new { account = p.Account, path = p.LocalPath, sid = p.Sid, lastUseUtc = p.LastUseUtc, sizeBytes = stored?.SizeBytes, sizeMeasuredUtc = stored?.MeasuredUtc, sizeSource = "helper-reported", measuredByJobId = stored?.JobId, measuredBySid = stored?.MeasuredBySid };

    /// <summary>The audit details of a finished target. For a profile deletion they name the profile, so the result row stands on its own.</summary>
    private static Dictionary<string, object?> JobTargetAudit(Job j, JobTarget t, int? exitCode, DateTime now)
    {
        var d = new Dictionary<string, object?>
        {
            ["jobId"] = j.Id, ["targetId"] = t.Id, ["action"] = j.ActionCode, ["exitCode"] = exitCode,
            ["seconds"] = t.StartedUtc is { } s ? (now - s).TotalSeconds : null, ["outputSha256"] = t.OutputSha256, ["summary"] = t.ResultSummary,
        };
        if (j.BatchId is { } batch) d["batchId"] = batch;
        if (j.ActionCode == "profile.delete")
        {
            try
            {
                var p = JsonSerializer.Deserialize<Dictionary<string, string>>(j.ParametersJson) ?? [];
                d["profile"] = new { sid = p.GetValueOrDefault("sid"), path = p.GetValueOrDefault("localPath"), sizeBytes = p.GetValueOrDefault("sizeBytes") };
            }
            catch (JsonException) { /* the job row is ours; an unreadable one just adds nothing */ }
        }
        return d;
    }

    // ---------- the size store ----------

    /// <summary>
    /// Writes the measurements of a finished profile.sizes job: one row per (workstation, SID), replaced by a newer measurement. The numbers are read
    /// from the helper's job output on the server, never from a browser request. Best effort: a failure here must not fail the job.
    /// </summary>
    private async Task StoreProfileSizesAsync(Job job, JobTarget target, string outputText, DateTime now)
    {
        try
        {
            string? only = null;
            try { only = (JsonSerializer.Deserialize<Dictionary<string, string>>(job.ParametersJson) ?? []).GetValueOrDefault("sid"); }
            catch (JsonException) { /* no usable parameters: accept every SID in the output */ }

            var measured = ProfileSizeOutput.Parse(outputText.Split('\n'), string.IsNullOrEmpty(only) ? null : only);
            if (measured.Count == 0) return;

            var existing = (await db.ProfileSizes.Where(s => s.EndpointId == target.EndpointId).ToListAsync())
                .ToDictionary(s => s.Sid, StringComparer.OrdinalIgnoreCase);
            foreach (var m in measured)
            {
                if (existing.TryGetValue(m.Sid, out var row)) { row.SizeBytes = m.Bytes; row.MeasuredUtc = now; row.JobId = job.Id; row.MeasuredBySid = job.InitiatorSid; }
                else db.ProfileSizes.Add(new ProfileSize { Id = Guid.NewGuid(), EndpointId = target.EndpointId, Sid = m.Sid, SizeBytes = m.Bytes, MeasuredUtc = now, JobId = job.Id, MeasuredBySid = job.InitiatorSid });
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Profile sizes of job {JobId} could not be stored", job.Id);
            foreach (var e in db.ChangeTracker.Entries<ProfileSize>().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList()) e.State = EntityState.Detached;
        }
    }
}
