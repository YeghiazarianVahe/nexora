using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Contracts;
using Nexora.Domain;

namespace Nexora.Application.Services;

// ---------- Services and dependents: read-only views and the guard that protects critical services ----------
// Reads use the agentless WMI/CIM path with the SERVER's identity and change nothing. Every start, stop and restart is a catalog job that runs
// in the operator's own helper; the rules here only decide what the UI may offer and what the server refuses.

public static partial class ServiceNameRules
{
    public const int MaxProtected = 200;

    [GeneratedRegex(@"^[A-Za-z0-9_.\-$ ]{1,256}\z")]
    private static partial Regex Pattern();

    /// <summary>The same shape the catalog's serviceName parameter and the helper accept. Anchored with \z so a trailing newline never passes.</summary>
    public static bool IsValid(string? name) => name is not null && Pattern().IsMatch(name);
}

/// <summary>What the Services view and the dependents lookup return for one service.</summary>
/// <param name="Protected">On the fixed floor or in the Admin list: it cannot be stopped or restarted from Nexora.</param>
/// <param name="Allowed">Actions the server would accept right now: "start", "stop", "restart". Computed server-side from state, start mode and protection.</param>
public sealed record ServiceRowDto(string Name, string DisplayName, string State, string StartMode, bool Protected, IReadOnlyList<string> Allowed);

/// <param name="EffectiveRisk">Risk of a stop or restart of this service as the server will apply it: Medium, or High when something running depends on it.</param>
public sealed record DependentsDto(IReadOnlyList<string> Running, IReadOnlyList<string> All, bool AnyProtected, string EffectiveRisk);

/// <summary>One service that depends (directly or not) on another, as read from WMI.</summary>
public sealed record DependentService(string Name, string State);

/// <summary>Either the rows or the reason the host could not be read (never an exception text).</summary>
/// <param name="LastKnown">When the host cannot be read: the last services read in this server process (actions switched off), or null.</param>
public sealed record ServiceListOutcome(IReadOnlyList<ServiceRowDto>? Rows, string? UnavailableReason, IReadOnlyList<ServiceRowDto>? LastKnown = null, DateTime? LastKnownUtc = null);

public sealed record DependentsOutcome(DependentsDto? Dependents, string? UnavailableReason);

public static class ServiceRules
{
    public const string Start = "start", Stop = "stop", Restart = "restart";

    public static bool IsServiceAction(string? actionCode) => actionCode is "svc.start" or "svc.stop" or "svc.restart";

    /// <summary>
    /// The Services view sends the service as "name"; the catalog and the helper call it "serviceName". "name" is accepted as an alias for the three
    /// service actions only when "serviceName" is absent (both together are left as they are, so the validator rejects the unknown one).
    /// </summary>
    public static Dictionary<string, System.Text.Json.JsonElement>? WithNameAlias(string actionCode, Dictionary<string, System.Text.Json.JsonElement>? parameters)
    {
        if (parameters is null || !IsServiceAction(actionCode) || parameters.ContainsKey("serviceName") || !parameters.TryGetValue("name", out var name)) return parameters;
        var copy = new Dictionary<string, System.Text.Json.JsonElement>(parameters, StringComparer.Ordinal);
        copy.Remove("name");
        copy["serviceName"] = name;
        return copy;
    }

    /// <summary>Risk of stopping or restarting a service as the Services view shows it: Medium, or High when something running depends on it. Same rule as the risk escalator.</summary>
    public static string StopRisk(bool anyRunningDependents) => anyRunningDependents ? "High" : "Medium";

    public static bool IsStopOrRestart(string? actionCode) => actionCode is "svc.stop" or "svc.restart";

    /// <summary>WMI says "Start Pending"; the API says "StartPending". Unknown strings become "Unknown", never a guess.</summary>
    public static string NormalizeState(string? state) => (state ?? "").Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "running" => "Running",
        "stopped" => "Stopped",
        "startpending" => "StartPending",
        "stoppending" => "StopPending",
        "paused" => "Paused",
        "continuepending" => "ContinuePending",
        "pausepending" => "PausePending",
        _ => "Unknown",
    };

    public static string NormalizeStartMode(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "auto" => "Auto",
        "manual" => "Manual",
        "disabled" => "Disabled",
        "boot" => "Boot",
        "system" => "System",
        _ => "Unknown",
    };

    /// <summary>Anything that is up or on its way up or down counts as running for dependents: stopping its parent would disturb it.</summary>
    public static bool IsActive(string? state) => NormalizeState(state) is "Running" or "StartPending" or "StopPending" or "Paused" or "ContinuePending" or "PausePending";

    /// <summary>
    /// What may be offered for a service. Start: stopped and not disabled (also for protected services: starting one is remediation). Stop: running, paused or starting, and not protected. Restart: running and not protected.
    /// </summary>
    public static IReadOnlyList<string> Allowed(string state, string startMode, bool isProtected)
    {
        var allowed = new List<string>(3);
        if (state == "Stopped" && startMode != "Disabled") allowed.Add(Start);
        if (!isProtected)
        {
            if (state is "Running" or "Paused" or "StartPending") allowed.Add(Stop);
            if (state == "Running") allowed.Add(Restart);
        }
        return allowed;
    }
}

// ---------- the guard ----------

public interface IServiceGuard
{
    /// <summary>True when the name is on the fixed floor or in the Admin list (trimmed, case-insensitive).</summary>
    Task<bool> IsProtectedAsync(string? name, CancellationToken ct = default);
    bool IsProtected(string? name, PlatformSettings settings);

    /// <summary>
    /// For a stop or restart with "stop dependents": null when it may go ahead, otherwise why not. Refuses when a dependent is protected, when the
    /// dependents cannot be read (fail safe), and when more than <see cref="ServiceGuard.MaxDependentTargets"/> workstations are involved.
    /// </summary>
    Task<string?> DependentsRefusalAsync(IReadOnlyList<ManagedEndpoint> targets, string serviceName, CancellationToken ct = default);
}

public sealed class ServiceGuard(ISettingsService settings, IServiceReader reader) : IServiceGuard
{
    public const int MaxDependentTargets = 5;
    public static readonly TimeSpan OverallDeadline = TimeSpan.FromSeconds(20);

    public bool IsProtected(string? name, PlatformSettings s)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) return false;
        return ProtectedNames.IsProtectedService(n) || (s.ProtectedServices ?? []).Any(p => string.Equals(p?.Trim(), n, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> IsProtectedAsync(string? name, CancellationToken ct = default) =>
        IsProtected(name, await settings.GetAsync(ct));

    public async Task<string?> DependentsRefusalAsync(IReadOnlyList<ManagedEndpoint> targets, string serviceName, CancellationToken ct = default)
    {
        if (targets.Count > MaxDependentTargets)
            return $"'Stop dependents' can be used on up to {MaxDependentTargets} workstations at a time.";
        var s = await settings.GetAsync(ct);
        foreach (var t in targets.Where(t => !t.Online))
            return $"{t.DisplayName ?? t.Hostname} is offline, so the dependent services of '{serviceName}' cannot be checked; 'stop dependents' is not allowed.";

        // All targets are read in parallel under one overall deadline, so a single confirm can never wait for them one after the other.
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(OverallDeadline);
        ServiceRead<IReadOnlyList<DependentService>>[] reads;
        try { reads = await Task.WhenAll(targets.Select(t => reader.ReadDependentsAsync(t.Hostname, serviceName, ServiceReader.DefaultMaxAge, overall.Token))); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"The dependent services of '{serviceName}' could not be checked in time, so 'stop dependents' is not allowed right now.";
        }
        for (var i = 0; i < targets.Count; i++)
        {
            var name = targets[i].DisplayName ?? targets[i].Hostname;
            if (!reads[i].Ok) return $"The dependent services of '{serviceName}' on {name} could not be checked, so 'stop dependents' is not allowed right now.";
            var bad = reads[i].Value!.Where(d => IsProtected(d.Name, s)).Select(d => d.Name).Take(5).ToList();
            if (bad.Count > 0) return $"'{serviceName}' on {name} has protected dependent service(s) ({string.Join(", ", bad)}); they cannot be stopped.";
        }
        return null;
    }
}

// ---------- the WMI read port and its bounded, cached reader ----------

public interface IServiceCollector
{
    /// <summary>Win32_Service of the host. Read-only; throws when the host cannot be reached.</summary>
    Task<IReadOnlyList<ServiceInfo>> CollectServicesAsync(string host, CancellationToken ct);

    /// <summary>Every service that depends on <paramref name="serviceName"/>, directly or not, stopped ones included. Read-only; throws on failure or when the tree is too deep.</summary>
    Task<IReadOnlyList<DependentService>> CollectDependentsAsync(string host, string serviceName, CancellationToken ct);
}

public sealed record ServiceRead<T>(T? Value, string? Reason) where T : class
{
    public bool Ok => Value is not null;
}

public interface IServiceReader
{
    Task<ServiceRead<IReadOnlyList<ServiceInfo>>> ReadServicesAsync(string host, TimeSpan maxAge, CancellationToken ct);
    Task<ServiceRead<IReadOnlyList<DependentService>>> ReadDependentsAsync(string host, string serviceName, TimeSpan maxAge, CancellationToken ct);
    /// <summary>The services of the last successful read of this host in this server process (for showing an offline workstation), or null.</summary>
    (IReadOnlyList<ServiceInfo> Services, DateTime AtUtc)? LastKnownServices(string host);
}

/// <summary>
/// The only way the server reads services and dependents. Bounded: at most <see cref="MaxConcurrent"/> reads run at once (a slot is held until the
/// WMI call really ends, even when the caller stopped waiting), every read has a deadline, a small cache serves repeats, and a failed host is
/// remembered briefly so a refresh loop cannot hammer it. A failure is a typed result with a generic reason, never an exception. Singleton.
/// </summary>
public sealed class ServiceReader(IServiceCollector collector, IOptions<NexoraOptions> options, ISystemClock clock, ILogger<ServiceReader> logger) : IServiceReader
{
    public const int MaxConcurrent = 4, MaxCacheEntries = 64;
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(15);
    public const string ReadFailed = "The services could not be read from the workstation right now.";
    public const string Busy = "Too many service reads are running. Try again in a moment.";
    private static readonly TimeSpan FailureMemory = TimeSpan.FromSeconds(5);

    private sealed record Entry(DateTime AtUtc, object? Value, string? Reason);

    private readonly SemaphoreSlim _gate = new(MaxConcurrent, MaxConcurrent);
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    private readonly Dictionary<string, (IReadOnlyList<ServiceInfo> Services, DateTime AtUtc)> _lastGood = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ServiceRead<IReadOnlyList<ServiceInfo>>> ReadServicesAsync(string host, TimeSpan maxAge, CancellationToken ct)
    {
        var read = await RunAsync("s|" + host, maxAge, c => collector.CollectServicesAsync(host, c), ct);
        if (read.Ok)
            lock (_lock)
            {
                if (!(_lastGood.TryGetValue(host, out var known) && ReferenceEquals(known.Services, read.Value)))
                {
                    _lastGood[host] = (read.Value!, clock.UtcNow);
                    if (_lastGood.Count > MaxCacheEntries)
                        foreach (var k in _lastGood.OrderBy(kv => kv.Value.AtUtc).Take(_lastGood.Count - MaxCacheEntries).Select(kv => kv.Key).ToList()) _lastGood.Remove(k);
                }
            }
        return read;
    }

    public (IReadOnlyList<ServiceInfo> Services, DateTime AtUtc)? LastKnownServices(string host)
    {
        lock (_lock) return _lastGood.TryGetValue(host, out var known) ? known : null;
    }

    public Task<ServiceRead<IReadOnlyList<DependentService>>> ReadDependentsAsync(string host, string serviceName, TimeSpan maxAge, CancellationToken ct) =>
        ServiceNameRules.IsValid(serviceName)
            ? RunAsync("d|" + host + "|" + serviceName.Trim(), maxAge, c => collector.CollectDependentsAsync(host, serviceName.Trim(), c), ct)
            : Task.FromResult(new ServiceRead<IReadOnlyList<DependentService>>(null, "Invalid service name."));

    private readonly Dictionary<string, Task> _inflight = new(StringComparer.OrdinalIgnoreCase);

    private async Task<ServiceRead<IReadOnlyList<T>>> RunAsync<T>(string key, TimeSpan maxAge, Func<CancellationToken, Task<IReadOnlyList<T>>> read, CancellationToken ct)
    {
        Task<ServiceRead<IReadOnlyList<T>>> task;
        TaskCompletionSource<ServiceRead<IReadOnlyList<T>>>? mine = null;
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                var age = clock.UtcNow - hit.AtUtc;
                if (hit.Reason is null ? age <= maxAge : age <= FailureMemory)
                    return new ServiceRead<IReadOnlyList<T>>(hit.Value as IReadOnlyList<T>, hit.Reason);
            }
            // Identical reads in flight are merged: they share one WMI call. Only a placeholder is created under the lock.
            if (_inflight.TryGetValue(key, out var running)) task = (Task<ServiceRead<IReadOnlyList<T>>>)running;
            else
            {
                mine = new TaskCompletionSource<ServiceRead<IReadOnlyList<T>>>(TaskCreationOptions.RunContinuationsAsynchronously);
                task = mine.Task;
                _inflight[key] = task;
            }
        }
        if (mine is not null) _ = RunSharedAsync(key, read, mine);   // detached: the owner caller can leave without cancelling the shared read
        return await task.WaitAsync(ct);   // a caller that leaves throws; the shared read carries on under its own deadline
    }

    private async Task RunSharedAsync<T>(string key, Func<CancellationToken, Task<IReadOnlyList<T>>> read, TaskCompletionSource<ServiceRead<IReadOnlyList<T>>> done)
    {
        try { done.SetResult(await FetchAsync(key, read)); }
        catch (Exception ex) { done.SetException(ex); }   // FetchAsync reports failures as results; this is only a safety net
        finally { lock (_lock) _inflight.Remove(key); }
    }

    private async Task<ServiceRead<IReadOnlyList<T>>> FetchAsync<T>(string key, Func<CancellationToken, Task<IReadOnlyList<T>>> read)
    {
        var seconds = Math.Clamp(options.Value.Monitoring.ServiceReadTimeoutSeconds, 2, 120);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var holdingSlot = false;
        try
        {
            if (!await _gate.WaitAsync(TimeSpan.FromSeconds(Math.Min(5, seconds)), deadline.Token)) return new(null, Busy);
            holdingSlot = true;
            var work = read(deadline.Token);
            holdingSlot = false;
            // The slot is freed when the WMI call really ends, not when this read stops waiting; the failure of an abandoned call is observed here.
            _ = work.ContinueWith(t => { _ = t.Exception; _gate.Release(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var value = await work.WaitAsync(deadline.Token);
            Remember(key, new Entry(clock.UtcNow, value, null));
            return new(value, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Service read {Key} failed or timed out", key);
            Remember(key, new Entry(clock.UtcNow, null, ReadFailed));
            return new(null, ReadFailed);
        }
        finally
        {
            if (holdingSlot) _gate.Release();   // read() threw before it returned a task
        }
    }

    private void Remember(string key, Entry entry)
    {
        lock (_lock)
        {
            _cache[key] = entry;
            if (_cache.Count <= MaxCacheEntries) return;
            var oldest = _cache.OrderBy(kv => kv.Value.AtUtc).Take(_cache.Count - MaxCacheEntries).Select(kv => kv.Key).ToList();
            foreach (var k in oldest) _cache.Remove(k);
        }
    }
}

// ---------- the views ----------

public interface IServiceQuery
{
    Task<Result<ServiceListOutcome>> ListAsync(Guid endpointId, CancellationToken ct = default);
    Task<Result<DependentsOutcome>> DependentsAsync(Guid endpointId, string serviceName, CancellationToken ct = default);
}

public sealed class ServiceQuery(INexoraDb db, ISettingsService settings, IServiceGuard guard, IServiceReader reader) : IServiceQuery
{
    public const string Offline = "The workstation is offline.";
    private const int MaxRows = 2_000, MaxNames = 500;
    private static readonly TimeSpan ListMaxAge = TimeSpan.FromSeconds(3);

    public async Task<Result<ServiceListOutcome>> ListAsync(Guid endpointId, CancellationToken ct = default)
    {
        var e = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == endpointId, ct);
        if (e is null) return Result<ServiceListOutcome>.Fail(ErrorKind.NotFound, "Endpoint not found.");
        var s = await settings.GetAsync(ct);

        string? reason = null;
        if (!e.Online) reason = Offline;
        else
        {
            var read = await reader.ReadServicesAsync(e.Hostname, ListMaxAge, ct);
            if (read.Ok) return Result<ServiceListOutcome>.Success(new ServiceListOutcome(Rows(read.Value!, s, withActions: true), null));
            reason = read.Reason ?? ServiceReader.ReadFailed;
        }
        // Unavailable: show what was seen last, with every action switched off.
        return Result<ServiceListOutcome>.Success(reader.LastKnownServices(e.Hostname) is { } known
            ? new ServiceListOutcome(null, reason, Rows(known.Services, s, withActions: false), known.AtUtc)
            : new ServiceListOutcome(null, reason));
    }

    private List<ServiceRowDto> Rows(IReadOnlyList<ServiceInfo> services, PlatformSettings s, bool withActions) =>
        services.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Take(MaxRows).Select(x =>
        {
            var state = ServiceRules.NormalizeState(x.State);
            var mode = ServiceRules.NormalizeStartMode(x.StartMode);
            var isProtected = guard.IsProtected(x.Name, s);
            return new ServiceRowDto(x.Name, string.IsNullOrWhiteSpace(x.DisplayName) ? x.Name : x.DisplayName, state, mode, isProtected,
                withActions ? ServiceRules.Allowed(state, mode, isProtected) : []);
        }).OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<Result<DependentsOutcome>> DependentsAsync(Guid endpointId, string serviceName, CancellationToken ct = default)
    {
        if (!ServiceNameRules.IsValid(serviceName)) return Result<DependentsOutcome>.Fail(ErrorKind.Validation, "Invalid service name.");
        var e = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == endpointId, ct);
        if (e is null) return Result<DependentsOutcome>.Fail(ErrorKind.NotFound, "Endpoint not found.");
        if (!e.Online) return Result<DependentsOutcome>.Success(new DependentsOutcome(null, Offline));

        var read = await reader.ReadDependentsAsync(e.Hostname, serviceName, ServiceReader.DefaultMaxAge, ct);
        if (!read.Ok) return Result<DependentsOutcome>.Success(new DependentsOutcome(null, read.Reason ?? ServiceReader.ReadFailed));

        var s = await settings.GetAsync(ct);
        var all = read.Value!.Where(d => !string.IsNullOrWhiteSpace(d.Name)).DistinctBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Take(MaxNames).ToList();
        return Result<DependentsOutcome>.Success(new DependentsOutcome(new DependentsDto(
            all.Where(d => ServiceRules.IsActive(d.State)).Select(d => d.Name).ToList(),
            all.Select(d => d.Name).ToList(),
            all.Any(d => guard.IsProtected(d.Name, s)),
            ServiceRules.StopRisk(all.Any(d => ServiceRules.IsActive(d.State)))), null));
    }
}
