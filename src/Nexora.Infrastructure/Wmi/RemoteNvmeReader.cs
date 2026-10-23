using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace Nexora.Infrastructure.Wmi;

/// <summary>Reads native NVMe logs in a short-lived, noninteractive process using the existing DCOM caller token. No agent, WinRM or SYSTEM task.</summary>
public static partial class RemoteNvmeReader
{
    public sealed record NativeDisk(int Index, string Model, string Serial, string Health, string Identify, string? Ata = null, string? Thresholds = null,
        DateTime? SourceSampledUtc = null, bool SourceStale = false, string? SourceNote = null);
    private sealed record Reply(string Machine, NativeDisk[] Disks, DateTime? CollectedUtc = null);
    public static IReadOnlyList<NativeDisk> ReadLocalCache()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nexora", "DiskTelemetry", "result.json");
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 256 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0) return [];
            if (!TrustedCacheAcl(file.GetAccessControl()) || !TrustedCacheAcl(file.Directory!.GetAccessControl())
                || !TrustedCacheAcl(file.Directory!.Parent!.GetAccessControl())) return [];
            return ParseLocalCache(File.ReadAllText(path), Environment.MachineName, DateTime.UtcNow);
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        catch (JsonException) { return []; }
    }
    /// <summary>Expired cache stays useful as last-known evidence, never as a current reading.</summary>
    public static IReadOnlyList<NativeDisk> ParseLocalCache(string json, string machine, DateTime now)
    {
        var reply = JsonSerializer.Deserialize<Reply>(json);
        if (reply is not { CollectedUtc: { } time, Disks: not null } || !MachineMatches(reply.Machine, machine)
            || time.Kind != DateTimeKind.Utc || time > now.AddSeconds(30)) return [];
        var stale = time < now.AddMinutes(-2);
        return reply.Disks.Select(d => d with { SourceSampledUtc=time, SourceStale=stale,
            SourceNote=stale ? (time < now.AddMinutes(-10) ? "Local collector cache expired; check the telemetry task. Last-known evidence only." : "Local collector is delayed; showing last-known evidence.") : null }).ToArray();
    }
    private static bool TrustedCacheAcl(FileSystemSecurity security)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!system.Equals(owner) && !admins.Equals(owner)) return false;
        var writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        return security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .All(rule => rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & writes) == 0
                || system.Equals(rule.IdentityReference) || admins.Equals(rule.IdentityReference));
    }
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{0,252}$")]
    private static partial Regex HostPattern();

    public static IReadOnlyList<NativeDisk> Read(string host, IReadOnlyList<int> indexes, CimSession session, CancellationToken ct)
    {
        if (!HostPattern().IsMatch(host) || indexes.Count == 0 || indexes.Count > 32 || indexes.Any(i => i < 0)) return [];
        // The endpoint may be registered by IP or a DNS alias. Obtain its machine identity
        // through the same authenticated session rather than comparing a machine name to an IP.
        using var identityOptions = new CimOperationOptions { Timeout = TimeSpan.FromSeconds(10) };
        var machine = session.QueryInstances(@"root\cimv2", "WQL", "SELECT Name FROM Win32_ComputerSystem", identityOptions)
            .Select(row => row.CimInstanceProperties["Name"]?.Value as string).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(machine)) return [];
        var root = new DirectoryInfo($@"\\{host}\C$\ProgramData\Nexora");
        Protect(root, read: true);
        var parent = new DirectoryInfo(Path.Combine(root.FullName, "NativeReads"));
        Protect(parent, read: false);
        var token = Guid.NewGuid().ToString("N");
        var dir = new DirectoryInfo(Path.Combine(parent.FullName, token));
        Protect(dir, read: false);
        var output = Path.Combine(dir.FullName, "result.json");
        var localOutput = $@"C:\ProgramData\Nexora\NativeReads\{token}\result.json";
        uint? processId = null;
        string? expectedCommand = null;
        try
        {
            using var stream = typeof(RemoteNvmeReader).Assembly.GetManifestResourceStream("native-nvme-read.ps1")!;
            using var reader = new StreamReader(stream);
            var script = reader.ReadToEnd().Replace("@@INDEXES@@", string.Join(',', indexes)).Replace("@@OUTPUT@@", localOutput);
            var command = "powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            expectedCommand = command;
            using var parameters = new CimMethodParametersCollection { CimMethodParameter.Create("CommandLine", command, CimType.String, CimFlags.In) };
            using var options = new CimOperationOptions { Timeout = TimeSpan.FromSeconds(10) };
            using var started = session.InvokeMethod(@"root\cimv2", "Win32_Process", "Create", parameters, options);
            if (Convert.ToUInt32(started.ReturnValue.Value) != 0) throw new IOException("Native NVMe read could not start.");
            processId = Convert.ToUInt32(started.OutParameters["ProcessId"].Value);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(output) && DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(200);
            }
            if (!File.Exists(output)) throw new TimeoutException("Native NVMe read timed out.");
            var file = new FileInfo(output);
            if (file.Length > 256 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid native NVMe reply.");
            var reply = JsonSerializer.Deserialize<Reply>(File.ReadAllText(output));
            if (reply is null || !MachineMatches(reply.Machine, machine)) return [];
            if (reply.CollectedUtc is not { Kind: DateTimeKind.Utc } sampled || sampled > DateTime.UtcNow.AddSeconds(30)) return [];
            return reply.Disks.Where(d => indexes.Contains(d.Index)).Select(d => d with {SourceSampledUtc=sampled}).ToArray();
        }
        finally
        {
            // Stop only this uniquely tagged process if it did not finish; guard against PID reuse.
            if (processId is { } pid && !File.Exists(output))
            {
                try
                {
                    foreach (var process in session.QueryInstances(@"root\cimv2", "WQL", $"SELECT * FROM Win32_Process WHERE ProcessId={pid}"))
                    {
                        if (process.CimInstanceProperties["CommandLine"]?.Value is string command && command == expectedCommand)
                            session.InvokeMethod(process, "Terminate", null)?.Dispose();
                    }
                }
                catch (CimException) { }
            }
            try { dir.Refresh(); if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) == 0) dir.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Protect(DirectoryInfo dir, bool read)
    {
        dir.Refresh();
        if (!dir.Exists) dir.Create();
        dir.Refresh();
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked native-read directory refused.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        using var identity = WindowsIdentity.GetCurrent();
        var owner = dir.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
        if (!system.Equals(owner) && !admins.Equals(owner) && !identity.User!.Equals(owner)) throw new UnauthorizedAccessException("Untrusted native-read directory owner.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { system, admins }) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (read) security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(security);
    }

    public static bool Matches(NativeDisk disk, int index, string? serial, string model) => disk.Index == index
        && disk.Model.Equals(model, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(serial) && Normalize(disk.Serial) == Normalize(serial);
    public static bool MachineMatches(string? replyMachine, string authenticatedMachine) =>
        !string.IsNullOrWhiteSpace(replyMachine) && replyMachine.Equals(authenticatedMachine, StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();
}
