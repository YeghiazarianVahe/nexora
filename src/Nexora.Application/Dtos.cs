using Nexora.Contracts;
using Nexora.Domain;

namespace Nexora.Application;

public sealed record DirectoryUserDto(string Sid, string Account, string DisplayName);

public sealed record AccessRequestDto(
    Guid Id, string Sid, string Account, string DisplayName, string Status,
    DateTime RequestedUtc, DateTime? DecidedUtc, string? GrantedRole, string? RejectReason);

public sealed record UserDto(
    string Sid, string Account, string DisplayName, string Role, string Status,
    DateTime CreatedUtc, DateTime? LastSeenUtc, string? LastAuthScheme, bool IsSuperAdmin);

/// <param name="Hostname">The address used to reach the PC: an IP or a host name.</param>
/// <param name="Name">Friendly workstation name (falls back to the address).</param>
public sealed record EndpointSummaryDto(
    Guid Id, string Hostname, string Name, string? ReportedHostname, string? IpAddress, string? LastUser,
    string? OsCaption, string? OsVersion,
    long? UptimeSeconds, DateTime? LastSeenUtc, bool Online,
    double? CpuPercent, double? RamPercent, double? DiskPercent,
    IReadOnlyList<Guid> GroupIds, string? DiskHealth, string? LastError, IReadOnlyList<string> Alerts,
    int? DiskHealthPercent = null, string? Manufacturer = null, string? Model = null, string? OrganizationalUnit = null,
    bool IsPinned = false,
    /// <summary>Worst health among the SSD/NVMe drives, and among the HDDs; null when that kind is absent.</summary>
    int? SsdHealthPercent = null, int? HddHealthPercent = null,
    long? RamTotalBytes = null, IReadOnlyList<VolumeDto>? Volumes = null,
    /// <summary>Set while the drive scan of this PC keeps failing (the CPU/RAM figures are still current).</summary>
    string? DiskScanError = null,
    /// <summary>"found", "notFound", "failed" or null (never checked) for <see cref="OrganizationalUnit"/>; OuError says why when failed.</summary>
    string? OuStatus = null, string? OuError = null,
    /// <summary>Live RAM from the last poll (additive): used = total - free; null until a poll reported it.</summary>
    long? RamUsedBytes = null, long? RamFreeBytes = null);

/// <summary>A fixed volume as of the last poll.</summary>
public sealed record VolumeDto(string Letter, string? FileSystem, long TotalBytes, long FreeBytes);

/// <summary>Alert and health thresholds every screen colours by (one source, from Settings and the health evaluator).</summary>
public sealed record ThresholdsDto(
    int CpuAlertPercent, int RamAlertPercent, int DiskAlertPercent, int HealthOkFrom, int HealthCriticalBelow,
    /// <summary>Default drive temperature limits per class (Celsius); a limit the drive reports itself takes precedence (see the drive's temperature.source).</summary>
    int DiskTempHddWarnC = 50, int DiskTempHddCritC = 55, int DiskTempSsdWarnC = 60, int DiskTempSsdCritC = 70, int DiskTempNvmeWarnC = 70, int DiskTempNvmeCritC = 80);

public sealed record DashboardDto(
    int Total, int Online, int Offline, int CpuAlerts, int RamAlerts, int DiskAlerts, int DiskHealthAlerts,
    IReadOnlyList<EndpointSummaryDto> AlertEndpoints, IReadOnlyList<AuditEntryDto> RecentActions,
    /// <summary>How many workstations have at least one alert (AlertEndpoints shows the worst 50 of them).</summary>
    int AlertEndpointTotal = 0);

public sealed record MetricPoint(DateTime TimeUtc, double? Cpu, double? Ram, double? Disk);

public sealed record FleetPoint(DateTime TimeUtc, double? AvgCpu, double? AvgRam, int Online);

public sealed record GroupDto(Guid Id, string Name, string Description, IReadOnlyList<Guid> MemberIds, int Online);

public sealed record GroupInput(string Name, string? Description);

public sealed record EndpointInput(string? Name, string Address);

/// <param name="LastUseSource">Where <paramref name="LastUseUtc"/> came from: "ProfileList", "NtUserDat" or "Wmi"; null when unknown.</param>
public sealed record ProfileInfo(
    string Sid, string? Account, string LocalPath, DateTime? LastUseUtc, bool Loaded, bool Special, bool Roaming, string? Status, string? LastUseSource = null);

public sealed record AuditEntryDto(
    long Id, DateTime TimestampUtc, string ActorSid, string ActorAccount, string Action,
    string? TargetType, string? TargetId, string? OldValue, string? NewValue, string? ClientIp,
    string? Details, bool Success);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed record AuditQuery(
    string? ActorSid = null, string? Action = null, string? Text = null,
    DateTime? FromUtc = null, DateTime? ToUtc = null, int Page = 1, int PageSize = 50);

public sealed record ScriptDto(Guid Id, string Name, string Description, string Body, string Sha256, bool Enabled, DateTime UpdatedUtc);

public sealed record ScriptInput(string Name, string Description, string Body, bool Enabled);


public sealed class PlatformSettings
{
    public int PollIntervalSeconds { get; set; } = 60;
    public int MaxConcurrency { get; set; } = 32;
    public int CpuAlertPercent { get; set; } = 90;
    public int RamAlertPercent { get; set; } = 90;
    public int DiskAlertPercent { get; set; } = 90;
    public int OfflineAfterFailures { get; set; } = 2;
    public int MetricRetentionDays { get; set; } = 7;
    public int DiskPollMinutes { get; set; } = 10;
    /// <summary>How many targets of one bulk job run at the same time.</summary>
    public int JobParallelism { get; set; } = 10;

    // Default drive temperature limits in Celsius, one pair per drive class (D4: global per class). A limit the drive reports itself (NVMe) wins.
    // A document that omits one of these (an older client) leaves it unspecified, and a save then keeps the stored value (see KeepStoredTempLimits).
    private int?[] _temps = new int?[6];
    public int DiskTempHddWarnC { get => _temps[0] ?? 50; set => _temps[0] = value; }
    public int DiskTempHddCritC { get => _temps[1] ?? 55; set => _temps[1] = value; }
    public int DiskTempSsdWarnC { get => _temps[2] ?? 60; set => _temps[2] = value; }
    public int DiskTempSsdCritC { get => _temps[3] ?? 70; set => _temps[3] = value; }
    public int DiskTempNvmeWarnC { get => _temps[4] ?? 70; set => _temps[4] = value; }
    public int DiskTempNvmeCritC { get => _temps[5] ?? 80; set => _temps[5] = value; }

    /// <summary>For every temperature limit this document did not mention, takes the stored value.</summary>
    public void KeepStoredTempLimits(PlatformSettings stored)
    {
        if (_temps[0] is null) DiskTempHddWarnC = stored.DiskTempHddWarnC;
        if (_temps[1] is null) DiskTempHddCritC = stored.DiskTempHddCritC;
        if (_temps[2] is null) DiskTempSsdWarnC = stored.DiskTempSsdWarnC;
        if (_temps[3] is null) DiskTempSsdCritC = stored.DiskTempSsdCritC;
        if (_temps[4] is null) DiskTempNvmeWarnC = stored.DiskTempNvmeWarnC;
        if (_temps[5] is null) DiskTempNvmeCritC = stored.DiskTempNvmeCritC;
    }

    public Services.DriveTempLimits ToTempLimits() =>
        new(DiskTempHddWarnC, DiskTempHddCritC, DiskTempSsdWarnC, DiskTempSsdCritC, DiskTempNvmeWarnC, DiskTempNvmeCritC);

    /// <summary>Host names where power and Critical actions are blocked unless an Admin overrides with a reason. Server operating systems and the Nexora server are always protected.</summary>
    public List<string> ProtectedHosts { get; set; } = ["wks-111"];

    /// <summary>Services that cannot be stopped or restarted from Nexora, in addition to the fixed floor (<see cref="Nexora.Contracts.ProtectedNames.Services"/>). Admin-edited.</summary>
    public List<string> ProtectedServices
    {
        get => _protectedServices;
        set { _protectedServices = value; ProtectedServicesSpecified = true; }
    }
    private List<string> _protectedServices = [];

    /// <summary>False when the document never mentioned the list (an older client): a save then keeps the stored list instead of emptying it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ProtectedServicesSpecified { get; private set; }

    /// <summary>
    /// Exchange servers the Outlook diagnosis tests the network path to (host names, at most 5). Admin-edited; an operator can never supply hosts.
    /// A document that does not mention the list keeps the stored one.
    /// </summary>
    public List<string> DiagExchangeHosts
    {
        get => _diagExchangeHosts;
        set { _diagExchangeHosts = value; DiagExchangeHostsSpecified = true; }
    }
    private List<string> _diagExchangeHosts = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public bool DiagExchangeHostsSpecified { get; private set; }

    /// <summary>The fixed, non-removable floor, for display. Read-only: a value sent by a client is ignored.</summary>
    public IReadOnlyList<string> ProtectedServicesFloor => Nexora.Contracts.ProtectedNames.Services;

    /// <summary>Trims and de-duplicates (case-insensitive) the user list and drops entries the floor already covers. Run before <see cref="Validate"/> on save.</summary>
    public void Normalize()
    {
        if (DiagExchangeHosts is not null)
            DiagExchangeHosts = DiagExchangeHosts.Select(h => h?.Trim() ?? "").Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ProtectedServices is null) return;
        ProtectedServices = ProtectedServices.Select(n => n?.Trim() ?? "").Where(n => n.Length > 0 && !Nexora.Contracts.ProtectedNames.IsProtectedService(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string? Validate()
    {
        if (PollIntervalSeconds is < 15 or > 3600) return "PollIntervalSeconds must be 15-3600.";
        if (MaxConcurrency is < 1 or > 256) return "MaxConcurrency must be 1-256.";
        if (CpuAlertPercent is < 1 or > 100) return "CpuAlertPercent must be 1-100.";
        if (RamAlertPercent is < 1 or > 100) return "RamAlertPercent must be 1-100.";
        if (DiskAlertPercent is < 1 or > 100) return "DiskAlertPercent must be 1-100.";
        if (OfflineAfterFailures is < 1 or > 20) return "OfflineAfterFailures must be 1-20.";
        if (MetricRetentionDays is < 1 or > 90) return "MetricRetentionDays must be 1-90.";
        if (DiskPollMinutes is < 5 or > 1440) return "DiskPollMinutes must be 5-1440.";
        if (JobParallelism is < 1 or > 50) return "JobParallelism must be 1-50.";
        foreach (var (name, warn, crit) in new[]
        {
            ("HDD", DiskTempHddWarnC, DiskTempHddCritC), ("SSD", DiskTempSsdWarnC, DiskTempSsdCritC), ("NVMe", DiskTempNvmeWarnC, DiskTempNvmeCritC),
        })
        {
            if (warn is < 20 or > 100 || crit is < 20 or > 100) return $"Disk temperature limits for {name} must be 20-100 °C.";
            if (warn >= crit) return $"The {name} drive temperature warning must be lower than its critical limit.";
        }
        if (ProtectedHosts is null || ProtectedHosts.Count > 500 || ProtectedHosts.Any(h => !Services.HostnameRules.IsValid(h?.Trim())))
            return "ProtectedHosts must be a list of host names (max 500).";
        if (ProtectedServices is null || ProtectedServices.Count > Services.ServiceNameRules.MaxProtected || ProtectedServices.Any(n => !Services.ServiceNameRules.IsValid(n?.Trim())))
            return $"ProtectedServices must be a list of service names (letters, digits, _ . - $ and spaces; max {Services.ServiceNameRules.MaxProtected}).";
        if (DiagExchangeHosts is null || DiagExchangeHosts.Count > Diagnostics.DiagnosticJob.MaxExchangeHosts || DiagExchangeHosts.Any(h => !Services.ExchangeHostRules.IsValid(h)))
            return $"DiagExchangeHosts must be a list of up to {Diagnostics.DiagnosticJob.MaxExchangeHosts} host names.";
        return null;
    }
}

public sealed record DirectoryComputer(string Hostname, string? OperatingSystem, string? OrganizationalUnit = null);

public static class Mapping
{
    public static AccessRequestDto ToDto(this AccessRequest r) =>
        new(r.Id, r.Sid, r.SamAccountName, r.DisplayName, r.Status.ToString(), r.RequestedUtc, r.DecidedUtc,
            r.GrantedRole?.ToString(), r.RejectReason);

    public static AuditEntryDto ToDto(this AuditEntry e) =>
        new(e.Id, e.TimestampUtc, e.ActorSid, e.ActorAccount, e.Action, e.TargetType, e.TargetId,
            e.OldValue, e.NewValue, e.ClientIp, e.Details, e.Success);

    public static ScriptDto ToDto(this ScriptDefinition s) =>
        new(s.Id, s.Name, s.Description, s.Body, s.Sha256, s.Enabled, s.UpdatedUtc);

    /// <summary>
    /// THE definition of a drive-health alert, used by the dashboard card, the "Needs attention" filter, the CSV filter and every list:
    /// the PC is online and its weakest drive is Warning or Critical. (Offline PCs keep their last drive status but raise no alert.)
    /// </summary>
    public static bool HasDriveHealthAlert(bool online, string? worstDriveStatus) =>
        online && worstDriveStatus is Services.DiskStatus.Warning or Services.DiskStatus.Critical;

    public static IReadOnlyList<string> AlertsFor(ManagedEndpoint e, PlatformSettings s)
    {
        var alerts = new List<string>();
        if (!e.Online) return alerts;
        if (e.CpuPercent >= s.CpuAlertPercent) alerts.Add("cpu");
        if (e.RamPercent >= s.RamAlertPercent) alerts.Add("ram");
        if (e.DiskPercent >= s.DiskAlertPercent) alerts.Add("disk");
        return alerts;
    }

    private static IReadOnlyList<VolumeDto>? ParseVolumes(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<VolumeDto>>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static EndpointSummaryDto ToDto(
        this ManagedEndpoint e, PlatformSettings s, DateTime nowUtc,
        IReadOnlyList<Guid>? groupIds = null, string? diskHealth = null, int? diskHealthPercent = null,
        int? ssdHealth = null, int? hddHealth = null)
    {
        var alerts = AlertsFor(e, s).ToList();
        if (HasDriveHealthAlert(e.Online, diskHealth)) alerts.Add("diskhealth");
        return new(e.Id, e.Hostname, string.IsNullOrWhiteSpace(e.DisplayName) ? e.Hostname : e.DisplayName,
            e.ReportedHostname, e.IpAddress, e.LastUser, e.OsCaption, e.OsVersion,
            e.Online && e.BootTimeUtc is { } boot ? (long)(nowUtc - boot).TotalSeconds : null,
            e.LastSeenUtc, e.Online, e.CpuPercent, e.RamPercent, e.DiskPercent,
            groupIds ?? [], diskHealth, e.LastError, alerts, diskHealthPercent, e.Manufacturer, e.Model, e.OrganizationalUnit, e.IsPinned,
            ssdHealth, hddHealth, e.RamTotalBytes, ParseVolumes(e.VolumesJson), e.DiskScanError,
            e.OuLookupStatus ?? (e.OrganizationalUnit is not null ? "found" : null), e.OuLookupStatus == "failed" ? e.OuLookupError : null,
            e.RamTotalBytes is { } rt && e.RamFreeBytes is { } rf && rf >= 0 && rf <= rt ? rt - rf : null, e.RamFreeBytes);
    }
}

/// <summary>The volume the table calls "the system drive": C: when present, else the first fixed volume.</summary>
public static class SystemVolume
{
    public static VolumeDto? Of(IReadOnlyList<VolumeDto>? volumes) =>
        volumes is { Count: > 0 } ? volumes.FirstOrDefault(v => v.Letter.Equals("C:", StringComparison.OrdinalIgnoreCase)) ?? volumes[0] : null;

    /// <summary>Used share of a volume, 0-100 with one decimal; null when the size is unknown.</summary>
    public static double? UsedPercent(VolumeDto? v) =>
        v is { TotalBytes: > 0 } ? Math.Round((1 - (double)v.FreeBytes / v.TotalBytes) * 100, 1) : null;
}
