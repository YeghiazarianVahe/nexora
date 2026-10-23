import { useSyncExternalStore } from 'react';
import { formatAbsolute, formatRelative, isStale, ouState, type OuSource } from '../lib/format';
import { formatBytes, formatPair, volumeLine, volumeUsedPercent } from '../lib/disks';
import { healthColorFor, metricColorFor, useThresholds, type MetricKind } from '../lib/thresholds';
import type { Volume } from '../types';

/* ---------- one shared clock: only <RelativeTime> cells re-render every 30 s, never the whole table ---------- */
let now = Date.now();
const listeners = new Set<() => void>();
let timer: number | undefined;

function subscribe(fn: () => void) {
  listeners.add(fn);
  if (listeners.size === 1) {
    now = Date.now();
    timer = window.setInterval(() => { now = Date.now(); listeners.forEach((l) => l()); }, 30_000);
  }
  return () => {
    listeners.delete(fn);
    if (listeners.size === 0 && timer !== undefined) { window.clearInterval(timer); timer = undefined; }
  };
}
const getNow = () => now;

export function RelativeTime({ iso, online }: { iso: string | null; online: boolean }) {
  const t = useSyncExternalStore(subscribe, getNow);
  if (!iso) return <span className="muted">—</span>;
  const stale = isStale(online, iso, t);
  return (
    <time dateTime={iso} title={formatAbsolute(iso)} className={stale ? 'stale' : undefined}>
      {formatRelative(iso, t)}{stale && <span className="sr-only"> (stale data)</span>}
    </time>
  );
}

export function StatusBadge({ online }: { online: boolean }) {
  return <span className={`tag ${online ? 'ok' : 'bad'}`}>{online ? 'Online' : 'Offline'}</span>;
}

/** The only place the hostname is shown: name in bold, the IP (or the address when it differs) beneath it. */
export function EndpointCell({ name, address, ip, pinned }: { name: string; address: string; ip: string | null; pinned?: boolean }) {
  const second = ip ?? (address.toLowerCase() !== name.toLowerCase() ? address : null);
  return (
    <div className="ep-cell" title={second ? `${name} · ${second}` : name}>
      <b className="ep-name">{name}{pinned && <span className="tag ok" style={{ marginLeft: 6 }} title="The Nexora server is always in the inventory">server</span>}</b>
      {second && <span className="ep-ip">{second}</span>}
    </div>
  );
}

/** Last two OU segments with an ellipsis; the full path is the tooltip; clicking filters the table to that OU. */
export function OuCell({ endpoint, onFilter }: { endpoint: OuSource; onFilter?: (path: string) => void }) {
  const s = ouState(endpoint);
  if (s.kind === 'failed') return <span className="muted ou-failed" title={s.tooltip}>{s.label}</span>;
  if (s.kind !== 'found') return <span className="muted" title={s.tooltip}>{s.label}</span>;
  if (!onFilter) return <span title={s.path}>{s.text}</span>;
  return (
    <button className="ou-link" title={s.path} aria-label={`OU ${s.path}. Filter by this OU`}
      onClick={(e) => { e.stopPropagation(); onFilter(s.path); }}>
      {s.text}
    </button>
  );
}

/** [bar] 74% on one line. Offline endpoints have no live numbers. Colours follow the Settings thresholds for this kind of metric. */
export function MetricCell({ value, online, kind, title }: { value: number | null; online: boolean; kind: MetricKind; title?: string }) {
  const th = useThresholds();
  if (!online || value == null) return <span className="muted">—</span>;
  const pct = Math.max(0, Math.min(100, value));
  return (
    <span className="metric" title={title ?? `${value.toFixed(1)}%`}>
      <span className="metric-bar" aria-hidden="true"><i style={{ width: `${pct}%`, background: metricColorFor(Math.round(value), kind, th) }} /></span>
      <span className="metric-pct">{Math.round(value)}%</span>
    </span>
  );
}

/** Drive health of one kind: meter plus %, "—" when the PC has no such drive. Health is the last stored scan, so it shows for offline PCs too. */
export function DriveHealthCell({ value, kind }: { value: number | null; kind: 'SSD' | 'HDD' }) {
  const th = useThresholds();
  if (value == null) return <span className="muted" title={`No ${kind} reporting health on this PC`}>—</span>;
  return (
    <span className="metric compact" title={`Worst ${kind} health: ${value}%`}>
      <span className="metric-bar" aria-hidden="true"><i style={{ width: `${Math.max(0, Math.min(100, value))}%`, background: healthColorFor(value, th) }} /></span>
      <span className="metric-pct">{value}%</span>
    </span>
  );
}

/** System drive free / total with a thin usage bar; the tooltip lists every volume. */
export function FreeSpaceCell({ volume, volumes }: { volume: Volume | null; volumes: Volume[] | null }) {
  const th = useThresholds();
  if (!volume) return <span className="muted">—</span>;
  const used = volumeUsedPercent(volume) ?? 0;
  const tip = (volumes ?? [volume]).map((v) => `${volumeLine(v)} (${volumeUsedPercent(v)?.toFixed(0) ?? '?'}% used)`).join('\n');
  return (
    <span className="freecell" title={tip}>
      <span className="free-text" data-testid="free-text">{formatPair(volume.freeBytes, volume.totalBytes)}</span>
      <span className="free-bar" aria-hidden="true"><i style={{ width: `${Math.max(0, Math.min(100, used))}%`, background: metricColorFor(Math.round(used), 'disk', th) }} /></span>
    </span>
  );
}

export { formatBytes };

export function HealthBadge({ status, percent }: { status: string | null; percent: number | null }) {
  if (!status || status === 'Unknown') return <span className="muted">—</span>;
  const cls = status === 'OK' ? 'ok' : status === 'Warning' ? 'warn' : 'bad';
  const icon = status === 'OK' ? '✓' : status === 'Warning' ? '!' : '✕';
  return <span className={`tag ${cls}`} title={`Drive health: ${status}`}>{icon} {percent != null ? `${percent}%` : status}</span>;
}
