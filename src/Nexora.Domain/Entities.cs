namespace Nexora.Domain;

public enum UserStatus
{
    Active = 1,
    Revoked = 2,
}

public sealed class User
{
    /// <summary>Primary key: the Windows SID string (uppercase).</summary>
    public string Sid { get; set; } = "";
    public string SamAccountName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public Role Role { get; set; } = Role.Viewer;
    public UserStatus Status { get; set; } = UserStatus.Active;
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    /// <summary>Kerberos, NTLM or Negotiate, as last observed.</summary>
    public string? LastAuthScheme { get; set; }
}

public sealed class AccessRequest
{
    public Guid Id { get; set; }
    public string Sid { get; set; } = "";
    public string SamAccountName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public AccessRequestStatus Status { get; set; } = AccessRequestStatus.Pending;
    public DateTime RequestedUtc { get; set; }
    public string? DecidedBySid { get; set; }
    public DateTime? DecidedUtc { get; set; }
    public Role? GrantedRole { get; set; }
    public string? RejectReason { get; set; }
}

/// <summary>Append-only. The database also blocks UPDATE and DELETE on this table.</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string ActorSid { get; set; } = "";
    public string ActorAccount { get; set; } = "";
    public string Action { get; set; } = "";
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? ClientIp { get; set; }
    public string? Details { get; set; }
    public bool Success { get; set; } = true;
}

public sealed class ManagedEndpoint
{
    /// <summary>PostgreSQL xmin optimistic concurrency token; not a user-editable address or API field.</summary>
    public uint RowVersion { get; set; }
    public Guid Id { get; set; }
    public string Hostname { get; set; } = "";
    public string? IpAddress { get; set; }
    public string? LastUser { get; set; }
    public string? OsCaption { get; set; }
    public string? OsVersion { get; set; }
    public DateTime? BootTimeUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public DateTime? LastPolledUtc { get; set; }
    public bool Online { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }
    public double? CpuPercent { get; set; }
    public double? RamPercent { get; set; }
    /// <summary>Highest used-percentage across fixed disks.</summary>
    public double? DiskPercent { get; set; }
    /// <summary>Friendly workstation name shown in the UI. Falls back to <see cref="Hostname"/>.</summary>
    public string? DisplayName { get; set; }
    /// <summary>The machine's own name as reported over WMI (useful when it was added by IP).</summary>
    public string? ReportedHostname { get; set; }
    public DateTime? LastDiskPollUtc { get; set; }
    /// <summary>Why the last drive scan could not be stored or read; cleared by the next scan that succeeds. Shown on the PC's page.</summary>
    public string? DiskScanError { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    /// <summary>AD organizational unit, as a path such as "Branch A/Workstations" (without the domain).</summary>
    public string? OrganizationalUnit { get; set; }
    /// <summary>When the OU was last looked up; stops the backfill from re-querying unknown computers every cycle.</summary>
    public DateTime? OuCheckedUtc { get; set; }
    /// <summary>"found", "notFound" or "failed"; null = never checked.</summary>
    public string? OuLookupStatus { get; set; }
    /// <summary>Short, non-sensitive reason when <see cref="OuLookupStatus"/> is "failed".</summary>
    public string? OuLookupError { get; set; }
    /// <summary>Installed RAM, from the same Win32_OperatingSystem read that gives the usage percentage.</summary>
    public long? RamTotalBytes { get; set; }
    /// <summary>Free physical memory from the last successful poll (Win32_OperatingSystem.FreePhysicalMemory, in bytes); used = total - free.</summary>
    public long? RamFreeBytes { get; set; }
    /// <summary>Fixed volumes from the last poll: letter, file system, size, free space (JSON array).</summary>
    public string? VolumesJson { get; set; }
    /// <summary>The Nexora server itself. Always in the inventory, listed first, and never removed by delete, bulk delete or delete-all.</summary>
    public bool IsPinned { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Last successful hardware/OS inventory of one workstation (serialized HardwareInventoryDto). Survives server restarts; deleted with the endpoint.</summary>
public sealed class EndpointInventory
{
    public Guid EndpointId { get; set; }
    public string Json { get; set; } = "{}";
    public DateTime CollectedUtc { get; set; }
}

/// <summary>
/// The last successful size measurement of one user profile on one workstation, written ONLY by the server when a profile.sizes job completes.
/// Never taken from a client request. Unique per (EndpointId, Sid); deleted with the endpoint.
/// </summary>
public sealed class ProfileSize
{
    public Guid Id { get; set; }
    public Guid EndpointId { get; set; }
    public string Sid { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime MeasuredUtc { get; set; }
    /// <summary>The profile.sizes job that produced the measurement.</summary>
    public Guid JobId { get; set; }
    /// <summary>SID of the operator whose helper reported the measurement (the initiator of that job).</summary>
    public string MeasuredBySid { get; set; } = "";
}

/// <summary>A named set of PCs for a specific job ("Cashiers", "Branch 12", "Pilot ring").</summary>
public sealed class EndpointGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}

public sealed class EndpointGroupMember
{
    public Guid GroupId { get; set; }
    public Guid EndpointId { get; set; }
}

/// <summary>One row per endpoint per poll; feeds the CPU/RAM history charts. Purged after the retention period.</summary>
public sealed class MetricSample
{
    public long Id { get; set; }
    public Guid EndpointId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public double? Cpu { get; set; }
    public double? Ram { get; set; }
    public double? Disk { get; set; }
}

/// <summary>One row per monitoring cycle: fleet-wide averages for the dashboard trend.</summary>
public sealed class FleetSample
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public int Total { get; set; }
    public int Online { get; set; }
    public double? AvgCpu { get; set; }
    public double? AvgRam { get; set; }
}

/// <summary>Latest SMART / reliability picture of one physical drive (HD Sentinel style).</summary>
public sealed class DiskHealthSnapshot
{
    public Guid Id { get; set; }
    public Guid EndpointId { get; set; }
    /// <summary>Stable key: serial number when known, else the device id.</summary>
    public string DiskKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string? Serial { get; set; }
    /// <summary>SSD, HDD, NVMe or Unknown.</summary>
    public string MediaType { get; set; } = "Unknown";
    public string? BusType { get; set; }
    public long SizeBytes { get; set; }
    public int? HealthPercent { get; set; }
    /// <summary>OK, Warning, Critical or Unknown.</summary>
    public string Status { get; set; } = "Unknown";
    public int? TemperatureC { get; set; }
    public int? MaxTemperatureC { get; set; }
    public long? PowerOnHours { get; set; }
    public int? WearPercent { get; set; }
    public long? ReadErrors { get; set; }
    public long? WriteErrors { get; set; }
    public long? ReallocatedSectors { get; set; }
    public long? PendingSectors { get; set; }
    public bool PredictFailure { get; set; }
    public int? EstimatedDaysLeft { get; set; }
    public string Summary { get; set; } = "";
    public string AttributesJson { get; set; } = "[]";
    public DateTime UpdatedUtc { get; set; }

    // How this drive was classified and joined at the last scan, kept so the fleet audit can check the mapping without a rescan.
    // Null on rows stored before this was recorded; a rescan fills them in.
    public int? BusTypeCode { get; set; }
    public int? MediaTypeCode { get; set; }
    /// <summary>MSFT_PhysicalDisk.SpindleSpeed as reported (0 = no platters, 4294967295 = unknown).</summary>
    public long? SpindleSpeed { get; set; }
    /// <summary>Which signal decided the class: bus, media, spindle or none.</summary>
    public string? ClassSource { get; set; }
    /// <summary>How the physical-disk record was paired with this drive: Serial, PnpId, DiskNumber or None.</summary>
    public string? PhysicalMatch { get; set; }
    public string? HealthSource { get; set; }
    public bool? SmartMatched { get; set; }
    /// <summary>SMART records and physical drives the host reported in that scan (the same on every drive of the PC).</summary>
    public int? HostSmartRecords { get; set; }
    public int? HostDriveCount { get; set; }
    /// <summary>The scoring rules that produced <see cref="HealthPercent"/>; a different version means a rescan is due.</summary>
    public int? ScoreVersion { get; set; }

    // Score version 4 (HDS-compatible, ADR-0005): the breakdown, the interface warning and the temperature limits used at the last scan.
    /// <summary>The health reasons as JSON: [{code, text, delta}].</summary>
    public string ReasonsJson { get; set; } = "[]";
    /// <summary>SMART 199 cable / connection warning; never part of the score.</summary>
    public string? InterfaceWarning { get; set; }
    public int? TempWarnC { get; set; }
    public int? TempCritC { get; set; }
    /// <summary>"drive" when the limits are the drive's own (NVMe WCTEMP/CCTEMP), "default" for the Admin-set class limits.</summary>
    public string? TempThresholdSource { get; set; }
    /// <summary>Lifetime maximum temperature when the drive provides one.</summary>
    public int? TempMaxLifetimeC { get; set; }
}

public sealed class DiskTempSample
{
    public long Id { get; set; }
    public Guid EndpointId { get; set; }
    public string DiskKey { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public int? TemperatureC { get; set; }
    public int? HealthPercent { get; set; }
}

public sealed class ScriptDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Body { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string CreatedBySid { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public enum ActionType
{
    RestartService = 1,
    KillProcess = 2,
    RunScript = 3,
    RemoteSession = 4,
    /// <summary>Credential operation: gated by Features:CredentialOps.</summary>
    ResetPassword = 5,
    /// <summary>Deletes a user profile folder and registration (System Properties > Advanced > User Profiles > Delete).</summary>
    DeleteProfile = 6,
    StartService = 7,
    StopService = 8,
}

public enum JobStatus
{
    Pending = 0,
    Dispatched = 1,
    Succeeded = 2,
    Failed = 3,
    Rejected = 4,
}

public sealed class ActionJob
{
    public Guid Id { get; set; }
    public ActionType Type { get; set; }
    public Guid EndpointId { get; set; }
    public string TargetHost { get; set; } = "";
    public string OperatorSid { get; set; } = "";
    public string OperatorAccount { get; set; } = "";
    public string ParametersJson { get; set; } = "{}";
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public string? Output { get; set; }
    public int? ExitCode { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

/// <summary>Singleton row (Id = 1) holding platform settings as JSON.</summary>
public sealed class SettingsRow
{
    public int Id { get; set; } = 1;
    public string Json { get; set; } = "{}";
    public DateTime UpdatedUtc { get; set; }
}
