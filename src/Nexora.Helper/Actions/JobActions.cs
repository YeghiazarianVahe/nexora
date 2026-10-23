using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Nexora.Contracts;

namespace Nexora.Helper.Actions;

/// <summary>Where an action writes live output and progress.</summary>
public interface IJobSink
{
    Task Log(string stream, string line);
    Task Progress(int percent);
}

/// <param name="State">Succeeded, Warning, Failed.</param>
public sealed record ActionOutcome(string State, int? ExitCode, string Summary);

/// <summary>
/// One allowlisted action the helper can execute with the operator's own token. The server decides what is allowed; the helper
/// only ever runs codes registered here, so a code it does not implement is refused even if the server sends it.
/// </summary>
public interface IJobAction
{
    IReadOnlyList<string> Codes { get; }
    Task<ActionOutcome> RunAsync(JobPayload job, IJobSink sink, CancellationToken ct);
}

public static class ActionRegistry
{
    private static readonly IJobAction[] All = [new DemoAction(), new ProfileSizesAction(), new ServiceAction(), new KillProcessAction(), new DeleteProfileAction(), new SessionAction(), new MessageAction(), new ScriptRunAction(), new SystemCommandAction(), new DiagCollectAction()];
    private static readonly Dictionary<string, IJobAction> ByCode = All.SelectMany(a => a.Codes.Select(c => (c, a))).ToDictionary(x => x.c, x => x.a, StringComparer.Ordinal);

    public static bool IsKnown(string code) => ByCode.ContainsKey(code);
    public static IJobAction? Find(string code) => ByCode.GetValueOrDefault(code);
}

/// <summary>Simulated job for exercising the pipeline. Touches nothing.</summary>
public sealed class DemoAction : IJobAction
{
    public IReadOnlyList<string> Codes { get; } = ["demo.simulate", "demo.simulate.medium", "demo.simulate.high", "demo.simulate.critical"];

    public async Task<ActionOutcome> RunAsync(JobPayload job, IJobSink sink, CancellationToken ct)
    {
        int Int(string name, int fallback) => job.Parameters.TryGetValue(name, out var v) && int.TryParse(v, out var n) ? n : fallback;
        var steps = Math.Clamp(Int("steps", 5), 1, 20);
        var delay = Math.Clamp(Int("delayMs", 400), 0, 2000);
        var outcome = job.Parameters.GetValueOrDefault("outcome", "success");

        await sink.Log("system", $"[helper] {job.ActionCode} for {job.Host}, running as {System.Security.Principal.WindowsIdentity.GetCurrent().Name}");
        for (var i = 1; i <= steps; i++)
        {
            await Task.Delay(delay, ct);
            await sink.Log("stdout", $"step {i} of {steps}");
            await sink.Progress(i * 100 / steps);
        }
        if (outcome == "hang") await Task.Delay(Timeout.Infinite, ct);

        return outcome switch
        {
            "fail" => new ActionOutcome("Failed", 1, "Simulated failure."),
            "warning" => new ActionOutcome("Warning", 0, "Finished with a simulated warning."),
            _ => new ActionOutcome("Succeeded", 0, "Simulated run completed."),
        };
    }
}

/// <summary>
/// Adds up the size of each user-profile folder on a workstation, through the admin share (\\host\C$) with the operator's own
/// rights. Read-only. One JSON line per profile goes to the output, which the Profiles screen reads.
/// </summary>
public sealed partial class ProfileSizesAction : IJobAction
{
    public IReadOnlyList<string> Codes { get; } = ["profile.sizes"];

    [GeneratedRegex(@"^S-1-\d{1,3}(-\d{1,10}){2,15}$")]
    private static partial Regex SidPattern();

    [GeneratedRegex(@"^[A-Za-z]:\\[^<>:""/|?*\x00-\x1F]+$")]
    private static partial Regex PathPattern();

    public async Task<ActionOutcome> RunAsync(JobPayload job, IJobSink sink, CancellationToken ct)
    {
        var only = job.Parameters.GetValueOrDefault("sid");
        if (!string.IsNullOrEmpty(only) && !SidPattern().IsMatch(only)) return new ActionOutcome("Failed", 2, "Invalid SID.");

        var profiles = await Task.Run(() => List(job.Host, only), ct);
        if (profiles.Count == 0) return new ActionOutcome("Warning", 0, "No matching user profiles were found.");

        long total = 0;
        for (var i = 0; i < profiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (sid, path) = profiles[i];
            var root = AdminSharePath(job.Host, path);
            await sink.Log("system", $"Measuring {path} ({i + 1} of {profiles.Count})");

            var (bytes, files, errors, partial) = await Task.Run(() => Measure(root, ct, () => { }), ct);
            total += bytes;
            await sink.Log("stdout", JsonSerializer.Serialize(new { sid, path, bytes, files, errors, partial }));
            await sink.Progress((i + 1) * 100 / profiles.Count);
        }
        return new ActionOutcome("Succeeded", 0, $"Measured {profiles.Count} profile(s), {total / 1_048_576.0:N0} MB in total.");
    }

    private static List<(string Sid, string Path)> List(string host, string? only)
    {
        using var session = JobExecutor.Open(host);
        var rows = session.QueryInstances(@"root\cimv2", "WQL", "SELECT SID, LocalPath, Special FROM Win32_UserProfile",
            new CimOperationOptions { Timeout = TimeSpan.FromSeconds(30) });
        var list = new List<(string, string)>();
        foreach (var r in rows)
        {
            var sid = r.CimInstanceProperties["SID"].Value?.ToString();
            var path = r.CimInstanceProperties["LocalPath"].Value?.ToString();
            if (sid is null || path is null || r.CimInstanceProperties["Special"].Value is true) continue;
            if (!SidPattern().IsMatch(sid) || !PathPattern().IsMatch(path)) continue;
            if (only is not null && !string.Equals(only, sid, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add((sid, path));
        }
        return list;
    }

    /// <summary>C:\Users\x on host PC-1 is read as \\PC-1\C$\Users\x; on the helper's own PC the local path is used.</summary>
    internal static string AdminSharePath(string host, string localPath)
    {
        if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return localPath;
        return $@"\\{host}\{localPath[0]}${localPath[2..]}";
    }

    /// <summary>Sums file sizes without following reparse points (junctions, symlinks), so nothing is counted twice or escapes the folder.</summary>
    internal static (long Bytes, long Files, long Errors, bool Partial) Measure(string root, CancellationToken ct, Action tick)
    {
        long bytes = 0, files = 0, errors = 0;
        var partial = false;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false,
        };
        try
        {
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                try { bytes += f.Length; files++; }
                catch (IOException) { errors++; }
                if (files % 5000 == 0) tick();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors++;
            partial = true;
        }
        return (bytes, files, errors, partial);
    }
}
