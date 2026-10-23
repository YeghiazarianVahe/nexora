using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Nexora.Application;
using Nexora.Application.Services;

namespace Nexora.Infrastructure.Wmi;

/// <summary>
/// Agentless telemetry over WMI/CIM (DCOM), using the Nexora process identity.
/// Native NVMe reads use a bounded process with protected temporary output; device queries are read-only.
/// Mutating maintenance actions go through the operator helper.
/// </summary>
public sealed partial class WmiEndpointCollector(IOptions<NexoraOptions> options, ILogger<WmiEndpointCollector> logger) : IEndpointCollector, IServiceCollector, IProfileCollector, IPerformanceCollector
{
    private const string Ns = @"root\cimv2";
    private const uint HklmKey = 0x80000002;
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(Math.Max(3, options.Value.Monitoring.ConnectTimeoutSeconds));

    public Task<EndpointSample> CollectSampleAsync(string host, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var session = Open(host);
            var op = Operation();

            var os = Query(session, op, "SELECT Caption, Version, LastBootUpTime, FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem").FirstOrDefault();
            ct.ThrowIfCancellationRequested();

            string? user = null, reportedName = null, maker = null, model = null;
            try
            {
                var cs = Query(session, op, "SELECT UserName, Name, Manufacturer, Model FROM Win32_ComputerSystem").FirstOrDefault();
                user = Str(cs, "UserName");
                reportedName = Str(cs, "Name");
                maker = Str(cs, "Manufacturer")?.Trim();
                model = Str(cs, "Model")?.Trim();
            }
            catch (CimException) { }

            if (string.IsNullOrEmpty(user))
            {
                try { user = ExplorerOwner(session, op); }
                catch (CimException) { }
            }

            var cpu = ReadCpu(session, op);

            double? disk = null;
            var logical = Query(session, op, "SELECT DeviceID, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3")
                .Select(d => (letter: Str(d, "DeviceID"), fs: Str(d, "FileSystem"), size: Num(d, "Size"), free: Num(d, "FreeSpace")))
                .Where(d => d.size is > 0 && d.free.HasValue).ToList();
            if (logical.Count > 0) disk = logical.Max(d => (1 - d.free!.Value / d.size!.Value) * 100);
            var volumes = logical.Where(d => !string.IsNullOrEmpty(d.letter))
                .Select(d => new VolumeSample(d.letter!, d.fs, (long)d.size!.Value, (long)d.free!.Value)).ToList();

            double? ram = null;
            var total = Num(os, "TotalVisibleMemorySize");
            var free = Num(os, "FreePhysicalMemory");
            if (total is > 0 && free.HasValue) ram = (1 - free.Value / total.Value) * 100;

            string? ip = null;
            try
            {
                ip = Query(session, op, "SELECT IPAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE")
                    .SelectMany(n => (n.CimInstanceProperties["IPAddress"]?.Value as string[]) ?? [])
                    .FirstOrDefault(a => System.Net.IPAddress.TryParse(a, out var parsed)
                        && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            }
            catch (CimException) { }

            // Installed RAM comes from the same read as the usage percentage (KB to bytes).
            long? ramTotal = total is > 0 ? (long)(total.Value * 1024) : null;
            long? ramFree = total is > 0 && free is >= 0 && free <= total ? (long)(free.Value * 1024) : null;
            return new EndpointSample(ip, user, Str(os, "Caption"), Str(os, "Version"), Boot(os), Round(cpu), Round(ram), Round(disk), reportedName, maker, model, ramTotal, volumes, ramFree);
        }, ct);

    public Task<EndpointDetailDto> CollectDetailAsync(string host, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var session = Open(host);
            var op = Operation();
            var warnings = new List<string>();

            T Section<T>(string name, Func<T> read, T empty)
            {
                try { return read(); }
                catch (Exception ex) when (ex is CimException or InvalidOperationException)
                {
                    logger.LogDebug(ex, "{Section} failed on {Host}", name, host);
                    warnings.Add($"{name}: could not be read. Details are in the server log.");
                    return empty;
                }
            }

            var services = Section("Services", () => Query(session, op, "SELECT Name, DisplayName, State, StartMode FROM Win32_Service")
                .Select(s => new ServiceInfo(Str(s, "Name") ?? "", Str(s, "DisplayName") ?? "", Str(s, "State") ?? "", Str(s, "StartMode") ?? ""))
                .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(), new List<ServiceInfo>());
            ct.ThrowIfCancellationRequested();

            var processes = Section("Processes", () => ReadProcesses(session, op, ct), new List<ProcessInfo>());

            var disks = Section("Disks", () => Query(session, op, "SELECT DeviceID, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3")
                .Select(d => new DiskInfo(Str(d, "DeviceID") ?? "", Str(d, "VolumeName"), (long)(Num(d, "Size") ?? 0), (long)(Num(d, "FreeSpace") ?? 0)))
                .ToList(), new List<DiskInfo>());
            ct.ThrowIfCancellationRequested();

            var software = Section("Software", () => ReadSoftware(session, ct), new List<SoftwareInfo>());
            var errors = Section("Event log", () => ReadErrors(session, ct), new List<EventInfo>());

            return new EndpointDetailDto(services, processes, disks, software, errors, warnings);
        }, ct);

    private static List<ProcessInfo> ReadProcesses(CimSession session, CimOperationOptions op, CancellationToken ct)
    {
        var cpu = new Dictionary<int, double>();
        try
        {
            var cores = Query(session, op, "SELECT NumberOfLogicalProcessors FROM Win32_ComputerSystem").FirstOrDefault();
            var count = Num(cores, "NumberOfLogicalProcessors");
            if (count is > 0)
                foreach (var p in Query(session, op, "SELECT IDProcess, PercentProcessorTime FROM Win32_PerfFormattedData_PerfProc_Process"))
                    if (Num(p, "IDProcess") is > 0 and <= int.MaxValue && Num(p, "PercentProcessorTime") is { } value)
                        cpu[(int)Num(p, "IDProcess")!.Value] = Math.Clamp(value / count.Value, 0, 100);
        }
        catch (CimException) { } // A provider may not expose performance counters.
        var rows = Query(session, op, "SELECT ProcessId, Name, WorkingSetSize, Handle FROM Win32_Process");
        var ownerBudget = System.Diagnostics.Stopwatch.StartNew();
        using var ownerOptions = new CimOperationOptions { Timeout = TimeSpan.FromSeconds(1) };
        var result = new List<ProcessInfo>();
        foreach (var p in rows)
        {
            ct.ThrowIfCancellationRequested();
            var pid = (int)(Num(p, "ProcessId") ?? 0);
            string? owner = null;
            if (ownerBudget.Elapsed < TimeSpan.FromSeconds(5))
                try
                {
                    var r = session.InvokeMethod(Ns, p, "GetOwner", null, ownerOptions);
                    if (Convert.ToUInt32(r.ReturnValue.Value, System.Globalization.CultureInfo.InvariantCulture) == 0
                        && r.OutParameters["User"]?.Value is string user && !string.IsNullOrWhiteSpace(user))
                        owner = r.OutParameters["Domain"]?.Value is string domain && domain.Length > 0 ? domain + "\\" + user : user;
                }
                catch (CimException) { }
            result.Add(new ProcessInfo(pid, Str(p, "Name") ?? "", (long)(Num(p, "WorkingSetSize") ?? 0), owner, cpu.TryGetValue(pid, out var load) ? load : null));
        }
        return result.OrderByDescending(p => p.WorkingSetBytes).ToList();
    }

    public Task<IReadOnlyList<ServiceInfo>> CollectServicesAsync(string host, CancellationToken ct) =>
        Task.Run<IReadOnlyList<ServiceInfo>>(() =>
        {
            using var session = Open(host);
            ct.ThrowIfCancellationRequested();
            return Query(session, Operation(), "SELECT Name, DisplayName, State, StartMode FROM Win32_Service")
                .Select(s => new ServiceInfo(Cap(Str(s, "Name")) ?? "", Cap(Str(s, "DisplayName")) ?? "", Str(s, "State") ?? "", Str(s, "StartMode") ?? ""))
                .ToList();
        }, ct);

    private static string? Cap(string? v) => v is { Length: > 256 } ? v[..256] : v;

    private const int MaxDependentDepth = 16, MaxDependents = 500;

    /// <summary>Read-only, like the helper's own lookup: ASSOCIATORS over Win32_DependentService, level by level, each service once.</summary>
    public Task<IReadOnlyList<DependentService>> CollectDependentsAsync(string host, string serviceName, CancellationToken ct) =>
        Task.Run<IReadOnlyList<DependentService>>(() =>
        {
            if (!ServiceNameRules.IsValid(serviceName)) throw new ArgumentException("Invalid service name.", nameof(serviceName));
            using var session = Open(host);
            var op = Operation();
            var found = new List<DependentService>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { serviceName.Trim() };
            var level = new List<string> { serviceName.Trim() };
            for (var depth = 0; level.Count > 0; depth++)
            {
                if (depth >= MaxDependentDepth) throw new InvalidOperationException("The dependency tree is too deep.");
                var next = new List<string>();
                foreach (var parent in level)
                {
                    ct.ThrowIfCancellationRequested();
                    // The name passed the service-name pattern (no quote, no backslash); the quote doubling is belt and braces.
                    var wql = $"ASSOCIATORS OF {{Win32_Service.Name='{parent.Replace("'", "''", StringComparison.Ordinal)}'}} WHERE AssocClass=Win32_DependentService Role=Antecedent ResultRole=Dependent";
                    foreach (var d in Query(session, op, wql))
                    {
                        var name = Cap(Str(d, "Name"));
                        if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                        if (found.Count >= MaxDependents) throw new InvalidOperationException("Too many dependent services.");
                        found.Add(new DependentService(name, Str(d, "State") ?? ""));
                        if (ServiceNameRules.IsValid(name)) next.Add(name);   // a name that fails validation is listed but never put in a query
                    }
                }
                level = next;
            }
            return found;
        }, ct);

    private CimSession Open(string host)
    {
        if (IsLocal(host)) return CimSession.Create(null);
        return CimSession.Create(host, new DComSessionOptions { Timeout = _timeout });
    }

    private CimOperationOptions Operation() => new() { Timeout = _timeout };

    private static bool IsLocal(string host) => LocalHost.Is(host, Environment.MachineName, LocalHost.Addresses.Value);

    private static List<CimInstance> Query(CimSession session, CimOperationOptions op, string wql) =>
        session.QueryInstances(Ns, "WQL", wql, op).ToList();

    private static string? Str(CimInstance? i, string name) =>
        i?.CimInstanceProperties[name]?.Value is { } v ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;

    private static double? Num(CimInstance? i, string name)
    {
        var v = i?.CimInstanceProperties[name]?.Value;
        return v is null ? null : Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static double? Round(double? v) => v is null ? null : Math.Round(v.Value, 1);

    /// <summary>Win32_ComputerSystem.UserName is empty for RDP-only sessions; the explorer.exe owner is the signed-in user.</summary>
    private static string? ExplorerOwner(CimSession session, CimOperationOptions op)
    {
        foreach (var p in session.QueryInstances(Ns, "WQL", "SELECT Handle FROM Win32_Process WHERE Name = 'explorer.exe'", op))
        {
            var r = session.InvokeMethod(Ns, p, "GetOwner", null, op);
            var user = r.OutParameters["User"]?.Value as string;
            var domain = r.OutParameters["Domain"]?.Value as string;
            if (!string.IsNullOrEmpty(user)) return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
        }
        return null;
    }

    private static DateTime? Boot(CimInstance? os) =>
        os?.CimInstanceProperties["LastBootUpTime"]?.Value switch
        {
            DateTime dt => dt.ToUniversalTime(),
            string s => HardwareMapping.ParseCimDateTime(s),   // a CIM datetime delivered as text
            _ => null,
        };

    private static List<SoftwareInfo> ReadSoftware(CimSession session, CancellationToken ct)
    {
        var result = new List<SoftwareInfo>();
        foreach (var root in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        })
        {
            var keys = RegCall(session, "EnumKey", root, null)?.OutParameters["sNames"]?.Value as string[] ?? [];
            foreach (var key in keys.Take(600))
            {
                ct.ThrowIfCancellationRequested();
                var path = root + "\\" + key;
                var name = RegString(session, path, "DisplayName");
                if (string.IsNullOrWhiteSpace(name)) continue;
                result.Add(new SoftwareInfo(name, RegString(session, path, "DisplayVersion"), RegString(session, path, "Publisher")));
            }
        }
        return result.DistinctBy(s => (s.Name, s.Version)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? RegString(CimSession session, string path, string value) =>
        RegCall(session, "GetStringValue", path, value)?.OutParameters["sValue"]?.Value as string;

    private static CimMethodResult? RegCall(CimSession session, string method, string subKey, string? valueName)
    {
        var p = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("hDefKey", HklmKey, CimType.UInt32, CimFlags.In),
            CimMethodParameter.Create("sSubKeyName", subKey, CimType.String, CimFlags.In),
        };
        if (valueName is not null)
            p.Add(CimMethodParameter.Create("sValueName", valueName, CimType.String, CimFlags.In));
        var r = session.InvokeMethod(Ns, "StdRegProv", method, p);
        return Convert.ToInt32(r.ReturnValue?.Value ?? -1, System.Globalization.CultureInfo.InvariantCulture) == 0 ? r : null;
    }

    private List<EventInfo> ReadErrors(CimSession session, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-24).ToString("yyyyMMddHHmmss'.000000+000'");
        var events = new List<EventInfo>();
        foreach (var log in new[] { "System", "Application" })
        {
            ct.ThrowIfCancellationRequested();
            var wql = $"SELECT EventCode, SourceName, TimeGenerated, Message, Logfile FROM Win32_NTLogEvent " +
                      $"WHERE Logfile = '{log}' AND EventType = 1 AND TimeGenerated > '{since}'";
            foreach (var e in session.QueryInstances(Ns, "WQL", wql, Operation()).Take(40))
            {
                var msg = Str(e, "Message") ?? "";
                events.Add(new EventInfo(
                    e.CimInstanceProperties["TimeGenerated"]?.Value is DateTime t ? t.ToUniversalTime() : DateTime.MinValue,
                    Str(e, "SourceName") ?? "", (int)(Num(e, "EventCode") ?? 0), log,
                    msg.Length > 400 ? msg[..400] + "..." : msg));
            }
        }
        return events.OrderByDescending(e => e.TimeUtc).Take(60).ToList();
    }
}

/// <summary>Whether a host name or address means THIS computer: the machine name, its FQDN or short name, localhost, or any of its IP addresses.</summary>
public static class LocalHost
{
    public static readonly Lazy<HashSet<string>> Addresses = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                foreach (var u in nic.GetIPProperties().UnicastAddresses) set.Add(u.Address.ToString());
        }
        catch (Exception) { /* best effort */ }
        return set;
    });

    public static bool Is(string? host, string machineName, IReadOnlySet<string> localAddresses)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim();
        if (host == "." || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "::1") return true;
        if (localAddresses.Contains(host)) return true;
        return string.Equals(host.Split('.')[0], machineName.Split('.')[0], StringComparison.OrdinalIgnoreCase)
               && !System.Net.IPAddress.TryParse(host, out _);
    }
}
