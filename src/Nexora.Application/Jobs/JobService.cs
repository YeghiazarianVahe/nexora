using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Application.Jobs;

public interface IJobService
{
    IReadOnlyList<CatalogEntry> Catalog(Role? role);
    Task<Result<ConfirmResponse>> ConfirmAsync(JobActor actor, ConfirmRequest request, CancellationToken ct = default);
    Task<Result<PreflightResult>> PreflightAsync(JobActor actor, PreflightRequest request, CancellationToken ct = default);
    Task<Result<JobDetail>> CreateAsync(JobActor actor, CreateJobRequest request, CancellationToken ct = default);
    Task<PagedResult<JobSummary>> ListAsync(JobActor actor, JobListQuery query, CancellationToken ct = default);
    Task<Result<JobDetail>> GetAsync(JobActor actor, Guid id, CancellationToken ct = default);
    Task<Result<IReadOnlyList<LogLine>>> LogAsync(JobActor actor, Guid jobId, Guid targetId, int afterSeq, int take, CancellationToken ct = default);
    Task<Result<string>> OutputTextAsync(JobActor actor, Guid jobId, Guid targetId, CancellationToken ct = default);
    Task<Result> CancelAsync(JobActor actor, Guid jobId, CancellationToken ct = default);
}

public sealed partial class JobService(
    INexoraDb db,
    ISettingsService settings,
    IAuditService audit,
    IConfirmTokens tokens,
    IJobEvents events,
    IJobSignal signal,
    IOptions<FeatureOptions> features,
    IProfileReader profileReader,
    ISystemClock clock,
    IRiskEscalator risk,
    IJobDenialAuditor denials,
    IServiceGuard serviceGuard,
    Diagnostics.IDiagnosticProfiles diagProfiles,
    Diagnostics.IDiagnosticCollectors diagCollectors,
    Diagnostics.IDiagnosisCompletion diagnosis,
    ILogger<JobService> logger) : IJobService, IJobReports
{
    private const int MaxTargets = 500;
    private const int MaxLinesPerTarget = 50_000;
    private const int MaxLineLength = 4_000;
    private static readonly TargetState[] Final =
        [TargetState.Succeeded, TargetState.Warning, TargetState.Failed, TargetState.Cancelled, TargetState.TimedOut, TargetState.Skipped];

    // ------------------------------------------------------------------ catalog

    public IReadOnlyList<CatalogEntry> Catalog(Role? role) =>
        ActionCatalog.All.Where(a => features.Value.DemoActions || (!a.Demo && a.Implemented)).Select(a => new CatalogEntry(
            a.Code, a.DisplayName, a.Category, a.Risk.ToString(), a.MinRole.ToString(), a.Method.ToString(),
            a.Parameters.Select(p => new ParameterInfo(p.Name, p.Type.ToString(), p.Label, p.Required, p.Min, p.Max, p.MaxLength, p.Allowed, p.Default, p.Help)).ToList(),
            (int)a.Timeout.TotalSeconds, a.Description, a.Command, a.SupportsCancel, a.SupportsBulk, a.RequiresUserNotice,
            a.NeedsConfirmation, a.NeedsReason, a.NeedsTypedConfirmation, a.Heavy, a.IsPower, a.Implemented, a.ChecksProtected,
            role is { } r && (int)r >= (int)a.MinRole)).ToList();

    // ------------------------------------------------------------------ confirm + create

    private sealed record Checked(
        ActionDefinition Def, List<ManagedEndpoint> Targets, Dictionary<string, string> Parameters, string? Reason, bool Override, RiskLevel Effective,
        ProfileInfo? Profile = null, ProfileSize? StoredSize = null);

    /// <summary>Everything that does not depend on the confirmation token: role, targets, parameters, reason, protected endpoints.</summary>
    private async Task<Result<Checked>> CheckAsync(
        JobActor actor, string actionCode, List<Guid>? endpointIds, Dictionary<string, JsonElement>? parameters,
        string? reason, bool overrideProtected, CancellationToken ct)
    {
        var def = ActionCatalog.Find(actionCode);
        if (def is null || (def.Demo && !features.Value.DemoActions)) return Result<Checked>.Fail(ErrorKind.Validation, "Unknown action.");
        if (actor.Role is not { } role || (int)role < (int)def.MinRole)
            return Result<Checked>.Fail(ErrorKind.Forbidden, $"'{def.DisplayName}' needs the {def.MinRole} role.");

        var ids = (endpointIds ?? []).Distinct().ToList();
        if (ids.Count == 0) return Result<Checked>.Fail(ErrorKind.Validation, "Pick at least one workstation.");
        if (ids.Count > MaxTargets) return Result<Checked>.Fail(ErrorKind.Validation, $"At most {MaxTargets} workstations per job.");
        if (ids.Count > 1 && !def.SupportsBulk) return Result<Checked>.Fail(ErrorKind.Validation, $"'{def.DisplayName}' runs on one workstation at a time.");

        // Targets always come from the inventory; the client never supplies a host name or address.
        var targets = await db.Endpoints.AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(ct);
        if (targets.Count != ids.Count) return Result<Checked>.Fail(ErrorKind.Validation, "One or more workstations are not in the inventory.");

        var (values, error) = ParameterValidator.Validate(def, ServiceRules.WithNameAlias(def.Code, parameters));
        if (error is not null) return Result<Checked>.Fail(ErrorKind.Validation, error);
        foreach (var spec in def.Parameters.Where(p => p.Type == ParamType.StringList && p.Max is not null))
            if (values!.TryGetValue(spec.Name, out var list) && list.Split(',', StringSplitOptions.RemoveEmptyEntries).Length > spec.Max)
                return Result<Checked>.Fail(ErrorKind.Validation, $"'{spec.Label}' has too many entries (max {spec.Max}).");

        if (await ServiceRefusalAsync(def, targets, values!, ct) is { } refusal) return Result<Checked>.Fail(ErrorKind.Validation, refusal);

        var effective = await EffectiveRiskAsync(def, targets, values!, ct);

        reason = reason?.Trim();
        if (ActionDefinition.NeedsReasonAt(effective) && string.IsNullOrEmpty(reason)) return Result<Checked>.Fail(ErrorKind.Validation, "A reason is required for this action.");
        if (reason is { Length: > 500 }) return Result<Checked>.Fail(ErrorKind.Validation, "The reason is too long (max 500).");

        if (ChecksProtectedAt(def, effective))
        {
            var s = await settings.GetAsync(ct);
            var blocked = targets.Select(t => (t, why: ProtectedReason(t, s))).Where(x => x.why is not null).ToList();
            if (blocked.Count > 0)
            {
                var list = string.Join(", ", blocked.Take(5).Select(b => $"{b.t.DisplayName ?? b.t.Hostname} ({b.why})"));
                if (def.Code == "profile.delete")   // never overridable: a profile on a protected workstation is not deleted from Nexora
                    return Result<Checked>.Fail(ErrorKind.Validation, $"Protected workstation(s): {list}. Profiles cannot be deleted on protected workstations, even with an override.");
                if (!overrideProtected)
                    return Result<Checked>.Fail(ErrorKind.Validation, $"Protected workstation(s): {list}. An Admin can override with a reason.");
                if (!actor.IsAdmin) return Result<Checked>.Fail(ErrorKind.Forbidden, "Only an Admin can override protected workstations.");
                if (string.IsNullOrEmpty(reason)) return Result<Checked>.Fail(ErrorKind.Validation, "A reason is required to override protected workstations.");
            }
            else overrideProtected = false;
        }
        else overrideProtected = false;

        // profile.delete is checked against what the PC reports right now, not against what the client claims.
        ProfileInfo? profile = null;
        ProfileSize? stored = null;
        if (def.Code == "profile.delete")
        {
            if (await ProfileDeletionActiveAsync(targets[0].Id, ct))
                return Result<Checked>.Fail(ErrorKind.Conflict, "A profile deletion is already queued or running on this workstation. Wait for it to finish.");
            var verified = await VerifyProfileAsync(targets[0], values!, actor.Sid, ct);
            if (!verified.Ok) return Result<Checked>.Fail(verified.Kind, verified.Message!);
            profile = verified.Value;
            stored = await StoredSizeAsync(targets[0].Id, profile!.Sid, ct);
        }

        return Result<Checked>.Success(new Checked(def, targets, values!, reason, overrideProtected, effective, profile, stored));
    }

    /// <summary>
    /// Server-side protection of critical services for stop and restart: the fixed floor plus the Admin list, whatever the role. With "stop dependents"
    /// the dependents are checked too (a protected one, or an unreadable list, refuses). Null when the request may go ahead.
    /// </summary>
    private async Task<string?> ServiceRefusalAsync(ActionDefinition def, IReadOnlyList<ManagedEndpoint> targets, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        if (!ServiceRules.IsStopOrRestart(def.Code) || !values.TryGetValue("serviceName", out var name)) return null;
        if (await serviceGuard.IsProtectedAsync(name, ct)) return $"'{name}' is a protected service and cannot be stopped or restarted.";
        return values.GetValueOrDefault("stopDependents") == "true" ? await serviceGuard.DependentsRefusalAsync(targets, name, ct) : null;
    }

    /// <summary>The risk the confirmation tier follows. Defaults to the catalog risk; <see cref="IRiskEscalator"/> may raise it, never lower it.</summary>
    private async Task<RiskLevel> EffectiveRiskAsync(ActionDefinition def, List<ManagedEndpoint> targets, Dictionary<string, string> parameters, CancellationToken ct)
    {
        var effective = await risk.EffectiveRiskAsync(def, targets, parameters, ct);
        return effective > def.Risk ? effective : def.Risk;
    }

    /// <summary>The protected-workstation check follows the effective risk, but only to switch it on: an escalator can never turn it off.</summary>
    private static bool ChecksProtectedAt(ActionDefinition def, RiskLevel effective) =>
        def.ChecksProtected || ActionDefinition.NeedsTypedConfirmationAt(effective);

    /// <summary>Audits a refused request (validation, authorization or not-implemented refusals) as job.denied. Never throws.</summary>
    private async Task<Result<T>> DeniedAsync<T>(JobActor actor, string stage, string actionCode, List<Guid>? ids,
        Dictionary<string, JsonElement>? parameters, Result<T> failure)
    {
        if (failure.Kind is ErrorKind.Validation or ErrorKind.Forbidden or ErrorKind.NotImplemented or ErrorKind.Conflict or ErrorKind.Unavailable)
            await denials.RecordAsync(actor.Actor, stage, actionCode, ids, parameters, failure.Kind, failure.Message);
        return failure;
    }

    /// <summary>
    /// The profile must exist on the PC with exactly this SID and folder (and, when asked, that folder), and pass <see cref="ProfileRules.BlockReason"/>:
    /// not a system profile, not loaded, not signed in (and who is signed in must be known), not the operator's own, account resolved.
    /// Returns what the PC reported (account, folder), which the audit entry uses.
    /// </summary>
    private static Result<ProfileInfo> JudgeProfile(ProfileSnapshot snapshot, string sid, string? expectedPath, string initiatorSid)
    {
        var found = snapshot.Profiles.FirstOrDefault(p => string.Equals(p.Sid, sid, StringComparison.OrdinalIgnoreCase));
        if (found is null) return Result<ProfileInfo>.Fail(ErrorKind.Validation, "That profile was not found on the PC. Refresh the list and try again.");
        if (expectedPath is not null && !string.Equals(found.LocalPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            return Result<ProfileInfo>.Fail(ErrorKind.Validation, $"The profile for that account is at '{found.LocalPath}', not '{expectedPath}'. Refresh the list and try again.");
        var block = ProfileRules.BlockReason(found, snapshot.SignedIn, initiatorSid, protectedReason: null, isAdmin: true, all: snapshot.Profiles);
        if (block is null) return Result<ProfileInfo>.Success(found);
        return Result<ProfileInfo>.Fail(ErrorKind.Validation, block == ProfileRules.SignedInUnknown ? "Could not verify who is signed in on that PC, so the deletion is refused." : block);
    }

    /// <summary>Reads the PC afresh (never from the cache) and judges the profile. Unreadable PC: Unavailable, nothing is accepted.</summary>
    private async Task<Result<ProfileInfo>> VerifyProfileAsync(ManagedEndpoint target, Dictionary<string, string> values, string initiatorSid, CancellationToken ct)
    {
        var read = await profileReader.ReadAsync(target.Hostname, TimeSpan.Zero, ct);
        if (!read.Ok)
            return Result<ProfileInfo>.Fail(ErrorKind.Unavailable, $"The profile list of {target.DisplayName ?? target.Hostname} could not be read, so the deletion cannot be checked. The PC must be reachable.");
        return JudgeProfile(read.Value!, values.GetValueOrDefault("sid") ?? "", values.GetValueOrDefault("localPath") ?? "", initiatorSid);
    }

    /// <summary>The server's own last measurement of this profile, or null. This is the only size that ever reaches a job or the audit log.</summary>
    private async Task<ProfileSize?> StoredSizeAsync(Guid endpointId, string sid, CancellationToken ct) =>
        (await db.ProfileSizes.AsNoTracking().Where(s => s.EndpointId == endpointId).ToListAsync(ct))
            .FirstOrDefault(s => string.Equals(s.Sid, sid, StringComparison.OrdinalIgnoreCase));

    /// <summary>What a confirmation is bound to besides the targets: the parameters AND the reason, so the reason cannot change between confirm and create.</summary>
    private static string BindingHash(Checked c) =>
        ParameterValidator.Hash(c.Parameters) + ":reason:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(c.Reason ?? "")))
        + ":risk:" + ((int)c.Effective).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The Nexora server is recognised by every name it can be reached by, not only the one typed into the inventory.</summary>
    private static readonly Lazy<HashSet<string>> ServerAddresses = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "::1", "localhost" };
        try
        {
            foreach (var ip in System.Net.Dns.GetHostAddresses(Environment.MachineName)) set.Add(ip.ToString());
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                foreach (var u in nic.GetIPProperties().UnicastAddresses) set.Add(u.Address.ToString());
        }
        catch (Exception) { /* name resolution is best effort; the names below still apply */ }
        return set;
    });

    private static string Short(string? name) => (name ?? "").Trim().Split('.')[0];

    private static bool SameHost(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(Short(a), Short(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why a workstation counts as protected, or null. By host name, FQDN or short name (an entry in the inventory may be any of them),
    /// by IP address, as the Nexora server itself, by the protected list, as a server OS, and when the OS is not known yet (never reached,
    /// so it cannot be shown to be a workstation).
    /// </summary>
    public static string? ProtectedReason(ManagedEndpoint e, PlatformSettings s)
    {
        if (e.IsPinned) return "Nexora server";
        if (SameHost(e.Hostname, Environment.MachineName) || SameHost(e.ReportedHostname, Environment.MachineName)
            || SameHost(e.DisplayName, Environment.MachineName)
            || (!string.IsNullOrWhiteSpace(e.IpAddress) && ServerAddresses.Value.Contains(e.IpAddress.Trim()))
            || (!string.IsNullOrWhiteSpace(e.Hostname) && ServerAddresses.Value.Contains(e.Hostname.Trim())))
            return "Nexora server";
        if (s.ProtectedHosts.Any(h => !string.IsNullOrWhiteSpace(h)
                && (SameHost(h, e.Hostname) || SameHost(h, e.ReportedHostname)
                    || string.Equals(h.Trim(), e.IpAddress, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(h.Trim(), e.Hostname, StringComparison.OrdinalIgnoreCase))))
            return "protected list";
        if (e.OsCaption?.Contains("Server", StringComparison.OrdinalIgnoreCase) == true) return "server OS";
        if (string.IsNullOrWhiteSpace(e.OsCaption)) return "operating system not known yet";
        return null;
    }

    public async Task<Result<PreflightResult>> PreflightAsync(JobActor actor, PreflightRequest r, CancellationToken ct = default)
    {
        var def = ActionCatalog.Find(r.ActionCode);
        if (def is null || (def.Demo && !features.Value.DemoActions)) return Result<PreflightResult>.Fail(ErrorKind.Validation, "Unknown action.");
        if (actor.Role is not { } role || (int)role < (int)def.MinRole)
            return Result<PreflightResult>.Fail(ErrorKind.Forbidden, $"'{def.DisplayName}' needs the {def.MinRole} role.");
        var ids = (r.EndpointIds ?? []).Distinct().ToList();
        if (ids.Count == 0 || ids.Count > MaxTargets) return Result<PreflightResult>.Fail(ErrorKind.Validation, $"Pick 1-{MaxTargets} workstations.");

        var targets = await db.Endpoints.AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(ct);
        if (targets.Count != ids.Count) return Result<PreflightResult>.Fail(ErrorKind.Validation, "One or more workstations are not in the inventory.");

        Dictionary<string, string> values = [];
        if (r.Parameters is not null)
        {
            var (checkedValues, error) = ParameterValidator.Validate(def, ServiceRules.WithNameAlias(def.Code, r.Parameters));
            if (error is not null) return await DeniedAsync(actor, "preflight", r.ActionCode, r.EndpointIds, r.Parameters, Result<PreflightResult>.Fail(ErrorKind.Validation, error));
            values = checkedValues!;
            if (await ServiceRefusalAsync(def, targets, values, ct) is { } refusal)
                return await DeniedAsync(actor, "preflight", r.ActionCode, r.EndpointIds, r.Parameters, Result<PreflightResult>.Fail(ErrorKind.Validation, refusal));
        }

        string Name(ManagedEndpoint e) => e.DisplayName ?? e.Hostname;
        var offline = targets.Where(t => !t.Online).Select(Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var blocked = new List<PreflightProtected>();
        // Without parameters the escalator sees none; the real check at confirm/create always uses the validated ones.
        var effective = await EffectiveRiskAsync(def, targets, values, ct);
        if (ChecksProtectedAt(def, effective))
        {
            var s = await settings.GetAsync(ct);
            blocked = targets.Select(t => (t, why: ProtectedReason(t, s))).Where(x => x.why is not null)
                .Select(x => new PreflightProtected(Name(x.t), x.why!)).ToList();
        }
        var busy = def.Heavy ? await BusyHeavyTargetsAsync(ids, ct) : [];
        return Result<PreflightResult>.Success(new PreflightResult(targets.Count, targets.Count - offline.Count, offline, blocked, busy, blocked.Count > 0, effective.ToString()));
    }

    public async Task<Result<ConfirmResponse>> ConfirmAsync(JobActor actor, ConfirmRequest r, CancellationToken ct = default)
    {
        var check = await CheckAsync(actor, r.ActionCode, r.EndpointIds, r.Parameters, r.Reason, r.OverrideProtected, ct);
        if (!check.Ok) return await DeniedAsync(actor, "confirm", r.ActionCode, r.EndpointIds, r.Parameters, Result<ConfirmResponse>.Fail(check.Kind, check.Message!));
        var c = check.Value!;

        if (ActionDefinition.NeedsTypedConfirmationAt(c.Effective))
        {
            var typed = r.Typed?.Trim() ?? "";
            // A profile deletion is confirmed like any other single-workstation action: by typing the workstation's name, not the account's.
            var expected = c.Targets.Count == 1
                ? new[] { c.Targets[0].DisplayName, c.Targets[0].Hostname }.Where(x => !string.IsNullOrEmpty(x)).ToArray()
                : [c.Targets.Count.ToString()];
            if (!expected.Any(x => string.Equals(x, typed, StringComparison.OrdinalIgnoreCase)))
                return await DeniedAsync(actor, "confirm", r.ActionCode, r.EndpointIds, r.Parameters, Result<ConfirmResponse>.Fail(ErrorKind.Validation,
                    c.Targets.Count == 1 ? "Type the workstation name to confirm." : $"Type the number of workstations ({c.Targets.Count}) to confirm."));
        }
        if (!c.Def.Implemented)
            return await DeniedAsync(actor, "confirm", r.ActionCode, r.EndpointIds, r.Parameters, Result<ConfirmResponse>.Fail(ErrorKind.NotImplemented, $"'{c.Def.DisplayName}' is not available yet."));
        return Result<ConfirmResponse>.Success(tokens.Issue(
            actor.Sid, c.Def.Code, TargetsHash.Of(c.Targets.Select(t => t.Id)), BindingHash(c), c.Override, clock.UtcNow));
    }

    public async Task<Result<JobDetail>> CreateAsync(JobActor actor, CreateJobRequest r, CancellationToken ct = default)
    {
        var check = await CheckAsync(actor, r.ActionCode, r.EndpointIds, r.Parameters, r.Reason, r.OverrideProtected, ct);
        if (!check.Ok) return await DeniedAsync(actor, "create", r.ActionCode, r.EndpointIds, r.Parameters, Result<JobDetail>.Fail(check.Kind, check.Message!));
        var c = check.Value!;

        // The checks that can still fail run BEFORE the single-use token is spent, so a refused request does not burn the confirmation.
        if (c.Def.Heavy)
        {
            var busy = await BusyHeavyTargetsAsync(c.Targets.Select(t => t.Id).ToList(), ct);
            if (busy.Count > 0)
                return Result<JobDetail>.Fail(ErrorKind.Conflict,
                    $"A heavy job (sfc, DISM, chkdsk or updates) is already running on: {string.Join(", ", busy.Take(5))}. Wait for it or cancel it.");
        }
        if (ActionDefinition.NeedsConfirmationAt(c.Effective) && !tokens.Consume(r.ConfirmToken, actor.Sid, c.Def.Code,
                TargetsHash.Of(c.Targets.Select(t => t.Id)), BindingHash(c), c.Override, clock.UtcNow))
            return await DeniedAsync(actor, "create", r.ActionCode, r.EndpointIds, r.Parameters,
                Result<JobDetail>.Fail(ErrorKind.Validation, "Confirmation is missing, expired or does not match this request. Confirm again."));
        if (!c.Def.Implemented)
            return await DeniedAsync(actor, "create", r.ActionCode, r.EndpointIds, r.Parameters, Result<JobDetail>.Fail(ErrorKind.NotImplemented, $"'{c.Def.DisplayName}' is not available yet."));

        var now = clock.UtcNow;
        var job = new Job
        {
            Id = Guid.NewGuid(), ActionCode = c.Def.Code, ParametersJson = JsonSerializer.Serialize(JobParameters(c)),
            InitiatorSid = actor.Sid, InitiatorAccount = actor.Actor.Account, CreatedUtc = now, Status = JobState.Queued,
            Reason = c.Reason, ProtectedOverride = c.Override, ClientIp = actor.Actor.ClientIp, TargetCount = c.Targets.Count,
        };
        db.Jobs.Add(job);
        foreach (var t in c.Targets)
            db.JobTargets.Add(new JobTarget { Id = Guid.NewGuid(), JobId = job.Id, EndpointId = t.Id, EndpointName = t.DisplayName ?? t.Hostname, Host = t.Hostname });

        audit.Add(actor.Actor, "job.create", "Job", job.Id.ToString(), null, c.Def.Code,
            new { action = c.Def.Code, risk = c.Def.Risk.ToString(), effectiveRisk = c.Effective.ToString(), targets = c.Targets.Select(t => t.DisplayName ?? t.Hostname).Take(100), count = c.Targets.Count,
                  parameters = c.Parameters, reason = c.Reason, protectedOverride = c.Override,
                  profile = ProfileAudit(c.Profile, c.StoredSize) });
        await db.SaveChangesAsync(ct);

        signal.Poke();
        var detail = await LoadDetailAsync(job.Id, ct);
        await events.JobChangedAsync(detail!.Job);
        return Result<JobDetail>.Success(detail);
    }

    private async Task<List<string>> BusyHeavyTargetsAsync(List<Guid> endpointIds, CancellationToken ct)
    {
        var heavyCodes = ActionCatalog.All.Where(a => a.Heavy).Select(a => a.Code).ToList();
        var active = await db.JobTargets.AsNoTracking()
            .Where(t => endpointIds.Contains(t.EndpointId) && (t.Status == TargetState.Queued || t.Status == TargetState.Running))
            .Select(t => new { t.JobId, t.EndpointName }).ToListAsync(ct);
        if (active.Count == 0) return [];
        var jobIds = active.Select(a => a.JobId).Distinct().ToList();
        var heavyJobs = (await db.Jobs.AsNoTracking().Where(j => jobIds.Contains(j.Id) && heavyCodes.Contains(j.ActionCode)).Select(j => j.Id).ToListAsync(ct)).ToHashSet();
        return active.Where(a => heavyJobs.Contains(a.JobId)).Select(a => a.EndpointName).Distinct().ToList();
    }

    // ------------------------------------------------------------------ queries

    public async Task<PagedResult<JobSummary>> ListAsync(JobActor actor, JobListQuery q, CancellationToken ct = default)
    {
        IQueryable<Job> jobs = db.Jobs.AsNoTracking();
        if (!actor.IsAdmin) jobs = jobs.Where(j => j.InitiatorSid == actor.Sid); // Viewers and Operators see their own jobs only
        if (Enum.TryParse<JobState>(q.Status, true, out var st) && !int.TryParse(q.Status, out _)) jobs = jobs.Where(j => j.Status == st);
        if (!string.IsNullOrWhiteSpace(q.Action)) jobs = jobs.Where(j => j.ActionCode.StartsWith(q.Action));
        if (actor.IsAdmin && !string.IsNullOrWhiteSpace(q.Operator))
        {
            var o = q.Operator.Trim().ToLower();
            jobs = jobs.Where(j => j.InitiatorAccount.ToLower().Contains(o) || j.InitiatorSid == q.Operator);
        }
        if (q.EndpointId is { } ep) jobs = jobs.Where(j => db.JobTargets.Any(t => t.JobId == j.Id && t.EndpointId == ep));
        if (q.FromUtc is { } from) jobs = jobs.Where(j => j.CreatedUtc >= from);
        if (q.ToUtc is { } to) jobs = jobs.Where(j => j.CreatedUtc <= to);

        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);
        var total = await jobs.CountAsync(ct);
        var rows = await jobs.OrderByDescending(j => j.CreatedUtc).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(j => j.Id).ToList();
        var targets = (await db.JobTargets.AsNoTracking().Where(t => ids.Contains(t.JobId)).Select(t => new { t.JobId, t.Status }).ToListAsync(ct))
            .GroupBy(t => t.JobId).ToDictionary(g => g.Key, g => g.Select(x => x.Status).ToList());
        return new PagedResult<JobSummary>(rows.Select(j => Summarize(j, targets.GetValueOrDefault(j.Id) ?? [])).ToList(), total, page, size);
    }

    public async Task<Result<JobDetail>> GetAsync(JobActor actor, Guid id, CancellationToken ct = default)
    {
        var detail = await LoadDetailAsync(id, ct);
        // Someone else's job is reported as not found, not as forbidden.
        if (detail is null || (!actor.IsAdmin && !SameSid(detail.Job.InitiatorSid, actor.Sid))) return Result<JobDetail>.Fail(ErrorKind.NotFound, "Job not found.");
        return Result<JobDetail>.Success(detail);
    }

    public async Task<Result<IReadOnlyList<LogLine>>> LogAsync(JobActor actor, Guid jobId, Guid targetId, int afterSeq, int take, CancellationToken ct = default)
    {
        var visible = await GetAsync(actor, jobId, ct);
        if (!visible.Ok || visible.Value!.Targets.All(t => t.Id != targetId))
            return Result<IReadOnlyList<LogLine>>.Fail(ErrorKind.NotFound, "Not found.");
        var rows = await db.JobLogChunks.AsNoTracking().Where(c => c.JobTargetId == targetId && c.Seq > afterSeq)
            .OrderBy(c => c.Seq).Take(Math.Clamp(take, 1, 2000)).ToListAsync(ct);
        return Result<IReadOnlyList<LogLine>>.Success(rows.Select(c => new LogLine(c.Seq, c.TimestampUtc, c.Stream, c.Text)).ToList());
    }

    public async Task<Result<string>> OutputTextAsync(JobActor actor, Guid jobId, Guid targetId, CancellationToken ct = default)
    {
        var visible = await GetAsync(actor, jobId, ct);
        var target = visible.Value?.Targets.FirstOrDefault(t => t.Id == targetId);
        if (!visible.Ok || target is null) return Result<string>.Fail(ErrorKind.NotFound, "Not found.");
        var lines = await db.JobLogChunks.AsNoTracking().Where(c => c.JobTargetId == targetId).OrderBy(c => c.Seq).Select(c => c.Text).ToListAsync(ct);
        var header = $"# {visible.Value!.Job.ActionName} on {target.EndpointName} ({target.Host})\r\n# job {jobId} target {targetId}\r\n# started by {visible.Value.Job.InitiatorAccount}\r\n\r\n";
        return Result<string>.Success(header + string.Join("\r\n", lines) + "\r\n");
    }

    // ------------------------------------------------------------------ cancel

    public async Task<Result> CancelAsync(JobActor actor, Guid jobId, CancellationToken ct = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || (!actor.IsAdmin && !SameSid(job.InitiatorSid, actor.Sid))) return Result.Fail(ErrorKind.NotFound, "Job not found.");
        if (job.Status is JobState.Succeeded or JobState.Warning or JobState.PartiallyFailed or JobState.Failed or JobState.Cancelled)
            return Result.Fail(ErrorKind.Conflict, "The job already finished.");
        if (job.CancelRequestedUtc is not null) return Result.Success();

        var def = ActionCatalog.Find(job.ActionCode);
        job.CancelRequestedUtc = clock.UtcNow;
        audit.Add(actor.Actor, "job.cancel", "Job", job.Id.ToString(), null, job.ActionCode, new { supportsCancel = def?.SupportsCancel });
        await db.SaveChangesAsync(ct);
        signal.Poke();
        return Result.Success();
    }

    // ------------------------------------------------------------------ reports from the executor

    public async Task<Result> StartedAsync(Guid targetId, string? reporterSid)
    {
        var (t, j, err) = await LoadForReportAsync(targetId, reporterSid);
        if (err is not null) return err;
        if (t!.Status == TargetState.Queued || t.Status == TargetState.Running)
        {
            t.Status = TargetState.Running;
            t.StartedUtc ??= clock.UtcNow;
            await db.SaveChangesAsync();
            await PublishAsync(j!, t);
        }
        return Result.Success();
    }

    public async Task<Result> LogAsync(Guid targetId, string stream, IReadOnlyList<string> lines, string? reporterSid)
    {
        var (t, j, err) = await LoadForReportAsync(targetId, reporterSid);
        if (err is not null) return err;
        if (IsFinal(t!.Status) || lines.Count == 0) return Result.Success();

        var now = clock.UtcNow;
        var pushed = new List<LogLine>();
        var safeStream = stream is "stdout" or "stderr" or "system" ? stream : "stdout";
        foreach (var raw in lines)
        {
            if (t.LogLines >= MaxLinesPerTarget) break;
            var text = OutputRedactor.Redact(raw.Length > MaxLineLength ? raw[..MaxLineLength] + " [line truncated]" : raw);
            t.LogLines++;
            if (t.LogLines == MaxLinesPerTarget) text = "[output limit reached; further lines are dropped]";
            db.JobLogChunks.Add(new JobLogChunk { JobTargetId = t.Id, Seq = t.LogLines, TimestampUtc = now, Stream = safeStream, Text = text });
            pushed.Add(new LogLine(t.LogLines, now, safeStream, text));
        }
        await db.SaveChangesAsync();
        await events.LogAppendedAsync(j!.Id, t.Id, j.InitiatorSid, pushed);
        return Result.Success();
    }

    public async Task<Result> ProgressAsync(Guid targetId, int percent, string? reporterSid)
    {
        var (t, j, err) = await LoadForReportAsync(targetId, reporterSid);
        if (err is not null) return err;
        if (IsFinal(t!.Status)) return Result.Success();
        t.Progress = Math.Clamp(percent, 0, 100);
        await db.SaveChangesAsync();
        await events.TargetChangedAsync(ToInfo(t), j!.InitiatorSid);
        return Result.Success();
    }

    public async Task<Result> CompletedAsync(Guid targetId, TargetState state, int? exitCode, string? summary, string? reporterSid)
    {
        var (t, j, err) = await LoadForReportAsync(targetId, reporterSid);
        if (err is not null) return err;
        if (IsFinal(t!.Status)) return Result.Success(); // first final state wins (for example a timeout beats a late report)
        if (!IsFinal(state)) return Result.Fail(ErrorKind.Validation, "Not a final state.");

        var def = ActionCatalog.Find(j!.ActionCode);
        if (exitCode is { } code && def is not null && state == TargetState.Succeeded)
        {
            // The action's exit-code map has the last word on a plain "completed".
            var (mapped, mappedSummary) = def.MapExit(code);
            state = mapped;
            summary ??= mappedSummary;
        }

        var now = clock.UtcNow;
        t.Status = state;
        t.FinishedUtc = now;
        t.ExitCode = exitCode;
        t.ResultSummary = summary is { Length: > 1000 } ? summary[..1000] : summary;
        if (state is TargetState.Succeeded or TargetState.Warning) t.Progress = 100;

        var text = string.Join("\n", await db.JobLogChunks.AsNoTracking().Where(c => c.JobTargetId == t.Id).OrderBy(c => c.Seq).Select(c => c.Text).ToListAsync());
        t.OutputSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        audit.Add(new Actor(j.InitiatorSid, j.InitiatorAccount, null), "job.target", "Endpoint", t.Host, null, state.ToString(),
            JobTargetAudit(j, t, exitCode, now),
            success: state is TargetState.Succeeded or TargetState.Warning);
        await db.SaveChangesAsync();
        // Stored before the job-finished push, so a screen that refreshes on that push already sees the new size.
        if (j.ActionCode == "profile.sizes" && state is TargetState.Succeeded or TargetState.Warning) await StoreProfileSizesAsync(j, t, text, now);
        // A diagnosis is evaluated and stored before the job-finished push, so the screen that opens the run on that push finds it.
        if (j.ActionCode == Diagnostics.DiagnosticJob.ActionCode) await diagnosis.OnTargetFinishedAsync(j, t, state);

        await RefreshJobStateAsync(j);
        await events.TargetChangedAsync(ToInfo(t), j.InitiatorSid);
        signal.Poke();
        return Result.Success();
    }

    // ------------------------------------------------------------------ shared helpers (also used by the orchestrator)

    /// <summary>Recomputes the job's overall state from its targets and publishes the change. Returns true when the job just finished.</summary>
    public async Task<bool> RefreshJobStateAsync(Job job)
    {
        var states = await db.JobTargets.Where(t => t.JobId == job.Id).Select(t => t.Status).ToListAsync();
        var next = ComputeState(job, states);
        var finished = false;
        if (next != job.Status)
        {
            job.Status = next;
            if (next is JobState.Succeeded or JobState.Warning or JobState.PartiallyFailed or JobState.Failed or JobState.Cancelled)
            {
                job.FinishedUtc = clock.UtcNow;
                finished = true;
            }
            await db.SaveChangesAsync();
        }
        var summary = Summarize(job, states);
        await events.JobChangedAsync(summary);
        if (finished) await events.JobFinishedAsync(summary);
        return finished;
    }

    public static JobState ComputeState(Job job, IReadOnlyList<TargetState> targets)
    {
        if (targets.Count == 0) return job.Status;
        if (targets.Any(s => !IsFinal(s)))
            return targets.Any(s => s == TargetState.Running) ? JobState.Running
                : job.Status == JobState.WaitingForHelper ? JobState.WaitingForHelper : job.Status is JobState.Running ? JobState.Running : JobState.Queued;

        if (job.CancelRequestedUtc is not null && targets.Any(s => s == TargetState.Cancelled)) return JobState.Cancelled;
        var worked = targets.Where(s => s != TargetState.Skipped && s != TargetState.Cancelled).ToList();
        if (worked.Count == 0) return JobState.Failed;
        var bad = worked.Count(s => s is TargetState.Failed or TargetState.TimedOut);
        if (bad == worked.Count) return JobState.Failed;
        if (bad > 0) return JobState.PartiallyFailed;
        return worked.Any(s => s == TargetState.Warning) ? JobState.Warning : JobState.Succeeded;
    }

    public static bool IsFinal(TargetState s) => Final.Contains(s);

    public static JobSummary Summarize(Job j, IEnumerable<TargetState> targets)
    {
        var list = targets.ToList();
        int N(TargetState s) => list.Count(x => x == s);
        var def = ActionCatalog.Find(j.ActionCode);
        return new JobSummary(j.Id, j.ActionCode, def?.DisplayName ?? j.ActionCode, j.InitiatorSid, j.InitiatorAccount, j.Status.ToString(),
            j.CreatedUtc, j.FinishedUtc, j.Reason, j.TargetCount,
            N(TargetState.Queued), N(TargetState.Running), N(TargetState.Succeeded), N(TargetState.Warning), N(TargetState.Failed),
            N(TargetState.Cancelled), N(TargetState.TimedOut), N(TargetState.Skipped), j.ProtectedOverride, (def?.Risk ?? RiskLevel.Low).ToString());
    }

    public static JobTargetInfo ToInfo(JobTarget t) =>
        new(t.Id, t.JobId, t.EndpointId, t.EndpointName, t.Host, t.Status.ToString(), t.StartedUtc, t.FinishedUtc,
            t.StartedUtc is { } s ? ((t.FinishedUtc ?? DateTime.UtcNow) - s).TotalSeconds : null,
            t.ExitCode, t.ResultSummary, t.SkipReason, t.Progress, t.LogLines, t.OutputPath is not null, t.OutputSha256);

    private async Task<JobDetail?> LoadDetailAsync(Guid id, CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return null;
        var targets = await db.JobTargets.AsNoTracking().Where(t => t.JobId == id).OrderBy(t => t.EndpointName).ToListAsync(ct);
        return new JobDetail(Summarize(job, targets.Select(t => t.Status)), job.ParametersJson, targets.Select(ToInfo).ToList());
    }

    private async Task PublishAsync(Job job, JobTarget target)
    {
        await events.TargetChangedAsync(ToInfo(target), job.InitiatorSid);
        var states = await db.JobTargets.AsNoTracking().Where(x => x.JobId == job.Id).Select(x => x.Status).ToListAsync();
        if (job.Status is JobState.Queued or JobState.WaitingForHelper) { job.Status = JobState.Running; await db.SaveChangesAsync(); }
        await events.JobChangedAsync(Summarize(job, states));
    }

    private async Task<(JobTarget? Target, Job? Job, Result? Error)> LoadForReportAsync(Guid targetId, string? reporterSid)
    {
        var t = await db.JobTargets.FirstOrDefaultAsync(x => x.Id == targetId);
        if (t is null) return (null, null, Result.Fail(ErrorKind.NotFound, "Unknown target."));
        var j = await db.Jobs.FirstAsync(x => x.Id == t.JobId);
        if (reporterSid is not null && !SameSid(j.InitiatorSid, reporterSid))
            return (null, null, Result.Fail(ErrorKind.Forbidden, "Only the initiating operator's helper may report on this job."));
        return (t, j, null);
    }

    private static bool SameSid(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
