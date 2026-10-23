import type {
  CollectorStatus, DiagnoseAction, DiagnoseCollector, DiagnoseConfidence, DiagnoseFinding, DiagnoseHistoryItem, DiagnoseProfile, DiagnoseRun,
  DiagnoseSeverity, DiagnoseStatus,
} from '../types';

const STATUSES: readonly DiagnoseStatus[] = ['Running', 'Complete', 'Incomplete', 'Failed'];
const SEVERITIES: readonly DiagnoseSeverity[] = ['info', 'warning', 'critical'];
const CONFIDENCES: readonly DiagnoseConfidence[] = ['high', 'medium', 'low'];
const COLLECTOR_STATES: readonly CollectorStatus[] = ['ok', 'incomplete', 'failed', 'missing'];

const rec = (v: unknown): Record<string, unknown> => (typeof v === 'object' && v !== null && !Array.isArray(v) ? (v as Record<string, unknown>) : {});
const str = (v: unknown, fallback = ''): string => (typeof v === 'string' ? v : typeof v === 'number' ? String(v) : fallback);
const oneOf = <T extends string>(list: readonly T[], v: unknown, fallback: T): T => (list.includes(v as T) ? (v as T) : fallback);
const arr = (v: unknown): unknown[] => (Array.isArray(v) ? v : []);

/** Evidence values come from the endpoint: whatever they are, they become plain text. */
const evidenceText = (v: unknown): string => (typeof v === 'string' ? v : v === null || v === undefined ? '' : typeof v === 'object' ? JSON.stringify(v) : String(v));

function parseAction(raw: unknown): DiagnoseAction {
  const a = rec(raw);
  const kind = a.kind === 'catalog' ? 'catalog' : 'manual';
  const params = a.params !== undefined && a.params !== null ? rec(a.params) : undefined;
  return {
    kind, label: str(a.label), actionCode: typeof a.actionCode === 'string' ? a.actionCode : undefined, params,
    instructions: typeof a.instructions === 'string' ? a.instructions : undefined,
  };
}

function parseFinding(raw: unknown, i: number): DiagnoseFinding {
  const f = rec(raw);
  return {
    id: str(f.id, `finding-${i}`), cause: str(f.cause), confidence: oneOf(CONFIDENCES, f.confidence, 'low'), severity: oneOf(SEVERITIES, f.severity, 'info'),
    score: typeof f.score === 'number' ? f.score : 0,
    evidence: arr(f.evidence).map((e) => ({ label: str(rec(e).label), value: evidenceText(rec(e).value) })),
    actions: arr(f.actions).map(parseAction),
  };
}

/** The run is validated at the boundary; findings keep the server's order (score only ranks them there). */
export function parseRun(raw: unknown): DiagnoseRun {
  const r = rec(raw);
  if (typeof r.id !== 'string' || r.id === '') throw new Error('The server returned an unreadable diagnosis.');
  const collectors: DiagnoseCollector[] = arr(r.collectors).map((c) => {
    const o = rec(c);
    return { id: str(o.id), status: oneOf(COLLECTOR_STATES, o.status, 'failed'), reason: typeof o.reason === 'string' ? o.reason : undefined };
  });
  return {
    id: r.id, endpointId: str(r.endpointId), hostname: str(r.hostname), profileId: str(r.profileId), profileName: str(r.profileName),
    status: oneOf(STATUSES, r.status, 'Failed'), complete: r.complete === true, startedUtc: str(r.startedUtc),
    finishedUtc: typeof r.finishedUtc === 'string' ? r.finishedUtc : null, jobId: typeof r.jobId === 'string' ? r.jobId : null,
    schemaVersion: typeof r.schemaVersion === 'number' ? r.schemaVersion : 0,
    findings: arr(r.findings).map(parseFinding), collectors, notEvaluated: arr(r.notEvaluated).map((s) => str(s)),
  };
}

export const parseProfiles = (raw: unknown): DiagnoseProfile[] =>
  arr(raw).map((p) => { const o = rec(p); return { id: str(o.id), name: str(o.name), description: str(o.description), collectors: typeof o.collectors === 'number' ? o.collectors : 0 }; })
    .filter((p) => p.id !== '');

export const parseHistory = (raw: unknown): DiagnoseHistoryItem[] =>
  arr(raw).map((h) => {
    const o = rec(h);
    return {
      id: str(o.id), profileId: str(o.profileId), profileName: str(o.profileName), status: oneOf(STATUSES, o.status, 'Failed'), complete: o.complete === true,
      startedUtc: str(o.startedUtc), finishedUtc: typeof o.finishedUtc === 'string' ? o.finishedUtc : null,
      findingCount: typeof o.findingCount === 'number' ? o.findingCount : 0,
      topSeverity: SEVERITIES.includes(o.topSeverity as DiagnoseSeverity) ? (o.topSeverity as DiagnoseSeverity) : null,
    };
  }).filter((h) => h.id !== '');

export const statusTone = (s: DiagnoseStatus): string => (s === 'Complete' ? 'ok' : s === 'Running' ? 'info' : s === 'Incomplete' ? 'warn' : 'bad');
export const severityTone = (s: DiagnoseSeverity): string => (s === 'critical' ? 'bad' : s === 'warning' ? 'warn' : 'info');
export const confidenceTone = (c: DiagnoseConfidence): string => (c === 'high' ? 'ok' : c === 'medium' ? 'warn' : '');

/** Polling is a fallback to the DiagnosisCompleted push: every 3 s while Running, for at most 30 minutes (a job can outlive its queue TTL plus its timeout). */
export const POLL_MS = 3000;
export const POLL_MAX_MS = 30 * 60 * 1000;
export const pollWhileRunning = (running: boolean, sinceMs: number, nowMs: number): number | false => (running && nowMs - sinceMs < POLL_MAX_MS ? POLL_MS : false);

/** Pretty JSON for the raw evidence view, capped so a large capture cannot freeze the page. */
export function capText(text: string, cap: number): { shown: string; truncated: boolean } {
  return text.length <= cap ? { shown: text, truncated: false } : { shown: text.slice(0, cap), truncated: true };
}
