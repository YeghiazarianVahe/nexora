using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Nexora.Api.Auth;
using Nexora.Application;
using Nexora.Api.Services;
using Nexora.Application.Jobs;
using Nexora.Application.Services;
using Nexora.Contracts;
using Nexora.Domain;

namespace Nexora.Api.Hubs;

public static class HubGroups
{
    public const string Viewers = "viewers";
    public const string SuperAdmins = "superadmins";
    public static string User(string sid) => "user:" + sid.ToUpperInvariant();
    public static string Endpoint(Guid id) => "endpoint:" + id;
    public const string JobsAdmin = "jobs-admin";
    public static string Job(Guid id) => "job:" + id;
}

/// <summary>
/// Every live hub connection by operator SID, so a role change or revocation can drop them at once. A dropped browser reconnects and is given
/// the groups of its NEW role; a dropped helper reconnects and is refused when the role is gone. Nobody keeps live data on a revoked role.
/// </summary>
public sealed class HubConnectionTracker
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, HubCallerContext>> _by = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string? sid, HubCallerContext context)
    {
        if (sid is null) return;
        _by.GetOrAdd(sid, _ => new())[context.ConnectionId] = context;
    }

    public void Remove(string? sid, string connectionId)
    {
        if (sid is not null && _by.TryGetValue(sid, out var set)) { set.TryRemove(connectionId, out _); if (set.IsEmpty) _by.TryRemove(sid, out _); }
    }

    public int Count(string sid) => _by.TryGetValue(sid, out var set) ? set.Count : 0;

    /// <summary>Closes all live connections of this operator; returns how many.</summary>
    public int Abort(string sid)
    {
        if (!_by.TryGetValue(sid, out var set)) return 0;
        var n = 0;
        foreach (var ctx in set.Values) { try { ctx.Abort(); n++; } catch (Exception) { /* already gone */ } }
        return n;
    }
}

/// <summary>Browser hub. Any authenticated user may connect (a waiting user needs the approval push); data groups need a role.</summary>
[Authorize(Policy = Policies.Authenticated)]
public sealed class NexoraHub(HubConnectionTracker tracker) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var user = Context.User!;
        var sid = user.GetSid();
        Context.Items["sid"] = sid;
        tracker.Add(sid, Context);
        if (sid is not null) await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.User(sid));
        if (user.HasNexoraRole(NexoraRoles.Viewer, NexoraRoles.Operator, NexoraRoles.Admin, NexoraRoles.SuperAdmin))
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Viewers);
        if (user.IsInRole(NexoraRoles.SuperAdmin))
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.SuperAdmins);
        if (user.HasNexoraRole(NexoraRoles.Admin, NexoraRoles.SuperAdmin))
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.JobsAdmin);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        tracker.Remove(Context.Items.TryGetValue("sid", out var sid) ? sid as string : null, Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Live output and per-target changes of one job. Only the initiator or an Admin may subscribe.</summary>
    [Authorize(Policy = Policies.Viewer)]
    public async Task<bool> SubscribeJob(Guid jobId, [FromServices] IJobService jobs)
    {
        var visible = await jobs.GetAsync(Context.User!.ToJobActor(), jobId);
        if (!visible.Ok) return false;
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Job(jobId));
        return true;
    }

    [Authorize(Policy = Policies.Viewer)]
    public Task UnsubscribeJob(Guid jobId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.Job(jobId));

    [Authorize(Policy = Policies.Viewer)]
    public Task SubscribeEndpoint(Guid endpointId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Endpoint(endpointId));

    [Authorize(Policy = Policies.Viewer)]
    public Task UnsubscribeEndpoint(Guid endpointId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.Endpoint(endpointId));
}

/// <summary>
/// Pushes to the browsers. Endpoint updates and dashboard changes are coalesced: one message a second at most, carrying the LATEST state of every
/// endpoint that changed in that second as a list (a cycle over hundreds of PCs is one message, not hundreds).
/// </summary>
public sealed class SignalRNotifier : INexoraNotifier, IDisposable
{
    private readonly IHubContext<NexoraHub> _hub;
    private readonly ConcurrentDictionary<Guid, EndpointSummaryDto> _pending = new();
    private int _dashboardDirty;
    private readonly Timer _timer;
    private int _flushing;

    private readonly HubConnectionTracker _tracker;

    public SignalRNotifier(IHubContext<NexoraHub> hub, HubConnectionTracker tracker, TimeSpan? interval = null)
    {
        _hub = hub;
        _tracker = tracker;
        var every = interval ?? TimeSpan.FromSeconds(1);
        _timer = new Timer(_ => _ = FlushAsync(), null, every, every);
    }

    public Task AccessRequestPendingAsync(AccessRequestDto request) =>
        _hub.Clients.Group(HubGroups.SuperAdmins).SendAsync("AccessRequestPending", request);

    public async Task AccessChangedAsync(string sid)
    {
        await _hub.Clients.Group(HubGroups.User(sid)).SendAsync("AccessChanged");
        await _hub.Clients.Group(HubGroups.SuperAdmins).SendAsync("RolesChanged");
        // Immediate effect: close this person's live connections (browser and helper). Their groups were fixed at connect time from the old role;
        // they reconnect with the new one, or are refused when the role is gone. Delayed a moment so the message above is delivered first.
        _ = Task.Run(async () => { await Task.Delay(300); _tracker.Abort(sid); });
    }

    public Task EndpointUpdatedAsync(EndpointSummaryDto endpoint)
    {
        _pending[endpoint.Id] = endpoint; // a newer state of the same endpoint replaces the older one
        return Task.CompletedTask;
    }

    public Task DashboardChangedAsync()
    {
        Interlocked.Exchange(ref _dashboardDirty, 1);
        return Task.CompletedTask;
    }

    /// <summary>Sends what has gathered since the last flush. Public so a test can drive it.</summary>
    public async Task FlushAsync()
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return;
        try
        {
            if (!_pending.IsEmpty)
            {
                var batch = new List<EndpointSummaryDto>();
                foreach (var id in _pending.Keys.ToList())
                    if (_pending.TryRemove(id, out var e)) batch.Add(e);
                foreach (var chunk in batch.Chunk(200))
                    await _hub.Clients.Group(HubGroups.Viewers).SendAsync("EndpointsUpdated", chunk);
            }
            if (Interlocked.Exchange(ref _dashboardDirty, 0) == 1)
                await _hub.Clients.Group(HubGroups.Viewers).SendAsync("DashboardChanged");
        }
        catch (Exception) { /* a push that fails is simply superseded by the next one */ }
        finally { Interlocked.Exchange(ref _flushing, 0); }
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>Which operator has a helper connected, keyed by the operator's SID.</summary>
public sealed class HelperRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> _connections =
        new(StringComparer.OrdinalIgnoreCase);

    public void Add(string sid, string connectionId) =>
        _connections.GetOrAdd(sid, _ => new()).TryAdd(connectionId, DateTime.UtcNow);

    public void Remove(string sid, string connectionId)
    {
        if (_connections.TryGetValue(sid, out var set))
        {
            set.TryRemove(connectionId, out _);
            if (set.IsEmpty) _connections.TryRemove(sid, out _);
        }
    }

    /// <summary>The operator's most recently connected helper, if any.</summary>
    public string? Find(string sid) =>
        _connections.TryGetValue(sid, out var set) && !set.IsEmpty
            ? set.OrderByDescending(kv => kv.Value).First().Key
            : null;
}

/// <summary>
/// Hub for Nexora.Helper, which runs in an operator's own Windows session and authenticates with that
/// operator's Windows identity. The server only ever dispatches a job to the helper whose SID equals the job's operator.
/// </summary>
[Authorize(Policy = Policies.Operator)]
public sealed class HelperHub(
    HelperRegistry registry, IJobReports jobReports, HelperDiagnostics diagnostics, HubConnectionTracker tracker, IHubContext<NexoraHub> browsers, ILogger<HelperHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var sid = Context.User!.GetSid()!;
        // Captured now: the Windows identity is disposed by the time OnDisconnectedAsync runs, and reading it then throws.
        var account = Context.User!.GetAccount();
        Context.Items["sid"] = sid;
        Context.Items["account"] = account;
        tracker.Add(sid, Context);
        registry.Add(sid, Context.ConnectionId);
        diagnostics.Connected(sid);
        // Which PC: what the connecting address resolves to (trusted), else the name the helper reports (shown as unverified).
        var remoteIp = Context.GetHttpContext()?.Connection.RemoteIpAddress;
        var resolved = HelperDiagnostics.CleanMachine(await ResolveHostAsync(remoteIp));
        var claimed = HelperDiagnostics.CleanMachine(Context.GetHttpContext()?.Request.Query["machine"]);
        var machine = resolved ?? claimed;
        if (machine is not null)
            diagnostics.PcConnected(Context.ConnectionId, machine, sid, account, resolved is not null && (claimed is null || claimed == resolved), remoteIp?.ToString());
        await browsers.Clients.Group(HubGroups.User(sid)).SendAsync("HelperStatusChanged", new { connected = true });
        logger.LogInformation("Helper connected for {Account}", account);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var sid = (Context.Items.TryGetValue("sid", out var s) ? s as string : null) ?? "";
        var account = Context.Items.TryGetValue("account", out var a) ? a as string : sid;
        tracker.Remove(sid, Context.ConnectionId);
        registry.Remove(sid, Context.ConnectionId);
        diagnostics.Disconnected(sid);
        diagnostics.PcDisconnected(Context.ConnectionId);
        // Tell the browser first: it must not depend on anything below succeeding. Another helper of the same operator may still be
        // connected; the browser only needs to know whether any is.
        try { await browsers.Clients.Group(HubGroups.User(sid)).SendAsync("HelperStatusChanged", new { connected = registry.Find(sid) is not null }); }
        catch (Exception ex) { logger.LogDebug(ex, "Could not push the helper status"); }
        logger.LogInformation("Helper disconnected for {Account}", account);
        await base.OnDisconnectedAsync(exception);
    }

    // ----- jobs v2: the helper reports on targets of its own operator's jobs only (checked against the job's initiator SID)
    public async Task<bool> JobTargetStarted(Guid targetId) => (await jobReports.StartedAsync(targetId, Sid)).Ok;

    public async Task<bool> JobLog(Guid targetId, string stream, string[] lines) => (await jobReports.LogAsync(targetId, stream, lines, Sid)).Ok;

    public async Task<bool> JobProgress(Guid targetId, int percent) => (await jobReports.ProgressAsync(targetId, percent, Sid)).Ok;

    public async Task<bool> JobTargetCompleted(Guid targetId, string state, int? exitCode, string? summary)
    {
        if (!Enum.TryParse<TargetState>(state, true, out var parsed) || int.TryParse(state, out _)) return false;
        var outcome = await jobReports.CompletedAsync(targetId, parsed, exitCode, summary, Sid);
        if (!outcome.Ok) logger.LogWarning("Rejected job report for {Target} from {Account}: {Reason}", targetId, Context.User!.GetAccount(), outcome.Message);
        return outcome.Ok;
    }

    private string Sid => Context.User!.GetSid()!;

    /// <summary>The host name behind the connecting address (reverse DNS, 2 s limit); this PC's own name for loopback.</summary>
    private static async Task<string?> ResolveHostAsync(System.Net.IPAddress? ip)
    {
        if (ip is null) return null;
        if (System.Net.IPAddress.IsLoopback(ip)) return Environment.MachineName;
        try
        {
            var lookup = System.Net.Dns.GetHostEntryAsync(ip);
            if (await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(2))) != lookup) return null;
            return (await lookup).HostName.Split('.')[0];
        }
        catch (Exception) { return null; }
    }
}
