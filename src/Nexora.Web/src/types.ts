export type Role = 'None' | 'Viewer' | 'Operator' | 'Admin' | 'SuperAdmin';

export type MeStatus = 'Active' | 'NoAccess' | 'Pending' | 'Rejected' | 'Revoked' | 'Denied';

export interface Me {
  sid: string;
  account: string;
  displayName: string;
  role: Role;
  isSuperAdmin: boolean;
  status: MeStatus;
  detail: string | null;
}

export interface AccessRequest {
  id: string;
  sid: string;
  account: string;
  displayName: string;
  status: string;
  requestedUtc: string;
  decidedUtc: string | null;
  grantedRole: string | null;
  rejectReason: string | null;
}

export interface UserRow {
  sid: string;
  account: string;
  displayName: string;
  role: string;
  status: string;
  createdUtc: string;
  lastSeenUtc: string | null;
  lastAuthScheme: string | null;
  isSuperAdmin: boolean;
}

export interface DirectoryUser {
  sid: string;
  account: string;
  displayName: string;
}

/** A fixed volume as of the last poll. */
export interface Volume {
  letter: string;
  fileSystem: string | null;
  totalBytes: number;
  freeBytes: number;
}

export interface EndpointSummary {
  id: string;
  /** The address used to reach the PC: an IP or a host name. */
  hostname: string;
  /** Friendly workstation name (falls back to the address). */
  name: string;
  reportedHostname: string | null;
  ipAddress: string | null;
  lastUser: string | null;
  osCaption: string | null;
  osVersion: string | null;
  uptimeSeconds: number | null;
  lastSeenUtc: string | null;
  online: boolean;
  cpuPercent: number | null;
  ramPercent: number | null;
  diskPercent: number | null;
  groupIds: string[];
  diskHealth: 'OK' | 'Warning' | 'Critical' | 'Unknown' | null;
  /** The weakest drive's health, 0-100. */
  diskHealthPercent: number | null;
  /** Worst health among the SSD / NVMe drives, null when the PC has none. */
  ssdHealthPercent: number | null;
  /** Worst health among the HDDs, null when the PC has none. */
  hddHealthPercent: number | null;
  ramTotalBytes: number | null;
  /** Fixed volumes as of the last poll. */
  volumes: Volume[] | null;
  manufacturer: string | null;
  model: string | null;
  /** AD organizational unit, e.g. "Branch A/Workstations". */
  organizationalUnit: string | null;
  /** Outcome of the AD OU lookup; null = not checked yet. Optional so an older server still parses. */
  ouStatus?: 'found' | 'notFound' | 'failed' | null;
  /** Why the lookup failed (only with ouStatus 'failed'). */
  ouError?: string | null;
  lastError: string | null;
  /** Set while this PC's drive scan keeps failing; CPU/RAM are still current. */
  diskScanError?: string | null;
  alerts: string[];
  /** The Nexora server: always listed first, never removed. */
  isPinned: boolean;
}

export interface AuditEntry {
  id: number;
  timestampUtc: string;
  actorSid: string;
  actorAccount: string;
  action: string;
  targetType: string | null;
  targetId: string | null;
  oldValue: string | null;
  newValue: string | null;
  clientIp: string | null;
  details: string | null;
  success: boolean;
}

export interface Paged<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface Dashboard {
  total: number;
  online: number;
  offline: number;
  cpuAlerts: number;
  ramAlerts: number;
  diskAlerts: number;
  diskHealthAlerts: number;
  alertEndpoints: EndpointSummary[];
  recentActions: AuditEntry[];
  /** How many workstations have an alert; alertEndpoints lists the worst 50. */
  alertEndpointTotal: number;
}

export interface EndpointDetail {
  services: { name: string; displayName: string; state: string; startMode: string }[];
  processes: { pid: number; name: string; workingSetBytes: number; owner?: string | null; cpuPercent?: number | null }[];
  disks: { drive: string; label: string | null; sizeBytes: number; freeBytes: number }[];
  software: { name: string; version: string | null; publisher: string | null }[];
  errors: { timeUtc: string; source: string; eventId: number; log: string; message: string }[];
  warnings: string[];
}

export interface Script {
  id: string;
  name: string;
  description: string;
  body: string;
  sha256: string;
  enabled: boolean;
  updatedUtc: string;
}

export interface PlatformSettings {
  pollIntervalSeconds: number;
  maxConcurrency: number;
  cpuAlertPercent: number;
  ramAlertPercent: number;
  diskAlertPercent: number;
  offlineAfterFailures: number;
  metricRetentionDays: number;
  diskPollMinutes: number;
  /** Admin-editable extra protected service names / patterns (case-insensitive; * is a wildcard). */
  protectedServices: string[];
  /** Built-in protected services; read-only. */
  protectedServicesFloor: string[];
}

export const roleRank: Record<Role, number> = { None: 0, Viewer: 1, Operator: 2, Admin: 3, SuperAdmin: 4 };

export interface Group {
  id: string;
  name: string;
  description: string;
  memberIds: string[];
  online: number;
}

export interface MetricPoint {
  timeUtc: string;
  cpu: number | null;
  ram: number | null;
  disk: number | null;
}

export interface FleetPoint {
  timeUtc: string;
  avgCpu: number | null;
  avgRam: number | null;
  online: number;
}

export interface SmartAttribute {
  id: number;
  name: string;
  current: number;
  worst: number;
  raw: number;
  threshold: number;
}

/** One line of the score breakdown. delta < 0 deducted points, 0 is a note, > 0 points given back. Invariant: 100 + sum(delta) == healthPercent. */
export interface HealthReason { code: string; text: string; delta: number }

export interface DriveTemperature {
  currentC: number | null;
  warnC: number | null;
  critC: number | null;
  /** 'drive' = limits reported by the drive; 'default' = Admin setting. */
  source: 'drive' | 'default';
  /** null for NVMe. */
  lifetimeMaxC: number | null;
  status: 'OK' | 'Warning' | 'Critical' | null;
}

export interface DiskHealth {
  confidence?: 'High' | 'Medium' | 'Low';
  valueKind?: 'estimated' | 'unavailable';
  stale?: boolean;
  lastKnownTemperatureC?: number | null;
  firmware?: string | null;
  healthModelVersion?: number | null;
  currentHealthModelVersion?: number | null;
  scanCompletedUtc?: string | null;
  sourceNote?: string | null;
  diskKey: string;
  /** Additive (F4); absent on older payloads. */
  reasons?: HealthReason[] | null;
  temperature?: DriveTemperature | null;
  /** From SMART 199 (CRC). Not part of the score. */
  interfaceWarning?: string | null;
  model: string;
  serial: string | null;
  mediaType: string;
  /** SSD (SSD and NVMe), HDD, or Unknown only when the drive cannot be told apart. */
  type: 'SSD' | 'HDD' | 'Unknown';
  busType: string | null;
  sizeBytes: number;
  healthPercent: number | null;
  status: 'OK' | 'Warning' | 'Critical' | 'Unknown';
  temperatureC: number | null;
  temperatureStatus?: "OK" | "Warning" | "Critical" | null;
  maxTemperatureC: number | null;
  powerOnHours: number | null;
  wearPercent: number | null;
  readErrors: number | null;
  writeErrors: number | null;
  reallocatedSectors: number | null;
  pendingSectors: number | null;
  predictFailure: boolean;
  estimatedDaysLeft: number | null;
  summary: string;
  attributes: SmartAttribute[];
  updatedUtc: string;
  temperatureHistory: { timeUtc: string; temperatureC: number | null }[];
}

// ---------- user profiles (F3): GET /api/endpoints/:id/profiles ----------

/** 'ProfileList' | 'Wmi' | a helper-reported value; unknown values are displayed as plain text. */
export type ProfileLastUsedSource = string;

export interface ProfileRowDto {
  state?: string | null;
  sid: string;
  account: string | null;
  localPath: string;
  loaded: boolean;
  sizeBytes: number | null;
  sizeMeasuredUtc: string | null;
  lastUsedUtc: string | null;
  lastUsedSource: ProfileLastUsedSource | null;
  deletable: boolean;
  blockReason: string | null;
}

export interface ProfilesResponse { unavailable: boolean; reason: string | null; items: ProfileRowDto[] }

/** POST /api/endpoints/:id/profiles/delete-batch/confirm */
export interface ProfileDeleteConfirm {
  token: string;
  expectedHostname: string;
  count: number;
  items: { sid: string; account: string | null; path: string; sizeBytes: number | null }[];
}

/** POST /api/endpoints/:id/profiles/delete-batch */
export interface ProfileDeleteResult { batchId: string; jobIds: string[] }

export type Range = '1h' | '6h' | '24h' | '7d';

// ---------- hardware / OS inventory (GET /api/endpoints/:id/hardware); null = the workstation did not report it ----------

export interface OsInventory { productName: string | null; edition: string | null; version: string | null; build: string | null; architecture: string | null; installedUtc: string | null; bootTimeUtc: string | null;
  kernelVersion?: string | null;
  /** Overview endpoint only. null = unknown. */
  fastStartupEnabled?: boolean | null;
  /** 'fullBoot' = bootTimeUtc is the last full boot (Fast Startup hides restarts-by-shutdown). */
  uptimeBasis?: 'boot' | 'fullBoot' }
export interface CpuInventory { manufacturer: string | null; model: string | null; physicalCores: number | null; logicalProcessors: number | null; architecture: string | null; maxClockMhz: number | null }
export interface MemoryModule { slot: string | null; bank: string | null; capacityBytes: number | null; manufacturer: string | null; speedMhz: number | null; configuredSpeedMhz: number | null; partNumber: string | null; technology: string | null }
export interface MemoryInventory {
  installedBytes: number | null; usableBytes: number | null; technology: string | null; speedMhz: number | null;
  /** null = unknown, never 0 */
  slotCount: number | null; occupiedSlots: number; emptySlots: number | null; maxCapacityBytes: number | null;
  modules: MemoryModule[]; modulesReported: boolean;
}
export interface BoardInventory { manufacturer: string | null; model: string | null; serial: string | null }
export interface BiosInventory { manufacturer: string | null; version: string | null; releaseDateUtc: string | null }
export interface ComputerInventory { manufacturer: string | null; model: string | null; hostname: string | null; domain: string | null; domainJoined: boolean | null }
export interface HardwareInventory {
  status: 'Complete' | 'Partial' | 'Unavailable';
  collectedUtc: string | null; cached: boolean; serialsHidden: boolean; errors: string[];
  os: OsInventory | null; cpu: CpuInventory | null; memory: MemoryInventory | null;
  board: BoardInventory | null; bios: BiosInventory | null; computer: ComputerInventory | null;
  attemptedUtc?: string | null;
}

// ---------- GET /api/endpoints/:id/overview ----------

export interface OverviewLive {
  cpuLoadPercent: number | null; ramTotalBytes: number | null; ramUsedBytes: number | null; ramFreeBytes: number | null;
  ramPercentUsed: number | null; sampledUtc: string;
}
export interface EndpointOverview {
  online: boolean; lastSeenUtc: string | null; collectedUtc: string | null; stale: boolean; unavailableReason: string | null;
  inventory: HardwareInventory | null; live: OverviewLive | null;
}

/** Service action codes the server may offer for a row. */
export type ServiceAction = 'start' | 'stop' | 'restart';

export interface ServiceRowDto {
  name: string;
  displayName: string;
  state: string;
  startMode: string;
  protected: boolean;
  /** What the SERVER allows for this row. The UI never recomputes it. */
  allowed: ServiceAction[];
}

export interface ServicesResult {
  unavailable: boolean;
  reason: string | null;
  items: ServiceRowDto[];
}

export interface ServiceDependents {
  running: string[];
  all: string[];
  anyProtected: boolean;
  /** The dependents could not be determined; the action must stay blocked. */
  unavailable: boolean;
  reason: string | null;
  /** Risk as computed by the server (High when dependents would stop). */
  effectiveRisk: 'Low' | 'Medium' | 'High' | 'Critical' | null;
}

// ---- F5 Diagnose ----

export type DiagnoseStatus = 'Running' | 'Complete' | 'Incomplete' | 'Failed';
export type DiagnoseSeverity = 'info' | 'warning' | 'critical';
export type DiagnoseConfidence = 'high' | 'medium' | 'low';
export type CollectorStatus = 'ok' | 'incomplete' | 'failed' | 'missing';

export interface DiagnoseProfile { id: string; name: string; description: string; collectors: number }

export interface DiagnoseHistoryItem {
  id: string; profileId: string; profileName: string; status: DiagnoseStatus; complete: boolean;
  startedUtc: string; finishedUtc: string | null; findingCount: number; topSeverity: DiagnoseSeverity | null;
}

export interface DiagnoseAction { kind: 'catalog' | 'manual'; actionCode?: string; params?: Record<string, unknown>; label: string; instructions?: string }
export interface DiagnoseFinding {
  id: string; cause: string; confidence: DiagnoseConfidence; severity: DiagnoseSeverity; score: number;
  evidence: { label: string; value: string }[]; actions: DiagnoseAction[];
}
export interface DiagnoseCollector { id: string; status: CollectorStatus; reason?: string }

export interface DiagnoseRun {
  id: string; endpointId: string; hostname: string; profileId: string; profileName: string; status: DiagnoseStatus; complete: boolean;
  startedUtc: string; finishedUtc: string | null; jobId: string | null; schemaVersion: number;
  findings: DiagnoseFinding[]; collectors: DiagnoseCollector[]; notEvaluated: string[];
}
