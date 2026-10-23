export type RiskLevel = 'Low' | 'Medium' | 'High' | 'Critical';

export interface ParamInfo {
  name: string;
  type: 'String' | 'Int' | 'Bool' | 'Enum' | 'StringList';
  label: string;
  required: boolean;
  min: number | null;
  max: number | null;
  maxLength: number;
  allowed: string[] | null;
  default: string | null;
  help: string | null;
}

export interface CatalogEntry {
  code: string;
  displayName: string;
  category: string;
  risk: RiskLevel;
  minRole: string;
  method: string;
  parameters: ParamInfo[];
  timeoutSeconds: number;
  description: string;
  /** The technical command behind the friendly name, for example "sfc /scannow". */
  command: string | null;
  supportsCancel: boolean;
  supportsBulk: boolean;
  requiresUserNotice: boolean;
  needsConfirmation: boolean;
  needsReason: boolean;
  needsTypedConfirmation: boolean;
  heavy: boolean;
  isPower: boolean;
  /** Protected workstations (the Nexora server, server OS, protected list) need an Admin override with a reason. */
  checksProtected: boolean;
  implemented: boolean;
  allowed: boolean;
}

export type JobState = 'Queued' | 'WaitingForHelper' | 'Running' | 'Succeeded' | 'Warning' | 'PartiallyFailed' | 'Failed' | 'Cancelled';
export type TargetState = 'Queued' | 'Running' | 'Succeeded' | 'Warning' | 'Failed' | 'Cancelled' | 'TimedOut' | 'Skipped';

export interface JobSummary {
  id: string;
  actionCode: string;
  actionName: string;
  initiatorSid: string;
  initiatorAccount: string;
  status: JobState;
  createdUtc: string;
  finishedUtc: string | null;
  reason: string | null;
  targetCount: number;
  queued: number;
  running: number;
  succeeded: number;
  warning: number;
  failed: number;
  cancelled: number;
  timedOut: number;
  skipped: number;
  protectedOverride: boolean;
  risk: RiskLevel;
}

export interface JobTargetInfo {
  id: string;
  jobId: string;
  endpointId: string;
  endpointName: string;
  host: string;
  status: TargetState;
  startedUtc: string | null;
  finishedUtc: string | null;
  durationSeconds: number | null;
  exitCode: number | null;
  resultSummary: string | null;
  skipReason: string | null;
  progress: number | null;
  logLines: number;
  hasOutputFile: boolean;
  outputSha256: string | null;
}

export interface JobDetailDto {
  job: JobSummary;
  parametersJson: string;
  targets: JobTargetInfo[];
}

export interface LogLineDto {
  seq: number;
  timestampUtc: string;
  stream: 'stdout' | 'stderr' | 'system';
  text: string;
}

/** Category keys come from the catalog (`category`); one map for every label. */
export const CATEGORY_LABEL: Record<string, string> = {
  'power-session': 'Power & Session',
  'health-repair': 'System Health & Repair',
  'updates-security': 'Windows Update & Security',
  'network-policy': 'Network & Policy',
  diagnostics: 'Diagnostics',
  'admin-scripts': 'Admin Scripts',
  services: 'Services',
  processes: 'Processes',
  profiles: 'Profiles',
};

/** Tabs of the Remote Actions grid, in display order. */
export const GRID_CATEGORIES = ['power-session', 'health-repair', 'updates-security', 'network-policy', 'diagnostics', 'admin-scripts'] as const;
export const DEFAULT_CATEGORY = GRID_CATEGORIES[0];
/** Categories whose actions live only in the Services / Processes / Profiles tabs and never appear in the Remote Actions grid. */
export const NON_GRID_CATEGORIES: readonly string[] = ['services', 'processes', 'profiles'];
/** Catch-all tab for a grid action whose category key is not known to this UI yet, so new actions never disappear. */
export const OTHER_CATEGORY = 'other';
export const OTHER_LABEL = 'Other';

export const FINAL_JOB: JobState[] = ['Succeeded', 'Warning', 'PartiallyFailed', 'Failed', 'Cancelled'];
export const isFinalJob = (s: JobState) => FINAL_JOB.includes(s);
export const isFinalTarget = (s: TargetState) => ['Succeeded', 'Warning', 'Failed', 'Cancelled', 'TimedOut', 'Skipped'].includes(s);

export const riskRank: Record<RiskLevel, number> = { Low: 0, Medium: 1, High: 2, Critical: 3 };

/** What a bulk run would meet: offline, protected and busy targets. */
export interface PreflightResult {
  total: number;
  reachable: number;
  offline: string[];
  protected: { name: string; reason: string }[];
  busy: string[];
  overrideNeeded: boolean;
  /** Highest risk the server computed for these targets, when it reports one. */
  effectiveRisk?: RiskLevel;
}

/** Display names the UI fixes regardless of the catalog text: a process is stopped, never ended or killed. */
const LABEL_OVERRIDE: Readonly<Record<string, string>> = { 'proc.kill': 'Stop process' };
export const actionLabel = (code: string, serverName: string) => LABEL_OVERRIDE[code] ?? serverName;
