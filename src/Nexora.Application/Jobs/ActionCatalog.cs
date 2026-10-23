using Nexora.Contracts;
using Nexora.Domain;

namespace Nexora.Application.Jobs;

/// <summary>
/// The complete action catalog, defined in code. Everything is typed and allowlisted: there is no free-form command execution.
/// Only entries with <c>Implemented = true</c> can run; the rest are planned and ship step by step.
/// </summary>
public static class ActionCatalog
{
    private const string HostPattern = @"^[A-Za-z0-9._\-$ ]{1,256}$";
    private const string UncPattern = @"^\\\\[A-Za-z0-9._\-]{1,63}(\\[A-Za-z0-9._\-$ ]{1,100}){1,12}$";

    private static readonly TimeSpan Short = TimeSpan.FromMinutes(5);
    internal const string DemoCategory = ActionCategories.Diagnostics;

    private static ParameterSpec[] DemoParameters =>
    [
        new("steps", ParamType.Int, "Steps", Min: 1, Max: 20, Default: "5"),
        new("delayMs", ParamType.Int, "Delay per step (ms)", Min: 0, Max: 2000, Default: "400"),
        new("outcome", ParamType.Enum, "Outcome", Allowed: ["success", "warning", "fail", "hang"], Default: "success",
            Help: "'hang' never finishes, so you can see a timeout or cancel."),
    ];

    private static ParameterSpec Delay => new("delayMinutes", ParamType.Int, "Delay (minutes)", Min: 0, Max: 60, Default: "0",
        Help: "0 to 60 minutes. The user sees a countdown.");
    private static ParameterSpec UserMessage => new("message", ParamType.String, "Message to the user", MaxLength: 255);
    /// <summary>Start has no deny list: starting a stopped critical service is remediation. Stop and restart refuse the floor (and the server also checks the Admin list).</summary>
    private static ParameterSpec ServiceNameForStart => new("serviceName", ParamType.String, "Service name", Required: true, MaxLength: 256,
        Pattern: @"^[A-Za-z0-9_.\-$ ]{1,256}$", Deny: ProtectedNames.NoStartServices);
    private static ParameterSpec ServiceName => ServiceNameForStart with { Deny = ProtectedNames.Services };

    private static ParameterSpec StopDependents => new("stopDependents", ParamType.Bool, "Also stop dependent services", Default: "false",
        Help: "Stops services that depend on this one first. Off by default.");

    /// <summary>IDs of the compiled diagnostic collectors a job may name. Empty until F5 adds them, so nothing can be requested yet.</summary>
    public static readonly IReadOnlyList<string> CollectorIds = [];

    public const int MaxExchangeHosts = 5;
    private const string ExchangeHostPattern = @"^[A-Za-z0-9](?:[A-Za-z0-9.\-]{0,251}[A-Za-z0-9])?\z";

    /// <summary>An exact-match pattern over a fixed list; an empty list matches nothing.</summary>
    private static string AllowListPattern(IReadOnlyList<string> ids) =>
        ids.Count == 0 ? @"^(?!)\z" : "^(?:" + string.Join("|", ids.Select(System.Text.RegularExpressions.Regex.Escape)) + @")\z";

    private static ActionDefinition D(
        string code, string name, string cat, RiskLevel risk, Role min, ExecutionMethod method, TimeSpan timeout,
        string description, ParameterSpec[]? p = null, bool bulk = true, bool cancel = true, bool notice = false,
        string? parser = null, ExitCodeRule[]? exits = null, bool heavy = false, bool power = false, bool implemented = false, bool demo = false, bool protect = false, string? cmd = null) =>
        new(code, name, cat, risk, min, method, p ?? [], timeout, description, cancel, bulk, notice, parser, exits, heavy, power, implemented, demo, protect, cmd);

    /// <summary>
    /// The maintenance actions the helper runs on the target as SYSTEM through a one-off task (see SystemCommandAction in the helper). A test
    /// keeps this list identical to the helper's own table, so the catalog never offers something the helper would refuse.
    /// </summary>
    public static readonly IReadOnlySet<string> SystemCommandCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "repair.sfc", "repair.dism.check", "repair.dism.scan", "repair.dism.cleanup", "repair.dism.restore",
        "disk.chkdsk.scan", "disk.chkdsk.fix", "system.timesync", "net.gpupdate", "net.gpresult", "net.flushdns", "net.klist", "power.cancel",
        "power.restart", "power.shutdown", "net.diag", "disk.cleanup", "system.pendingreboot", "defender.signature", "defender.quickscan", "svc.spooler.reset", "update.scan", "update.history",
    };

    private static readonly IReadOnlyList<ActionDefinition> Declared =
    [
        // ----- Demo: exercises the whole pipeline (job, helper channel, streaming, progress, cancel, timeout) without touching a PC.
        D("demo.simulate", "Simulate a job (demo)", ActionCategories.Diagnostics, RiskLevel.Low, Role.Operator, ExecutionMethod.Local, TimeSpan.FromSeconds(30),
            "Prints a few lines with progress and finishes with the chosen outcome. Runs inside your helper and changes nothing on the target.",
            DemoParameters, implemented: true, demo: true),
        D("demo.simulate.medium", "Simulate a Medium-risk job (demo)", DemoCategory, RiskLevel.Medium, Role.Operator, ExecutionMethod.Local, TimeSpan.FromSeconds(30),
            "Same as the demo above, but asks for confirmation.", DemoParameters, implemented: true, demo: true),
        D("demo.simulate.high", "Simulate a High-risk power job (demo)", DemoCategory, RiskLevel.High, Role.Operator, ExecutionMethod.Local, TimeSpan.FromSeconds(30),
            "Needs a reason and the typed workstation name, and is blocked on protected workstations like a real power action.", DemoParameters,
            power: true, implemented: true, demo: true),
        D("demo.simulate.critical", "Simulate a Critical heavy job (demo)", DemoCategory, RiskLevel.Critical, Role.Admin, ExecutionMethod.Local, TimeSpan.FromSeconds(30),
            "Admin only. Counts as a heavy job: one at a time per workstation.", DemoParameters, heavy: true, implemented: true, demo: true),

        // ----- Power and session
        D("power.restart", "Restart computer", ActionCategories.PowerSession, RiskLevel.High, Role.Operator, ExecutionMethod.SystemTask, Short,
            "Restarts the PC; warns if a user is signed in", [Delay, UserMessage], notice: true, power: true, cmd: "shutdown /r"),
        D("power.shutdown", "Shut down computer", ActionCategories.PowerSession, RiskLevel.High, Role.Operator, ExecutionMethod.SystemTask, Short,
            "Powers the PC off", [Delay, UserMessage], notice: true, power: true, cmd: "shutdown /s"),
        D("power.cancel", "Cancel pending restart/shutdown", ActionCategories.PowerSession, RiskLevel.Low, Role.Operator, ExecutionMethod.SystemTask, Short,
            "Stops a scheduled restart or shutdown", cmd: "shutdown /a"),
        D("power.wake", "Wake computer", ActionCategories.PowerSession, RiskLevel.Low, Role.Operator, ExecutionMethod.Local, Short,
            "Powers the PC on over the network", power: true, cmd: "Wake-on-LAN"),
        D("session.logoff", "Sign out user", ActionCategories.PowerSession, RiskLevel.High, Role.Operator, ExecutionMethod.Wmi, Short,
            "Signs the logged-on user out", notice: true),
        D("session.lock", "Lock screen", ActionCategories.PowerSession, RiskLevel.Medium, Role.Operator, ExecutionMethod.SystemTask, Short,
            "Locks the workstation"),
        D("session.message", "Send message to user", ActionCategories.PowerSession, RiskLevel.Low, Role.Operator, ExecutionMethod.Local, TimeSpan.FromSeconds(30),
            "Shows a popup message on the PC",
            [new("message", ParamType.String, "Message", Required: true, MaxLength: 255)], implemented: true, cmd: "msg *"),
        D("session.rdp", "Open Remote Desktop", ActionCategories.PowerSession, RiskLevel.Low, Role.Operator, ExecutionMethod.Local, Short,
            "Opens an RDP session from your PC", bulk: false, cancel: false, implemented: true, cmd: "mstsc"),
        D("session.assist", "Offer Remote Assistance", ActionCategories.PowerSession, RiskLevel.Low, Role.Operator, ExecutionMethod.Local, Short,
            "Offers to view/control the user's screen", bulk: false, cancel: false, implemented: true, cmd: "msra"),

        // ----- System health and repair
        D("repair.sfc", "Repair system files", ActionCategories.HealthRepair, RiskLevel.Medium, Role.Operator, ExecutionMethod.SystemTask, TimeSpan.FromMinutes(60),
            "Scans and repairs Windows system files", parser: "sfc", heavy: true, cmd: "sfc /scannow"),
        D("repair.dism.check", "Check component store", ActionCategories.HealthRepair, RiskLevel.Low, Role.Operator, ExecutionMethod.SystemTask, Short,
            "Quick check of the Windows image", parser: "dism", heavy: true,
            exits: [new(0, TargetState.Succeeded, "Completed."), new(-2146498529, TargetState.Failed, "Source files could not be found (0x800f081f).")], cmd: "DISM /CheckHealth"),
        D("repair.dism.scan", "Scan component store", ActionCategories.HealthRepair, RiskLevel.Medium, Role.Operator, ExecutionMethod.SystemTask, TimeSpan.FromMinutes(60),
            "Deeper scan of the Windows image", parser: "dism", heavy: true, cmd: "DISM /ScanHealth"),
        D("repair.dism.restore", "Repair Windows image", ActionCategories.HealthRepair, RiskLevel.High, Role.Operator, ExecutionMethod.SystemTask, TimeSpan.FromMinutes(120),
            "Repairs the Windows image (can take a while)",
            [
                new("source", ParamType.String, "Source (UNC path to install.wim)", Pattern: UncPattern, MaxLength: 260, Help: @"Example: \\server\share\sources\install.wim"),
                new("limitAccess", ParamType.Bool, "Do not use Windows Update (/LimitAccess)", Default: "false"),
            ], parser: "dism", heavy: true, cmd: "DISM /RestoreHealth"),
        D("repair.dism.cleanup", "Clean up component store", ActionCategories.HealthRepair, RiskLevel.Medium, Role.Operator, ExecutionMethod.SystemTask, TimeSpan.FromMinutes(60),
            "Reclaims space from old components", parser: "dism", heavy: true, cmd: "DISM /StartComponentCleanup"),
        D("disk.chkdsk.scan", "Check disk (read-only)", ActionCategories.HealthRepair, RiskLevel.Low, Role.Operator, ExecutionMethod.SystemTask, TimeSpan.FromMinutes(60),
            "Checks the disk without downtime", parser: "chkdsk", heavy: true,
            exits: [new(0, TargetState.Succeeded, "No problems found."), new(1, TargetState.Warning, "Errors were found and fixed."), new(2, TargetState.Warning, "Cleanup was performed."), new(3, TargetState.Failed, "Could not check, or errors were not fixed.")], cmd: "chkdsk /scan"),
        D("disk.chkdsk.fix", "Fix disk at next boot", ActionCategories.HealthRepair, RiskLevel.Critical, Role.Admin, ExecutionMethod.SystemTask, Short,
            "Schedules a full disk repair on reboot",
            [new("restartNow", ParamType.Bool, "Restart now", Default: "false")], notice: true, heavy: true, cmd: "chkdsk /f /r"),
        D("disk.smart", "Check disk health (SMART)", ActionCategories.HealthRepair, RiskLevel.Low, Role.Viewer, ExecutionMethod.Wmi, Short,
            "Reads drive SMART health"),
        D("disk.cleanup", "Free up disk space", ActionCategories.HealthRepair, RiskLevel.Medium, Role.Operator, ExecutionMethod.SystemTask, TimeSpan.FromMinutes(30),
            "Deletes Windows temp files older than 7 days"),
        D("system.pendingreboot", "Pending reboot check", ActionCategories.Diagnostics, RiskLevel.Low, Role.Viewer, ExecutionMethod.Wmi, Short,
            "Shows whether the PC is waiting to reboot"),
        D("system.timesync", "Resync clock", ActionCategories.HealthRepair, RiskLevel.Low, Role.Operator, ExecutionMethod.SystemTask, Short,
            "Fixes the system time", cmd: "w32tm /resync"),

        // ----- Windows Update and security
        D("update.scan", "Check for updates", ActionCategories.UpdatesSecurity, RiskLevel.Low, Role.Operator, ExecutionMethod.SystemTask, TimeSpan.FromMinutes(30),
            "Lists pending Windows updates", parser: "wua-search", heavy: true),
        D("update.install", "Install updates", ActionCategories.UpdatesSecurity, RiskLevel.Critical, Role.Admin, ExecutionMethod.SystemTask, TimeSpan.FromMinutes(120),
            "Installs selected or all pending updates",
            [
                new("kbs", ParamType.StringList, "KB numbers (empty = all)", Pattern: @"(?i)^KB\d{6,8}$", Help: "Comma separated, for example KB5040442, KB5039212"),
                new("rebootPolicy", ParamType.Enum, "Reboot", Allowed: ["never", "ifRequired"], Default: "never"),
                new("rebootDelayMinutes", ParamType.Int, "Reboot delay (minutes)", Min: 0, Max: 60, Default: "5"),
            ], notice: true, parser: "wua-install", heavy: true),
        D("update.history", "Update history", ActionCategories.Diagnostics, RiskLevel.Low, Role.Viewer, ExecutionMethod.SystemTask, Short,
            "Shows recently installed updates", parser: "wua-history"),
        D("update.reset", "Reset Windows Update", ActionCategories.UpdatesSecurity, RiskLevel.High, Role.Admin, ExecutionMethod.SystemTask, TimeSpan.FromMinutes(15),
            "Repairs a stuck Windows Update", heavy: true),
        D("defender.status", "Antivirus status", ActionCategories.UpdatesSecurity, RiskLevel.Low, Role.Viewer, ExecutionMethod.Wmi, Short,
            "Shows Microsoft Defender status"),
        D("defender.signature", "Update antivirus definitions", ActionCategories.UpdatesSecurity, RiskLevel.Low, Role.Operator, ExecutionMethod.SystemTask, Short,
            "Updates Defender signatures"),
        D("defender.quickscan", "Quick antivirus scan", ActionCategories.UpdatesSecurity, RiskLevel.Medium, Role.Operator, ExecutionMethod.SystemTask, TimeSpan.FromMinutes(30),
            "Runs a Defender quick scan"),
        D("security.bitlocker", "Encryption status", ActionCategories.UpdatesSecurity, RiskLevel.Low, Role.Viewer, ExecutionMethod.Wmi, Short,
            "Shows BitLocker status"),
        D("security.localadmins", "Local administrators", ActionCategories.UpdatesSecurity, RiskLevel.Low, Role.Viewer, ExecutionMethod.Wmi, Short,
            "Lists local admin accounts"),

        // ----- Network and policy
        D("net.gpupdate", "Apply group policy", ActionCategories.NetworkPolicy, RiskLevel.Low, Role.Operator, ExecutionMethod.SystemTask, Short, "Refreshes Group Policy now", cmd: "gpupdate /force"),
        D("net.gpresult", "Group policy report", ActionCategories.NetworkPolicy, RiskLevel.Low, Role.Viewer, ExecutionMethod.SystemTask, Short,
            "Shows applied policies", cmd: "gpresult /r"),
        D("net.flushdns", "Flush DNS cache", ActionCategories.NetworkPolicy, RiskLevel.Low, Role.Operator, ExecutionMethod.SystemTask, Short, "Clears the DNS cache", cmd: "ipconfig /flushdns"),
        D("net.klist", "Purge Kerberos tickets", ActionCategories.NetworkPolicy, RiskLevel.Medium, Role.Operator, ExecutionMethod.SystemTask, Short,
            "Clears cached Kerberos tickets", cmd: "klist purge"),
        D("net.diag", "Network diagnostics", ActionCategories.Diagnostics, RiskLevel.Low, Role.Viewer, ExecutionMethod.SystemTask, Short,
            "Checks connectivity, DNS and domain reachability"),

        // ----- Services, processes and software
        D("svc.list", "List services", ActionCategories.Services, RiskLevel.Low, Role.Viewer, ExecutionMethod.Wmi, Short, "Read-only list of services with state and start type"),
        D("svc.start", "Start service", ActionCategories.Services, RiskLevel.Low, Role.Operator, ExecutionMethod.Wmi, Short,
            "Controls a Windows service", [ServiceNameForStart], implemented: true),
        D("svc.stop", "Stop service", ActionCategories.Services, RiskLevel.Medium, Role.Operator, ExecutionMethod.Wmi, Short,
            "Controls a Windows service", [ServiceName, StopDependents], implemented: true, protect: true),
        D("svc.restart", "Restart service", ActionCategories.Services, RiskLevel.Medium, Role.Operator, ExecutionMethod.Wmi, Short,
            "Controls a Windows service", [ServiceName, StopDependents], implemented: true, protect: true),
        D("svc.spooler.reset", "Restart print spooler", ActionCategories.HealthRepair, RiskLevel.Medium, Role.Operator, ExecutionMethod.SystemTask, Short,
            "Clears the print queue and restarts the spooler"),
        D("proc.list", "List processes", ActionCategories.Processes, RiskLevel.Low, Role.Viewer, ExecutionMethod.Wmi, Short, "Read-only list of running processes"),
        D("proc.kill", "Stop process", ActionCategories.Processes, RiskLevel.Medium, Role.Operator, ExecutionMethod.Wmi, Short,
            "Stops a running process",
            [
                new("pid", ParamType.Int, "Process id", Required: true, Min: 5, Max: 4_194_304),
                new("processName", ParamType.String, "Process name", Required: true, MaxLength: 128, Pattern: @"^[A-Za-z0-9_.\- ]{1,128}$", Deny: ProtectedNames.Processes),
            ], bulk: false, implemented: true, protect: true),
        D("sw.inventory", "Installed software", ActionCategories.Diagnostics, RiskLevel.Low, Role.Viewer, ExecutionMethod.Wmi, Short, "Read-only list of installed software"),
        D("sw.startup", "Startup items", ActionCategories.Diagnostics, RiskLevel.Low, Role.Viewer, ExecutionMethod.Wmi, Short,
            "Read-only list of startup items"),
        D("diag.eventlog", "Recent errors", ActionCategories.Diagnostics, RiskLevel.Low, Role.Viewer, ExecutionMethod.Wmi, Short, "Shows recent System/Application event-log errors"),
        D("diag.bundle", "Collect diagnostics bundle", ActionCategories.Diagnostics, RiskLevel.Medium, Role.Operator, ExecutionMethod.AdminShare, TimeSpan.FromMinutes(20),
            "Gathers logs into a downloadable zip", heavy: true),
        // Not implemented until the helper executor (S0c) and the compiled collectors (F5) exist: hidden from the catalog and refused.
        D("diag.collect", "Collect diagnostics", ActionCategories.Diagnostics, RiskLevel.Low, Role.Operator, ExecutionMethod.SystemTask, TimeSpan.FromMinutes(15),
            "Runs approved read-only collectors on one workstation",
            [
                new("collectorIds", ParamType.StringList, "Collectors", Pattern: AllowListPattern(CollectorIds), Help: "Only collectors compiled into the helper can be chosen."),
                new("exchangeHosts", ParamType.StringList, "Exchange hosts (max 5)", Max: MaxExchangeHosts, Pattern: ExchangeHostPattern),
            ], bulk: false),

        // ----- User profiles
        D("profile.sizes", "Measure user profile sizes", ActionCategories.Profiles, RiskLevel.Low, Role.Operator, ExecutionMethod.AdminShare, TimeSpan.FromMinutes(20),
            "Adds up each profile folder's size (read-only)",
            [new("sid", ParamType.String, "Only this profile (SID)", Pattern: @"^S-1-\d{1,3}(-\d{1,10}){2,15}$", MaxLength: 184,
                 Help: "Leave empty to measure every profile on the workstation.")],
            bulk: false, implemented: true),

        D("profile.delete", "Delete user profile", ActionCategories.Profiles, RiskLevel.Critical, Role.Admin, ExecutionMethod.Wmi, Short,
            "Removes a user's local profile (Admin only; type the workstation name)",
            [
                new("sid", ParamType.String, "Profile SID", Required: true, MaxLength: 184, Pattern: @"^S-1-(5-21|12-1)(-\d{1,10}){3,15}$"),
                new("localPath", ParamType.String, "Profile folder", Required: true, MaxLength: 80, Pattern: @"^[A-Za-z]:\\Users\\(?=[A-Za-z0-9_-])(?!.*[. ]\z)[A-Za-z0-9._ -]{1,64}\z"),
            ], bulk: false, cancel: false, implemented: true),

        // ----- Admin scripts
        D("script.run", "Run catalog script", ActionCategories.AdminScripts, RiskLevel.Critical, Role.Admin, ExecutionMethod.SystemTask, TimeSpan.FromMinutes(30),
            "Runs an approved script from the Admin catalog",
            [new("scriptId", ParamType.String, "Script", Required: true, Pattern: @"^[0-9a-fA-F-]{36}$", MaxLength: 36)], notice: true, implemented: true),
    ];

    public static readonly IReadOnlyList<ActionDefinition> All = Declared.Select(a => SystemCommandCodes.Contains(a.Code) ? a with { Implemented = true } : a).ToList();

    private static readonly Dictionary<string, ActionDefinition> ByCode = All.ToDictionary(a => a.Code, StringComparer.Ordinal);

    public static ActionDefinition? Find(string? code) => code is not null && ByCode.TryGetValue(code, out var d) ? d : null;
}
