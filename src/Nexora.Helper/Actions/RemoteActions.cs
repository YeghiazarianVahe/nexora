using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Nexora.Contracts;

namespace Nexora.Helper.Actions;

// ---------- the WMI surface the actions need, behind an interface so the actions can be tested without a PC ----------

/// <summary>One WMI instance: its properties, plus an opaque handle the host needs to call a method on it.</summary>
public sealed record WmiRow(object Handle, IReadOnlyDictionary<string, object?> Props)
{
    public string? Text(string name) => Props.GetValueOrDefault(name)?.ToString();
    public bool Flag(string name) => Props.GetValueOrDefault(name) is true;
}

public interface IWmiHost : IDisposable
{
    IReadOnlyList<WmiRow> Query(string wql);
    /// <summary>Calls a WMI method and returns its ReturnValue.</summary>
    int Invoke(WmiRow row, string method);
    void Delete(WmiRow row);
}

/// <summary>CIM over DCOM with the operator's own token (default credentials).</summary>
public sealed class CimWmiHost(string host) : IWmiHost
{
    private const string Ns = @"root\cimv2";
    private readonly CimSession _session = JobExecutor.Open(host);
    private readonly CimOperationOptions _op = new() { Timeout = JobExecutor.CimTimeout };

    public IReadOnlyList<WmiRow> Query(string wql) =>
        _session.QueryInstances(Ns, "WQL", wql, _op)
            .Select(i => new WmiRow(i, i.CimInstanceProperties.ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase)))
            .ToList();

    public int Invoke(WmiRow row, string method) =>
        Convert.ToInt32(_session.InvokeMethod(Ns, (CimInstance)row.Handle, method, null, _op).ReturnValue.Value);

    public void Delete(WmiRow row) => _session.DeleteInstance(Ns, (CimInstance)row.Handle, _op);

    public void Dispose() => _session.Dispose();
}

/// <summary>The helper re-validates every input: it acts with the operator's own rights, so it never trusts a job just because it arrived on the channel.</summary>
public static partial class HelperInput
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{0,62}\z")]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-$ ]{1,256}\z")]
    private static partial Regex ServicePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.\- ]{1,128}\z")]
    private static partial Regex ProcessPattern();

    [GeneratedRegex(@"^S-1-(5-21|12-1)(-\d{1,10}){3,15}\z")]
    private static partial Regex UserSidPattern();

    [GeneratedRegex(@"^[A-Za-z]:\\Users\\(?=[A-Za-z0-9_-])(?!.*[. ]\z)[A-Za-z0-9._ -]{1,64}\z")]
    private static partial Regex ProfilePathPattern();

    public static bool Host(string? v) => v is not null && HostPattern().IsMatch(v);
    public static bool Service(string? v) => v is not null && ServicePattern().IsMatch(v);
    public static bool Process(string? v) => v is not null && ProcessPattern().IsMatch(v);
    public static bool UserSid(string? v) => v is not null && UserSidPattern().IsMatch(v);
    public static bool ProfilePath(string? v) => v is not null && ProfilePathPattern().IsMatch(v);

    /// <summary>Doubles single quotes for a WQL string literal. It does NOT escape backslashes: callers must only pass values that already passed a pattern above (none of them allows a backslash or quote), never raw WMI or user text.</summary>
    public static string Quote(string v) => v.Replace("'", "''");
}

internal static class Outcomes
{
    public static ActionOutcome Fail(string summary, int? code = null) => new("Failed", code, summary);
    public static ActionOutcome Ok(string summary) => new("Succeeded", 0, summary);
}

// ---------- services ----------

/// <summary>
/// Start, stop and restart a service through Win32_Service. Stopping or restarting a critical system service is refused here as well as on the server (starting one is allowed).
/// With stopDependents the running dependent services are stopped first (leaves first) and, on a restart, started again afterwards;
/// every dependent is checked against the protected floor BEFORE anything is stopped. A half-finished restart is a failure, never a success.
/// All waits share the job's time budget; on cancellation the log says what was left stopped.
/// </summary>
public sealed class ServiceAction(Func<string, IWmiHost>? open = null, TimeSpan? wait = null, TimeSpan? poll = null) : IJobAction
{
    private const int StopHasRunningDependents = 3, StopNotActive = 5, MaxDependentDepth = 16;
    private readonly Func<string, IWmiHost> _open = open ?? (h => new CimWmiHost(h));
    private readonly TimeSpan _wait = wait ?? TimeSpan.FromSeconds(60);
    private readonly TimeSpan _poll = poll ?? TimeSpan.FromMilliseconds(500);

    public IReadOnlyList<string> Codes { get; } = ["svc.start", "svc.stop", "svc.restart"];

    private static bool IsRunning(WmiRow? r) => string.Equals(r?.Text("State"), "Running", StringComparison.OrdinalIgnoreCase);
    private static bool IsActive(WmiRow? r) => (r?.Text("State") ?? "").Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant()
        is "running" or "paused" or "startpending" or "continuepending" or "pausepending" or "stoppending";

    // Trimmed, so 'WinDefend ' can never slip past the floor on a technicality.
    private static bool IsProtected(string n) => ProtectedNames.Services.Contains(n.Trim(), StringComparer.OrdinalIgnoreCase);

    public async Task<ActionOutcome> RunAsync(JobPayload job, IJobSink sink, CancellationToken ct)
    {
        var name = job.Parameters.GetValueOrDefault("serviceName");
        if (!HelperInput.Host(job.Host)) return Outcomes.Fail("Invalid target host.", 2);
        if (!HelperInput.Service(name)) return Outcomes.Fail("Invalid service name.", 2);
        var stop = job.ActionCode is "svc.stop" or "svc.restart";
        // Starting a stopped critical service is allowed (remediation); stopping or restarting one is not.
        if (stop && IsProtected(name!)) return Outcomes.Fail($"Refused: '{name}' is a protected system service.", 2);
        // Nexora never starts remote-management services (WinRM and the like), whatever the server says.
        if (job.ActionCode is "svc.start" or "svc.restart" && ProtectedNames.IsNoStartService(name)) return Outcomes.Fail($"Refused: '{name}' is a remote-management service that Nexora never starts.", 2);
        var start = job.ActionCode is "svc.start" or "svc.restart";
        var withDependents = bool.TryParse(job.Parameters.GetValueOrDefault("stopDependents"), out var sd) && sd;
        // The helper cancels the job after TimeoutSeconds; waits end a little earlier so the job fails cleanly instead of being cut off.
        var budgetEnd = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(2, job.TimeoutSeconds - 3));
        ct.ThrowIfCancellationRequested();
        using var wmi = _open(job.Host);

        Task<T> Wmi<T>(Func<T> f) => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return f();
        }, ct);
        WmiRow? Find(string n) => wmi.Query($"SELECT * FROM Win32_Service WHERE Name = '{HelperInput.Quote(n)}'").FirstOrDefault();
        // While stopping, at most half of the remaining budget may be used, so the start side of a restart is never instantly timed out.
        DateTime? stopPhaseEnd = null;
        async Task<bool> WaitFor(string n, string state)
        {
            var now = DateTime.UtcNow;
            var limit = stopPhaseEnd is { } sp && sp < budgetEnd ? sp : budgetEnd;
            var deadline = now + _wait < limit ? now + _wait : limit;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var observed = await Wmi(() => Find(n));
                ct.ThrowIfCancellationRequested();
                if (string.Equals(observed?.Text("State"), state, StringComparison.OrdinalIgnoreCase)) return true;
                await Task.Delay(_poll, ct);
            }
            return false;
        }
        // Everything that depends on 'root', leaves first (the order to stop them in), without duplicates.
        // A name that fails validation is returned but not followed, so it can be refused without ever being put in a query.
        (List<(string Name, bool Running)> Found, bool TooDeep) Dependents(string root)
        {
            var result = new List<(string, bool)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
            var tooDeep = false;
            void Visit(string n, int depth)
            {
                if (depth >= MaxDependentDepth) { tooDeep = true; return; }
                foreach (var d in wmi.Query($"ASSOCIATORS OF {{Win32_Service.Name='{HelperInput.Quote(n)}'}} WHERE AssocClass=Win32_DependentService Role=Antecedent ResultRole=Dependent"))
                {
                    var dn = d.Text("Name");
                    if (dn is null || !seen.Add(dn)) continue;
                    if (HelperInput.Service(dn)) Visit(dn, depth + 1);
                    result.Add((dn, IsActive(d)));
                }
            }
            Visit(root, 0);
            return (result, tooDeep);
        }

        var stoppedDependents = new List<string>(); // confirmed stopped by us, in stop order
        var down = new List<string>();              // everything we stopped (or tried to) that is not back up yet

        async Task<(string? Problem, bool ByUs)> StopOne(string n)
        {
            var row = await Wmi(() => Find(n));
            if (row is null) return ("not found", false);
            var rc = await Wmi(() => wmi.Invoke(row, "StopService"));
            await sink.Log("system", $"StopService {n} -> {rc}");
            if (rc == StopNotActive) return (null, false); // it was not running: not ours to start again
            if (rc != 0) return ($"stop failed (return code {rc})", false);
            down.Add(n);
            return await WaitFor(n, "Stopped") ? (null, true) : ("timed out waiting for it to stop", false);
        }
        async Task<string?> StartOne(string n)
        {
            var row = await Wmi(() => Find(n));
            if (row is null) return "not found";
            var rc = await Wmi(() => wmi.Invoke(row, "StartService"));
            await sink.Log("system", $"StartService {n} -> {rc}");
            if (rc != 0) return $"start failed (return code {rc})";
            if (!await WaitFor(n, "Running")) return "did not reach Running";
            down.Remove(n);
            return null;
        }
        static string Stopped(List<string> l) => l.Count > 0 ? $" Dependents already stopped: {string.Join(", ", l)}." : "";
        // The service itself is still running: put back the dependents we took down.
        async Task<string> Rollback()
        {
            stopPhaseEnd = null;   // putting things back may use all the time that is left
            if (stoppedDependents.Count == 0) return "";
            var failed = new List<string>();
            foreach (var d in Enumerable.Reverse(stoppedDependents))
            {
                await sink.Log("system", $"Starting dependent {d} again");
                if (await StartOne(d) is { } p) failed.Add($"{d} ({p})");
            }
            return failed.Count == 0 ? " The dependents that were stopped were started again." : $" Could NOT start these dependents again: {string.Join("; ", failed)}.";
        }

        // The original failure goes into the log BEFORE the rollback starts: starting dependents again can take time, fail, or be cancelled.
        async Task<ActionOutcome> FailAndRollBack(string summary, int? code = null)
        {
            await sink.Log("system", summary);
            return Outcomes.Fail(summary + await Rollback(), code);
        }

        var service = await Wmi(() => Find(name!));
        if (service is null) return Outcomes.Fail($"Service '{name}' was not found on {job.Host}.");
        var running = IsRunning(service);
        var stopped = string.Equals(service.Text("State"), "Stopped", StringComparison.OrdinalIgnoreCase);
        if (stop && !stopped && !IsActive(service))
            return Outcomes.Fail($"Service '{name}' has an unknown state; its stop cannot be verified.");

        try
        {
            if (stop && IsActive(service))
            {
                if (start) stopPhaseEnd = DateTime.UtcNow + (budgetEnd - DateTime.UtcNow) / 2;   // a restart reserves the other half for starting again
                if (withDependents)
                {
                    var (dependents, tooDeep) = await Wmi(() => Dependents(name!));
                    if (tooDeep) return Outcomes.Fail($"Refused: the dependency tree of '{name}' is too deep; nothing was stopped.", 2);
                    var bad = dependents.Where(d => !HelperInput.Service(d.Name) || IsProtected(d.Name)).Select(d => d.Name).ToList();
                    if (bad.Count > 0)
                        return Outcomes.Fail($"Refused: '{name}' has dependent service(s) that are protected or invalid ({string.Join(", ", bad)}); nothing was stopped.", 2);

                    var toStop = dependents.Where(d => d.Running).Select(d => d.Name).ToList();
                    if (toStop.Count > 0) await sink.Log("system", $"Stopping {toStop.Count.ToString(CultureInfo.InvariantCulture)} running dependent service(s): {string.Join(", ", toStop)}");
                    foreach (var d in toStop)
                    {
                        var (problem, byUs) = await StopOne(d);
                        if (byUs) stoppedDependents.Add(d);
                        if (problem is not null)
                        {
                            var timedOut = problem.StartsWith("timed", StringComparison.Ordinal);
                            var summary = $"Could not stop dependent '{d}' ({problem}); '{name}' was not stopped." + Stopped(stoppedDependents)
                                + (timedOut && stoppedDependents.Count > 0 ? " They were NOT started again." : "");
                            if (timedOut) return Outcomes.Fail(summary);   // out of time: no attempt to start anything again
                            return await FailAndRollBack(summary);
                        }
                    }
                }

                await sink.Log("system", $"Stopping {name}");
                var rc = await Wmi(() => wmi.Invoke(service, "StopService"));
                await sink.Log("system", $"StopService {name} -> {rc}");
                if (rc == StopHasRunningDependents)
                {
                    var names = (await Wmi(() => Dependents(name!))).Found.Where(d => d.Running).Select(d => d.Name).ToList();
                    var hint = names.Count > 0
                        ? $"'{name}' has running dependent service(s): {string.Join(", ", names)}." + (withDependents ? " They were still running when the stop was sent." : " Stop them first, or run again with 'stop dependents'.")
                        : $"Stop failed (return code {rc}): other services depend on '{name}'.";
                    return await FailAndRollBack(hint + Stopped(stoppedDependents), rc);
                }
                if (rc != 0 && rc != StopNotActive) return await FailAndRollBack($"Stop failed (return code {rc})." + Stopped(stoppedDependents), rc);
                down.Add(name!);
                if (!await WaitFor(name!, "Stopped"))
                    return Outcomes.Fail("Timed out waiting for the service to stop." + Stopped(stoppedDependents) + (stoppedDependents.Count > 0 ? " They were NOT started again." : ""));
                stopPhaseEnd = null;   // the start side gets what is left
            }
            else if (stop && !start)
            {
                return Outcomes.Ok($"Service '{name}' is already stopped.");
            }
            if (!start)
                return Outcomes.Ok(stoppedDependents.Count > 0
                    ? $"Service '{name}' is stopped (also stopped: {string.Join(", ", stoppedDependents)})."
                    : $"Service '{name}' is stopped.");

            if (!stop && running) return Outcomes.Ok($"Service '{name}' is already running.");
            await sink.Log("system", $"Starting {name}");
            var startProblem = await StartOne(name!);
            if (startProblem is not null)
                return Outcomes.Fail($"Start of '{name}' failed: {startProblem}." + Stopped(stoppedDependents) + (stoppedDependents.Count > 0 ? " Those dependents were NOT restarted." : ""));

            var failed = new List<string>();
            var restarted = new List<string>();
            foreach (var d in Enumerable.Reverse(stoppedDependents)) // dependency order: closest to the service first
            {
                await sink.Log("system", $"Restarting dependent {d}");
                var problem = await StartOne(d);
                if (problem is null) restarted.Add(d); else failed.Add($"{d} ({problem})");
            }
            if (failed.Count > 0)
                return Outcomes.Fail($"Partial result: '{name}' is running again, but dependent service(s) did not come back: {string.Join("; ", failed)}.", 1);
            return Outcomes.Ok(restarted.Count > 0
                ? $"Service '{name}' is running (dependents restarted: {string.Join(", ", restarted)})."
                : $"Service '{name}' is running.");
        }
        catch (OperationCanceledException)
        {
            if (down.Count > 0) await sink.Log("system", $"Cancelled; left stopped: {string.Join(", ", down)}");
            throw;
        }
    }
}

// ---------- processes ----------

/// <summary>Stops one process, matched by id AND name so a recycled id never stops an unrelated process.</summary>
public sealed class KillProcessAction(Func<string, IWmiHost>? open = null, TimeSpan? wait = null, TimeSpan? poll = null) : IJobAction
{
    private readonly Func<string, IWmiHost> _open = open ?? (h => new CimWmiHost(h));

    public IReadOnlyList<string> Codes { get; } = ["proc.kill"];

    public Task<ActionOutcome> RunAsync(JobPayload job, IJobSink sink, CancellationToken ct)
    {
        var name = job.Parameters.GetValueOrDefault("processName");
        if (!HelperInput.Host(job.Host)) return Task.FromResult(Outcomes.Fail("Invalid target host.", 2));
        if (!int.TryParse(job.Parameters.GetValueOrDefault("pid"), out var pid) || pid <= 4) return Task.FromResult(Outcomes.Fail("Invalid process id.", 2));
        if (!HelperInput.Process(name)) return Task.FromResult(Outcomes.Fail("Invalid process name.", 2));
        if (ProtectedNames.Processes.Contains(name!, StringComparer.OrdinalIgnoreCase)) return Task.FromResult(Outcomes.Fail($"Refused: '{name}' is a protected system process.", 2));

        return Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            using var wmi = _open(job.Host);
            var query = $"SELECT * FROM Win32_Process WHERE ProcessId = {pid} AND Name = '{HelperInput.Quote(name!)}'";
            var process = wmi.Query(query).FirstOrDefault();
            if (process is null) return Outcomes.Fail($"No process '{name}' with pid {pid} on {job.Host} (it may have already exited).");
            ct.ThrowIfCancellationRequested();
            var rc = wmi.Invoke(process, "Terminate");
            if (rc != 0) return Outcomes.Fail($"Stopping the process failed (return code {rc}).", rc);
            // A successful method return only acknowledges the request. Verify disappearance before reporting success.
            var budget = wait ?? TimeSpan.FromSeconds(Math.Clamp(job.TimeoutSeconds, 1, 10));
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < budget)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = wmi.Query(query).Count;
                ct.ThrowIfCancellationRequested();
                if (remaining == 0) return Outcomes.Ok($"Stopped {name} ({pid}).");
                await Task.Delay(poll ?? TimeSpan.FromMilliseconds(200), ct);
            }
            return Outcomes.Fail($"Stop was requested for {name} ({pid}), but its exit could not be verified in time.");
        }, ct);
    }
}

// ---------- user profiles ----------

/// <summary>
/// Deletes a user profile with Win32_UserProfile.Delete (the same operation as System Properties > User Profiles > Delete: it removes the
/// folder and the registration; the folder is never deleted on its own). The server has already verified the request, but the helper acts with the
/// operator's rights, so it checks again on the PC at execution time and refuses unless ALL of these hold: the SID is a user SID and still has this
/// folder, the folder is directly under X:\Users and not a default/public/system/Administrator one and not a link (reparse point), the profile is not
/// special, not loaded, not the operator's own, and its user is not in an interactive logon session. If who is signed in cannot be read, it refuses.
/// </summary>
public sealed class DeleteProfileAction(Func<string, IWmiHost>? open = null, Func<string, FileAttributes?>? attributes = null) : IJobAction
{
    private readonly Func<string, IWmiHost> _open = open ?? (h => new CimWmiHost(h));
    private readonly Func<string, FileAttributes?> _attributes = attributes ?? ReadAttributes;

    /// <summary>Win32_LogonSession.LogonType: interactive, unlock, remote interactive, cached interactive, cached remote interactive, cached unlock.</summary>
    private static readonly int[] InteractiveLogonTypes = [2, 7, 10, 11, 12, 13];

    public IReadOnlyList<string> Codes { get; } = ["profile.delete"];

    public Task<ActionOutcome> RunAsync(JobPayload job, IJobSink sink, CancellationToken ct)
    {
        var sid = job.Parameters.GetValueOrDefault("sid");
        var path = job.Parameters.GetValueOrDefault("localPath");
        if (!HelperInput.Host(job.Host)) return Task.FromResult(Outcomes.Fail("Invalid target host.", 2));
        if (!HelperInput.UserSid(sid)) return Task.FromResult(Outcomes.Fail("Invalid profile SID (system profiles cannot be deleted).", 2));
        if (!HelperInput.ProfilePath(path)) return Task.FromResult(Outcomes.Fail(@"Profile path must be a folder directly under X:\Users.", 2));
        if (string.Equals(sid, job.InitiatorSid, StringComparison.OrdinalIgnoreCase)) return Task.FromResult(Outcomes.Fail("Refused: this is your own profile.", 2));
        var leaf = path!.Split('\\').Last().TrimEnd('.', ' ');   // Windows ignores trailing dots and spaces
        if (ProtectedNames.SystemProfileFolders.Contains(leaf, StringComparer.OrdinalIgnoreCase) || leaf.Equals("Administrator", StringComparison.OrdinalIgnoreCase) || sid!.EndsWith("-500", StringComparison.Ordinal))
            return Task.FromResult(Outcomes.Fail("Refused: default, public, system and built-in Administrator profiles cannot be deleted.", 2));

        return Task.Run(() =>
        {
            using var wmi = _open(job.Host);
            var profile = wmi.Query($"SELECT * FROM Win32_UserProfile WHERE SID = '{HelperInput.Quote(sid!)}'").FirstOrDefault();
            if (profile is null) return Outcomes.Fail($"No profile with SID {sid} on {job.Host} (already deleted?).");

            var actual = profile.Text("LocalPath") ?? "";
            if (!string.Equals(actual, path, StringComparison.OrdinalIgnoreCase))
                return Outcomes.Fail($"Refused: the profile for that SID is at '{actual}', not '{path}'. Refresh the list and try again.", 2);
            // What WMI reports must itself be a plain, canonical path: no dots, spaces or control characters that Windows would normalise away.
            if (!HelperInput.ProfilePath(actual) || !string.Equals(Path.GetFullPath(actual), actual, StringComparison.OrdinalIgnoreCase))
                return Outcomes.Fail("Refused: the profile folder path is not a plain folder directly under X:\\Users.", 2);
            // No other registered profile (system ones included) may point at the same folder: deleting would take their data too.
            static string Norm(string? p) => (p ?? "").Trim().TrimEnd('\\', '.', ' ').ToUpperInvariant();
            if (wmi.Query("SELECT SID, LocalPath FROM Win32_UserProfile")
                .Any(o => !string.Equals(o.Text("SID"), sid, StringComparison.OrdinalIgnoreCase) && Norm(o.Text("LocalPath")) == Norm(actual)))
                return Outcomes.Fail("Refused: another profile on that PC uses the same folder.", 2);
            if (profile.Flag("Special")) return Outcomes.Fail("Refused: this is a system profile.", 2);
            if (profile.Flag("Loaded")) return Outcomes.Fail("Refused: the user is currently signed in on that PC (profile is loaded).", 2);

            HashSet<string> signedIn;
            try { signedIn = SignedInSids(wmi); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Outcomes.Fail("Refused: could not verify who is signed in on that PC, so nothing was deleted.", 2);
            }
            if (signedIn.Contains(sid!)) return Outcomes.Fail("Refused: the user is currently signed in on that PC (interactive session).", 2);

            // The folder is reached over the admin share with the operator's rights; a link there could point anywhere, so it is never followed.
            FileAttributes? folder;
            try { folder = _attributes(ProfileSizesAction.AdminSharePath(job.Host, actual)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Outcomes.Fail("Refused: the profile folder could not be checked, so nothing was deleted.", 2);
            }
            if (folder is { } a && (a & FileAttributes.ReparsePoint) != 0) return Outcomes.Fail("Refused: the profile folder is a link (reparse point), not a real folder.", 2);

            wmi.Delete(profile);
            return Outcomes.Ok($"Deleted profile {actual} ({sid}) on {job.Host}.{FreedText(job)}");
        }, ct);
    }

    /// <summary>"About N MB freed (last measurement)" when the server passed its stored size; the figure is the server's, not measured here.</summary>
    private static string FreedText(JobPayload job) =>
        long.TryParse(job.Parameters.GetValueOrDefault("sizeBytes"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bytes)
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $" About {bytes / 1_048_576.0:N0} MB freed (bytes: {bytes}, from the last size measurement).")
            : "";

    /// <summary>SIDs of the accounts in interactive-type logon sessions. Throws when WMI cannot answer.</summary>
    internal static HashSet<string> SignedInSids(IWmiHost wmi)
    {
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in wmi.Query("SELECT LogonId, LogonType FROM Win32_LogonSession"))
        {
            if (!int.TryParse(session.Text("LogonType"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var type)
                || !InteractiveLogonTypes.Contains(type)) continue;
            var id = session.Text("LogonId") ?? "";
            if (id.Length is 0 or > 20 || !id.All(char.IsAsciiDigit)) continue;   // goes into a query below: digits only
            foreach (var account in wmi.Query($"ASSOCIATORS OF {{Win32_LogonSession.LogonId='{id}'}} WHERE AssocClass=Win32_LoggedOnUser Role=Dependent ResultRole=Antecedent"))
                if (account.Text("SID") is { Length: > 0 } s) sids.Add(s);
        }
        return sids;
    }

    /// <summary>Null when the folder does not exist (nothing to follow); any other failure propagates so the caller can refuse.</summary>
    private static FileAttributes? ReadAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }
}

// ---------- sessions on the operator's own desktop ----------

/// <summary>Remote Desktop and Remote Assistance open on the operator's own desktop; the sign-in there uses the operator's credentials.</summary>
public sealed class SessionAction(Action<string, string>? launch = null) : IJobAction
{
    private readonly Action<string, string> _launch = launch ?? ((file, args) => Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }));

    public IReadOnlyList<string> Codes { get; } = ["session.rdp", "session.assist"];

    public Task<ActionOutcome> RunAsync(JobPayload job, IJobSink sink, CancellationToken ct)
    {
        if (!HelperInput.Host(job.Host)) return Task.FromResult(Outcomes.Fail("Invalid target host.", 2));
        if (job.ActionCode == "session.rdp")
        {
            _launch("mstsc.exe", $"/v:{job.Host}");
            return Task.FromResult(Outcomes.Ok($"Remote Desktop launched for {job.Host}."));
        }
        _launch("msra.exe", $"/offerra {job.Host}");
        return Task.FromResult(Outcomes.Ok($"Remote Assistance offer started for {job.Host}."));
    }
}
