namespace Nexora.Contracts;

/// <summary>
/// Names the helper must never touch, whatever the operator's rights are. The service list is the fixed, non-removable floor: Admins can add
/// to it in Settings (ProtectedServices) but never remove from it. Services operators routinely restart (Spooler, Dhcp, W32Time, BITS, wuauserv...) are deliberately not here.
/// </summary>
public static class ProtectedNames
{
    public static readonly IReadOnlyList<string> Services =
    [
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "WinDefend", "SamSs", "Winmgmt", "EventLog", "Schedule", "PlugPlay",
        "Power", "ProfSvc", "gpsvc", "BFE", "mpssvc", "LanmanServer", "LanmanWorkstation", "CryptSvc", "TrustedInstaller",
        "Netlogon", "Dnscache", "nsi", "KeyIso",
        "NlaSvc", "netprofm", "AppIDSvc", "EventSystem", "Wcmsvc", "Sense", "WdNisSvc", "wscsvc", "SecurityHealthService", "Sysmon", "Sysmon64",
    ];

    /// <summary>True when the service is on the fixed floor (trimmed, case-insensitive).</summary>
    public static bool IsProtectedService(string? name) =>
        name is not null && Services.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Services Nexora never starts: they open remote-management doors (Nexora must never enable WinRM, CLAUDE.md constraint 4). Stopping them is fine.
    /// </summary>
    public static readonly IReadOnlyList<string> NoStartServices = ["WinRM", "RemoteRegistry", "sshd", "TlntSvr"];

    public static bool IsNoStartService(string? name) =>
        name is not null && NoStartServices.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyList<string> Processes =
    [
        "csrss", "csrss.exe", "lsass", "lsass.exe", "wininit", "wininit.exe", "smss", "smss.exe", "services", "services.exe",
        "winlogon", "winlogon.exe", "system", "registry", "memory compression", "system idle process",
    ];

    /// <summary>
    /// Folder names under the profile root that are never an ordinary user's profile (default, public and system profiles). The server hides these
    /// from the profile list and the helper refuses them again at execution time.
    /// </summary>
    public static readonly IReadOnlyList<string> SystemProfileFolders =
        ["Default", "Default User", "Public", "All Users", "defaultuser0", "systemprofile", "LocalService", "NetworkService", "Default.migrated",
         "SYSTEM", "LOCAL SERVICE", "NETWORK SERVICE", "WDAGUtilityAccount", "DefaultAccount"];
}
