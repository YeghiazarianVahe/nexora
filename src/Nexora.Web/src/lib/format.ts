/** Shared thresholds for CPU / RAM / disk meters. One place, used by the table and the other pages. */
export const METRIC_COLORS = { ok: '#69BA31', warn: '#E5A50A', bad: '#E5484D' } as const;
export const METRIC_WARN = 70;
export const METRIC_BAD = 90;

export function metricColor(value: number): string {
  if (value >= METRIC_BAD) return METRIC_COLORS.bad;
  if (value >= METRIC_WARN) return METRIC_COLORS.warn;
  return METRIC_COLORS.ok;
}

export const SEPARATOR = ' › ';

/**
 * "CONTOSO/BRANCHES/BranchA/Workstations" shows as "BranchA › Workstations":
 * the root (domain-level) part is dropped and only the last two segments are kept.
 */
export function formatOu(path: string | null | undefined): string | null {
  if (!path) return null;
  const parts = path.split('/').map((p) => p.trim()).filter(Boolean);
  if (parts.length === 0) return null;
  return parts.slice(-2).join(SEPARATOR);
}

export interface OuSource {
  organizationalUnit: string | null;
  ouStatus?: 'found' | 'notFound' | 'failed' | null;
  ouError?: string | null;
}

export type OuState =
  | { kind: 'found'; path: string; text: string }
  | { kind: 'failed'; label: string; tooltip: string }
  | { kind: 'notFound' | 'unchecked'; label: string; tooltip: string };

/** What to show for an endpoint's OU. Only 'found' carries a path; everything else is a placeholder and never a filter value. */
export function ouState(e: OuSource): OuState {
  if (e.ouStatus === 'failed') return { kind: 'failed', label: 'Unknown (lookup failed)', tooltip: e.ouError?.trim() || 'The Active Directory lookup failed' };
  const text = formatOu(e.organizationalUnit);
  if (e.ouStatus !== 'notFound' && e.organizationalUnit && text) return { kind: 'found', path: e.organizationalUnit, text };
  if (e.ouStatus === 'notFound') return { kind: 'notFound', label: '—', tooltip: 'Not found in Active Directory' };
  return { kind: 'unchecked', label: '—', tooltip: 'Not checked yet' };
}

/** The OU path usable as a filter value / sort key, or null for failed, not found and unchecked. */
export const filterableOu = (e: OuSource): string | null => {
  const s = ouState(e);
  return s.kind === 'found' ? s.path : null;
};

export const STALE_MINUTES = 15;

/** "just now", "2 min ago", "3 h ago", "5 d ago". */
export function formatRelative(iso: string | null | undefined, nowMs: number): string {
  if (!iso) return '—';
  const diff = Math.max(0, nowMs - Date.parse(iso));
  const s = Math.floor(diff / 1000);
  if (s < 45) return 'just now';
  const m = Math.floor(s / 60);
  if (m < 60) return `${Math.max(1, m)} min ago`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h} h ago`;
  return `${Math.floor(h / 24)} d ago`;
}

const yerevan = new Intl.DateTimeFormat('en-GB', {
  timeZone: 'Asia/Yerevan', hour12: false, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit',
});
const yerevanSeconds = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Yerevan', hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' });
export const formatSecond = (when: string | number) => yerevanSeconds.format(new Date(when));

function yerevanParts(when: string | number | Date) {
  const parts = yerevan.formatToParts(new Date(when));
  const get = (t: string) => parts.find((p) => p.type === t)?.value ?? '';
  return { day: get('day'), month: get('month'), year: get('year'), hour: get('hour') === '24' ? '00' : get('hour'), minute: get('minute') };
}

/** dd.MM.yyyy HH:mm, 24 hour clock, Asia/Yerevan: THE date format of the application, whatever the browser's locale or time zone. */
export function formatMinute(when: string | number | Date | null | undefined): string {
  if (when == null || when === '') return '';
  const p = yerevanParts(when);
  return `${p.day}.${p.month}.${p.year} ${p.hour}:${p.minute}`;
}

/** HH:mm in Asia/Yerevan (chart axes). */
export function formatClock(ms: number): string {
  const p = yerevanParts(ms);
  return `${p.hour}:${p.minute}`;
}

/** dd.MM HH:mm in Asia/Yerevan (chart axes over more than a day and a half). */
export function formatDayClock(ms: number): string {
  const p = yerevanParts(ms);
  return `${p.day}.${p.month} ${p.hour}:${p.minute}`;
}

/** dd.MM.yyyy HH:mm:ss, 24 hour clock, Asia/Yerevan. */
export function formatAbsolute(iso: string | null | undefined): string {
  if (!iso) return '';
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone: 'Asia/Yerevan', hour12: false, year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit',
  }).formatToParts(new Date(iso));
  const get = (t: string) => parts.find((p) => p.type === t)?.value ?? '';
  const hour = get('hour') === '24' ? '00' : get('hour');
  return `${get('day')}.${get('month')}.${get('year')} ${hour}:${get('minute')}:${get('second')}`;
}

/** Online but silent for a while: the numbers on screen are old. */
export function isStale(online: boolean, iso: string | null | undefined, nowMs: number): boolean {
  return online && !!iso && nowMs - Date.parse(iso) > STALE_MINUTES * 60_000;
}

export interface ExportLabelInput {
  selectedCount: number;
  /** Rows matching the active filters (server count when the list is truncated). */
  filteredCount: number;
  /** Every endpoint, ignoring filters. */
  totalCount: number;
  filtersActive: boolean;
}

/** The button always says exactly what it will export. */
export function exportLabel({ selectedCount, filteredCount, totalCount, filtersActive }: ExportLabelInput): string {
  if (selectedCount > 0) return `Export ${selectedCount} selected`;
  if (filtersActive) return `Export ${filteredCount} filtered`;
  return `Export all ${totalCount}`;
}

export function uptimeText(seconds: number | null | undefined): string {
  if (seconds == null) return '—';
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  return d > 0 ? `${d}d ${h}h` : h > 0 ? `${h}h ${m}m` : `${m}m`;
}

/** "Manufacturer Model" without saying the manufacturer twice ("HP HP t540" -> "HP t540", "Hewlett-Packard HP t620" -> "HP t620"). */
export function formatModel(maker: string | null | undefined, model: string | null | undefined): string {
  const mk = (maker ?? '').trim();
  const md = (model ?? '').trim();
  if (!mk) return md;
  if (!md) return mk;
  const alias = /^(hewlett[- ]?packard|hp inc\.?|hp)$/i.test(mk) ? 'HP' : mk;
  const lower = md.toLowerCase();
  if (lower.startsWith(mk.toLowerCase()) || lower.startsWith(alias.toLowerCase())) return md;
  return `${alias} ${md}`;
}
