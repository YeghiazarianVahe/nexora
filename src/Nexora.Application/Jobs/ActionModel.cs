using Nexora.Domain;

namespace Nexora.Application.Jobs;

/// <summary>Category keys of the action catalog. The first six are the groups of the Remote Actions grid; the rest live only in their own tabs.</summary>
public static class ActionCategories
{
    public const string PowerSession = "power-session";
    public const string HealthRepair = "health-repair";
    public const string UpdatesSecurity = "updates-security";
    public const string NetworkPolicy = "network-policy";
    public const string Diagnostics = "diagnostics";
    public const string AdminScripts = "admin-scripts";
    public const string Services = "services";
    public const string Processes = "processes";
    public const string Profiles = "profiles";

    /// <summary>Categories listed in the Remote Actions grid.</summary>
    public static readonly IReadOnlyList<string> Grid = [PowerSession, HealthRepair, UpdatesSecurity, NetworkPolicy, Diagnostics, AdminScripts];

    /// <summary>Every valid key, grid and non-grid.</summary>
    public static readonly IReadOnlyList<string> All = [.. Grid, Services, Processes, Profiles];
}

public enum RiskLevel { Low = 0, Medium = 1, High = 2, Critical = 3 }

/// <summary>How the helper reaches the target. No WinRM anywhere: only WMI/DCOM, remote Task Scheduler, admin shares and local tools.</summary>
public enum ExecutionMethod { Wmi, SystemTask, AdminShare, Local }

public enum ParamType { String, Int, Bool, Enum, StringList }

public sealed record ParameterSpec(
    string Name, ParamType Type, string Label, bool Required = false,
    int? Min = null, int? Max = null, int MaxLength = 255, string? Pattern = null,
    IReadOnlyList<string>? Allowed = null, string? Default = null, string? Help = null,
    /// <summary>Values that are refused even though they match the pattern (critical services, system processes).</summary>
    IReadOnlyList<string>? Deny = null);

/// <summary>Maps a process exit code to a target state and a human summary.</summary>
public sealed record ExitCodeRule(int Code, TargetState State, string Summary);

/// <summary>
/// One allowlisted, typed action. Defined in code (never in the database or the client). <see cref="Implemented"/>
/// stays false until the helper has an executor for it; unimplemented actions are visible in the catalog API but cannot run.
/// </summary>
public sealed record ActionDefinition(
    string Code, string DisplayName, string Category, RiskLevel Risk, Role MinRole, ExecutionMethod Method,
    IReadOnlyList<ParameterSpec> Parameters, TimeSpan Timeout,
    string Description = "",
    bool SupportsCancel = true, bool SupportsBulk = true, bool RequiresUserNotice = false,
    /// <summary>Id of the output parser applied to the collected output (for example "sfc", "dism", "chkdsk").</summary>
    string? OutputParser = null,
    IReadOnlyList<ExitCodeRule>? ExitCodes = null,
    /// <summary>sfc, DISM, chkdsk, updates: at most one per target at a time.</summary>
    bool Heavy = false,
    /// <summary>Power-type actions are blocked on protected endpoints regardless of risk level.</summary>
    bool IsPower = false,
    bool Implemented = false,
    /// <summary>Simulated actions that exercise the pipeline; listed only while Features:DemoActions is on (off by default).</summary>
    bool Demo = false,
    /// <summary>Also checked against protected workstations (the Nexora server, server OS, the protected list) even when below High risk.</summary>
    bool Protect = false,
    /// <summary>The technical command behind the friendly name, shown as a muted sub-label so operators learn it (for example "sfc /scannow").</summary>
    string? Command = null)
{
    /// <summary>Protected workstations need an Admin override with a reason: power actions, High and Critical risk, and anything marked Protect.</summary>
    public bool ChecksProtected => IsPower || Risk >= RiskLevel.High || Protect;

    public IReadOnlyList<ExitCodeRule> ExitCodeMap { get; } = ExitCodes ?? [];

    public bool NeedsConfirmation => NeedsConfirmationAt(Risk);
    public bool NeedsReason => NeedsReasonAt(Risk);
    public static bool NeedsReasonAt(RiskLevel risk) => risk >= RiskLevel.High;
    public bool NeedsTypedConfirmation => NeedsTypedConfirmationAt(Risk);

    /// <summary>The confirmation tiers by risk, shared with the escalation hook so the catalog and the effective risk use one rule.</summary>
    public static bool NeedsConfirmationAt(RiskLevel risk) => risk >= RiskLevel.Medium;
    public static bool NeedsTypedConfirmationAt(RiskLevel risk) => risk >= RiskLevel.High;

    /// <summary>Maps an exit code, falling back to 0 = success, anything else = failed.</summary>
    public (TargetState State, string Summary) MapExit(int code)
    {
        var rule = ExitCodeMap.FirstOrDefault(r => r.Code == code);
        if (rule is not null) return (rule.State, rule.Summary);
        return code == 0 ? (TargetState.Succeeded, "Completed.") : (TargetState.Failed, $"Exited with code {code}.");
    }
}
