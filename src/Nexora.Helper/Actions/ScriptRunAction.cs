using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Nexora.Contracts;

namespace Nexora.Helper.Actions;

/// <summary>
/// What the script runner needs from the target PC. The real implementation uses the admin share (\\host\C$) and the remote Task Scheduler
/// (RPC, never WinRM) with the OPERATOR'S rights; tests use a fake.
/// </summary>
public interface ISystemTaskHost
{
    /// <summary>Creates the job folder (administrators and SYSTEM only) and writes the files into it.</summary>
    Task PrepareAsync(string host, string jobId, IReadOnlyDictionary<string, byte[]> files, CancellationToken ct);
    /// <summary>Registers a one-off task that runs as SYSTEM and starts it.</summary>
    Task StartAsync(string host, string taskName, string command, string arguments, TimeSpan limit, CancellationToken ct);
    /// <summary>Bytes of a file in the job folder from an offset on; null when it does not exist yet.</summary>
    Task<byte[]?> ReadAsync(string host, string jobId, string file, long offset, CancellationToken ct);
    /// <summary>True while the task's process is still running.</summary>
    Task<bool> IsRunningAsync(string host, string taskName, CancellationToken ct);
    Task StopAsync(string host, string taskName, CancellationToken ct);
    /// <summary>Removes the task and the job folder; never throws.</summary>
    Task CleanupAsync(string host, string taskName, string jobId);
}

/// <summary>
/// script.run: runs an approved catalog script on the target as SYSTEM, through a one-off scheduled task created over RPC with the operator's
/// own rights. The server puts the script body in the SIGNED job and its SHA-256 next to it; the helper checks the hash again, so what runs is
/// exactly what the Admin approved. Output is captured to a UTF-8 file in the job folder (not a console code page) and streamed back live.
/// </summary>
public sealed partial class ScriptRunAction(ISystemTaskHost? host = null, TimeSpan? poll = null) : IJobAction
{
    private readonly ISystemTaskHost _host = host ?? new AdminShareSystemTaskHost();
    private readonly TimeSpan _poll = poll ?? TimeSpan.FromSeconds(1);

    public IReadOnlyList<string> Codes { get; } = ["script.run"];

    public const string OutputFile = "output.txt", ExitFile = "exit.txt", RunnerFile = "runner.ps1", ScriptFile = "script.ps1";
    public const string JobsRoot = @"C:\ProgramData\Nexora\Jobs";

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\z")]
    private static partial Regex GuidPattern();

    public static string Runner(string jobId, string? expectedHash = null) => $$"""
        $ErrorActionPreference = 'Continue'
        $dir = '{{JobsRoot}}\{{jobId}}'
        $out = Join-Path $dir '{{OutputFile}}'
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        [Console]::OutputEncoding = $utf8
        $OutputEncoding = $utf8
        $code = 0
        try {
            $expectedHash = '{{expectedHash}}'
            if ($expectedHash -and (Get-FileHash -LiteralPath (Join-Path $dir '{{ScriptFile}}') -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Script hash changed after preparation.' }
            & (Join-Path $dir '{{ScriptFile}}') *>&1 | ForEach-Object { [System.IO.File]::AppendAllText($out, ($_.ToString() + "`r`n"), $utf8) }
            if ($LASTEXITCODE -is [int]) { $code = $LASTEXITCODE } elseif (-not $?) { $code = 1 }
        } catch {
            [System.IO.File]::AppendAllText($out, ('ERROR: ' + $_.Exception.Message + "`r`n"), $utf8)
            $code = 1
        }
        [System.IO.File]::WriteAllText((Join-Path $dir '{{ExitFile}}'), [string]$code, $utf8)
        exit $code
        """;

    public async Task<ActionOutcome> RunAsync(JobPayload job, IJobSink sink, CancellationToken ct)
    {
        if (!HelperInput.Host(job.Host)) return Outcomes.Fail("Invalid target host.", 2);
        var body = job.Parameters.GetValueOrDefault("scriptBody");
        var hash = job.Parameters.GetValueOrDefault("scriptSha256");
        if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(hash)) return Outcomes.Fail("The job carries no script.", 2);
        if (body.Length > 20_000) return Outcomes.Fail("The script is larger than the allowed size.", 2);
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase)) return Outcomes.Fail("The script does not match its approved hash; refusing to run it.", 2);

        await sink.Log("system", $"[helper] script sha256 {actual[..16]}…, to run as SYSTEM on {job.Host}");
        return await RunBodyAsync(job, body, sink, ct, null);
    }

    /// <summary>
    /// Runs a PowerShell body on the target as SYSTEM through a one-off task and streams its output. Used for the catalog scripts (hash-checked
    /// above) and for the fixed built-in maintenance commands (<see cref="SystemCommandAction"/>). <paramref name="mapExit"/> turns the exit code into an outcome.
    /// </summary>
    internal async Task<ActionOutcome> RunBodyAsync(JobPayload job, string body, IJobSink sink, CancellationToken ct, Func<int, ActionOutcome>? mapExit)
    {
        var jobId = job.TargetId.ToString("D");
        if (!GuidPattern().IsMatch(jobId)) return Outcomes.Fail("Invalid job id.", 2);
        var taskName = $"NexoraJob-{jobId}";
        var limit = TimeSpan.FromSeconds(Math.Max(30, job.TimeoutSeconds));

        try
        {
            var files = new Dictionary<string, byte[]>
            {
                [ScriptFile] = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(body)).ToArray(), // BOM so Windows PowerShell reads UTF-8
            };
            var fileHash = Convert.ToHexString(SHA256.HashData(files[ScriptFile]));
            files[RunnerFile] = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Runner(jobId, fileHash))).ToArray();
            await _host.PrepareAsync(job.Host, jobId, files, ct);
            await _host.StartAsync(job.Host, taskName,
                "powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{JobsRoot}\\{jobId}\\{RunnerFile}\"", limit, ct);

            long offset = 0;
            var pending = new StringBuilder();
            int? exit = null;
            var emptyAfterStop = 0;
            while (exit is null)
            {
                await Task.Delay(_poll, ct);
                offset = await DrainAsync(job.Host, jobId, offset, pending, sink, ct);

                if (await _host.ReadAsync(job.Host, jobId, ExitFile, 0, ct) is { } exitBytes
                    && int.TryParse(Encoding.UTF8.GetString(exitBytes).Trim('\uFEFF', ' ', '\r', '\n'), out var code))
                {
                    offset = await DrainAsync(job.Host, jobId, offset, pending, sink, ct); // the last lines
                    exit = code;
                }
                else if (!await _host.IsRunningAsync(job.Host, taskName, ct) && ++emptyAfterStop >= 3)
                {
                    // Not running, no result file: the process was killed (reboot, task stopped by someone else).
                    return Outcomes.Fail("The task ended without reporting a result on the PC.", 1);
                }
            }
            if (pending.Length > 0) await sink.Log("stdout", pending.ToString());

            if (mapExit is not null) return mapExit(exit.Value);
            return exit == 0
                ? Outcomes.Ok("The script finished successfully.")
                : new ActionOutcome("Failed", exit, $"The script exited with code {exit}.");
        }
        catch (OperationCanceledException)
        {
            try { await _host.StopAsync(job.Host, taskName, CancellationToken.None); } catch (Exception) { /* cleanup below */ }
            throw;
        }
        finally
        {
            await _host.CleanupAsync(job.Host, taskName, jobId);
        }
    }

    /// <summary>Reads what the script wrote since <paramref name="offset"/>, sends complete lines, keeps a partial last line for the next round.</summary>
    private async Task<long> DrainAsync(string hostName, string jobId, long offset, StringBuilder pending, IJobSink sink, CancellationToken ct)
    {
        var bytes = await _host.ReadAsync(hostName, jobId, OutputFile, offset, ct);
        if (bytes is null || bytes.Length == 0) return offset;

        // Only whole UTF-8 sequences: stop at the last newline so a multi-byte character is never cut in half.
        var cut = Array.LastIndexOf(bytes, (byte)'\n');
        if (cut < 0) return offset;
        var text = Encoding.UTF8.GetString(bytes, 0, cut + 1);
        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (line.Length == 0 && l.Length == 0) continue;
            await sink.Log("stdout", l);
        }
        return offset + cut + 1;
    }
}

/// <summary>The real thing: admin share for files, remote Task Scheduler (COM over RPC) for the SYSTEM task. Runs with the helper's own (the operator's) token.</summary>
public sealed class AdminShareSystemTaskHost : ISystemTaskHost
{
    private static string Dir(string host, string jobId)
    {
        var rel = $@"ProgramData\Nexora\Jobs\{jobId}";
        return host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? $@"C:\{rel}" : $@"\\{host}\C$\{rel}";
    }

    public Task PrepareAsync(string host, string jobId, IReadOnlyDictionary<string, byte[]> files, CancellationToken ct) => Task.Run(() =>
    {
        var dir = new DirectoryInfo(Dir(host, jobId));
        // Protect parents before creating the leaf: a user-owned parent could replace
        // even an ACL-protected leaf with another directory before SYSTEM starts it.
        ProtectDirectory(dir.Parent!.Parent!, allowUserRead: true);
        ProtectDirectory(dir.Parent, allowUserRead: false);
        if (dir.Exists) throw new IOException("Refusing to reuse an existing job directory.");
        ProtectDirectory(dir, allowUserRead: false);
        foreach (var (name, content) in files)
        {
            if (name != Path.GetFileName(name)) throw new IOException("Invalid job file name.");
            using var output = new FileStream(Path.Combine(dir.FullName, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(content);
        }
    }, ct);

    internal static void ProtectDirectory(DirectoryInfo dir, bool allowUserRead)
    {
        dir.Refresh();
        if (!dir.Exists) dir.Create();
        dir.Refresh();
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing a linked job directory.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        using var identity = WindowsIdentity.GetCurrent();
        var owner = dir.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
        if (!system.Equals(owner) && !admins.Equals(owner) && !identity.User!.Equals(owner))
            throw new UnauthorizedAccessException("Refusing an untrusted owner of a job directory.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { system, admins })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (allowUserRead)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(security);
    }

    public Task StartAsync(string host, string taskName, string command, string arguments, TimeSpan limit, CancellationToken ct) => Task.Run(() =>
    {
        var limitText = $"PT{(int)Math.Ceiling(limit.TotalSeconds)}S";
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>{limitText}</ExecutionTimeLimit>
                <Hidden>true</Hidden>
              </Settings>
              <Actions Context="Author"><Exec><Command>{System.Security.SecurityElement.Escape(command)}</Command><Arguments>{System.Security.SecurityElement.Escape(arguments)}</Arguments></Exec></Actions>
            </Task>
            """;
        var service = Service(host);
        dynamic folder = service.GetFolder("\\");
        dynamic task = folder.RegisterTask(taskName, xml, 6 /* create or update */, null, null, 5 /* service account: SYSTEM */);
        task.Run(null);
    }, ct);

    public Task<byte[]?> ReadAsync(string host, string jobId, string file, long offset, CancellationToken ct) => Task.Run<byte[]?>(() =>
    {
        var path = Path.Combine(Dir(host, jobId), file);
        if (!File.Exists(path)) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset >= fs.Length) return [];
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[Math.Min(fs.Length - offset, 1_000_000)];
        fs.ReadExactly(buffer);
        return buffer;
    }, ct);

    public Task<bool> IsRunningAsync(string host, string taskName, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            dynamic task = ((dynamic)Service(host)).GetFolder("\\").GetTask(taskName);
            return (int)task.State == 4; // TASK_STATE_RUNNING
        }
        catch (Exception) { return false; }
    }, ct);

    public Task StopAsync(string host, string taskName, CancellationToken ct) => Task.Run(() =>
    {
        dynamic task = ((dynamic)Service(host)).GetFolder("\\").GetTask(taskName);
        task.Stop(0);
    }, ct);

    public Task CleanupAsync(string host, string taskName, string jobId) => Task.Run(() =>
    {
        try { ((dynamic)Service(host)).GetFolder("\\").DeleteTask(taskName, 0); } catch (Exception) { }
        try { Directory.Delete(Dir(host, jobId), true); } catch (Exception) { }
    });

    private static dynamic Service(string host)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("The Task Scheduler is not available.");
        dynamic svc = Activator.CreateInstance(type)!;
        if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) svc.Connect(); else svc.Connect(host);
        return svc;
    }
}
