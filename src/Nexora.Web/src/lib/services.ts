import type { ServiceAction, ServiceDependents, ServiceRowDto, ServicesResult } from '../types';

const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
const str = (v: unknown) => (typeof v === 'string' ? v : '');
const strList = (v: unknown) => (Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : []);
const ACTIONS: readonly string[] = ['start', 'stop', 'restart'];

function row(v: unknown): ServiceRowDto | null {
  if (!isRecord(v) || typeof v.name !== 'string' || v.name === '') return null;
  return {
    name: v.name, displayName: str(v.displayName) || v.name, state: str(v.state), startMode: str(v.startMode),
    protected: v.protected === true,
    allowed: strList(v.allowed).filter((a): a is ServiceAction => ACTIONS.includes(a)),
  };
}

/** The one place the services envelope is read: { unavailable?, reason?, items } (a bare array is accepted too). */
export function parseServices(raw: unknown): ServicesResult {
  const list = Array.isArray(raw) ? raw : isRecord(raw) && Array.isArray(raw.items) ? raw.items : [];
  const items = list.map(row).filter((r): r is ServiceRowDto => r !== null);
  const o = isRecord(raw) ? raw : {};
  return { unavailable: o.unavailable === true, reason: typeof o.reason === 'string' && o.reason !== '' ? o.reason : null, items };
}

export function parseDependents(raw: unknown): ServiceDependents {
  const o = isRecord(raw) ? raw : {};
  const risk = o.effectiveRisk;
  return {
    running: strList(o.running), all: strList(o.all), anyProtected: o.anyProtected === true,
    unavailable: o.unavailable === true, reason: typeof o.reason === 'string' && o.reason !== '' ? o.reason : null,
    effectiveRisk: risk === 'Low' || risk === 'Medium' || risk === 'High' || risk === 'Critical' ? risk : null,
  };
}

/** Why a button is disabled. Display only: whether it is enabled comes from the server's allowed[] alone. */
export function disabledReason(s: ServiceRowDto, action: ServiceAction, hostOnline: boolean): string {
  if (!hostOnline) return 'The workstation is offline';
  if (s.protected && action !== 'start') return 'Protected: cannot be stopped or restarted';
  const running = s.state.toLowerCase() === 'running';
  if (action === 'start') {
    if (running) return 'Already running';
    if (s.startMode.toLowerCase() === 'disabled') return 'Start type is Disabled';
  } else if (!running) return 'Not running';
  return 'Not allowed by the server for your role or this service';
}

/** Exactly the server's rule for a protected service name: ^[A-Za-z0-9_.\-$ ]{1,256}\z */
const SERVICE_PATTERN = /^[A-Za-z0-9_.\-$ ]{1,256}$/;
export function validateServicePattern(raw: string, existing: readonly string[]): string | null {
  const v = raw.trim();
  if (v === '') return 'Enter a service name';
  if (v.length > 256) return 'Too long (256 characters at most)';
  if (!SERVICE_PATTERN.test(v)) return 'Use letters, digits, spaces and . _ - $ only';
  if (existing.some((x) => x.toLowerCase() === v.toLowerCase())) return 'Already in the list';
  return null;
}
