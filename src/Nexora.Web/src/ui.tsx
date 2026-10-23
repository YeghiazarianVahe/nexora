import { useEffect, useRef, useState, type ReactNode } from 'react';
import { ApiError } from './api';
import { formatMinute } from './lib/format';
import { metricColorFor, useThresholds, type MetricKind } from './lib/thresholds';

const FOCUSABLE = 'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])';

/**
 * Modal shell. Focus is set ONCE when it opens (the first field marked autofocus, else the dialog) and handed back to the
 * trigger on close. The close handler is read through a ref so a parent re-render (live updates, timers) never re-runs the
 * effect: it used to re-focus the dialog container on every render and steal the caret from the input being typed into.
 */
export function Dialog({ title, children, onClose, wide }: { title: string; children: ReactNode; onClose: () => void; wide?: boolean }) {
  const ref = useRef<HTMLDivElement>(null);
  const closeRef = useRef(onClose);
  useEffect(() => { closeRef.current = onClose; });
  const triggerRef = useRef<HTMLElement | null>(typeof document === 'undefined' ? null : (document.activeElement as HTMLElement | null)); // read before autoFocus runs
  useEffect(() => {
    const trigger = triggerRef.current;
    const node = ref.current;
    if (node && !node.contains(document.activeElement)) (node.querySelector<HTMLElement>('[data-autofocus]') ?? node).focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { e.stopPropagation(); closeRef.current(); return; }
      if (e.key !== 'Tab' || !node) return;
      const items = Array.from(node.querySelectorAll<HTMLElement>(FOCUSABLE));
      if (items.length === 0) { e.preventDefault(); return; }
      const firstItem = items[0], last = items[items.length - 1];
      if (!node.contains(document.activeElement)) { e.preventDefault(); firstItem.focus(); }
      else if (e.shiftKey && (document.activeElement === firstItem || document.activeElement === node)) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); firstItem.focus(); }
    };
    document.addEventListener('keydown', onKey, true);
    return () => { document.removeEventListener('keydown', onKey, true); if (trigger?.isConnected) trigger.focus(); };
  }, []);
  return (
    <div className="overlay" role="presentation" onMouseDown={(e) => e.target === e.currentTarget && closeRef.current()}>
      <div className={wide ? 'dialog wide' : 'dialog'} role="dialog" aria-modal="true" aria-label={title} tabIndex={-1} ref={ref}>
        <h3>{title}</h3>
        {children}
      </div>
    </div>
  );
}

/** Every remote action goes through an explicit confirmation naming the target and the operation. */
export function ConfirmDialog(props: {
  title: string;
  message: ReactNode;
  confirmLabel: string;
  danger?: boolean;
  busy?: boolean;
  /** For destructive actions: the operator must type this text to enable the button. */
  requireText?: string;
  /** Replaces "Type X to confirm" when the caller words it differently (for example for a host name or a count). */
  typedLabel?: ReactNode;
  /** Keeps Run disabled for reasons the dialog cannot see (a missing reason, a failed pre-flight). */
  confirmDisabled?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const [typed, setTyped] = useState('');
  const locked = !!props.confirmDisabled || (props.requireText != null && typed.trim().toLowerCase() !== props.requireText.toLowerCase());
  return (
    <Dialog title={props.title} onClose={props.onCancel}>
      <div>{props.message}</div>
      {props.requireText != null && (
        <div className="form-row" style={{ marginTop: 12 }}>
          <label htmlFor="confirm-text">{props.typedLabel ?? <>Type <b>{props.requireText}</b> to confirm</>}</label>
          <input id="confirm-text" data-autofocus value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" spellCheck={false}
            onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); if (!props.busy && !locked) props.onConfirm(); } }} />
        </div>
      )}
      <div className="actions">
        <button onClick={props.onCancel} disabled={props.busy}>Cancel</button>
        <button className={props.danger ? 'danger' : 'primary'} onClick={props.onConfirm} disabled={props.busy || locked}>
          {props.busy ? 'Working...' : props.confirmLabel}
        </button>
      </div>
    </Dialog>
  );
}

export function ErrorNotice({ error }: { error: unknown }) {
  if (!error) return null;
  const message = error instanceof ApiError ? error.message : error instanceof Error ? error.message : String(error);
  return <div className="notice error" role="alert">{message}</div>;
}

export function Loading({ label = 'Loading' }: { label?: string }) {
  return (
    <div role="status" aria-live="polite">
      <div className="spinner" />
      <div className="muted" style={{ textAlign: 'center' }}>{label}...</div>
    </div>
  );
}

/** dd.MM.yyyy HH:mm, Asia/Yerevan (see formatMinute): the same in every browser and on every PC. */
export const fmtDate = (iso: string | null | undefined) => (iso ? formatMinute(iso) : '-');

export function fmtUptime(seconds: number | null) {
  if (seconds == null) return '-';
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  return d > 0 ? `${d}d ${h}h` : h > 0 ? `${h}h ${m}m` : `${m}m`;
}

export const fmtBytes = (n: number) =>
  n >= 1 << 30 ? `${(n / (1 << 30)).toFixed(1)} GB` : n >= 1 << 20 ? `${(n / (1 << 20)).toFixed(0)} MB` : `${(n / 1024).toFixed(0)} KB`;

export function Meter({ value, kind = 'cpu' }: { value: number | null; kind?: MetricKind }) {
  const th = useThresholds();
  if (value == null) return <span className="muted">-</span>;
  return (
    <span>
      <span className="bar" aria-hidden="true"><i style={{ width: `${Math.min(100, value)}%`, background: metricColorFor(Math.round(value), kind, th) }} /></span>
      {value.toFixed(0)}%
    </span>
  );
}

export function OnlineTag({ online }: { online: boolean }) {
  return <span className={`tag ${online ? 'ok' : 'bad'}`}>{online ? 'Online' : 'Offline'}</span>;
}

export function GroupChips({ ids, groups }: { ids: string[]; groups: { id: string; name: string }[] | undefined }) {
  if (!ids.length || !groups) return null;
  return <>{ids.map((id) => { const g = groups.find((x) => x.id === id); return g ? <span key={id} className="chip">{g.name}</span> : null; })}</>;
}

export function DiskHealthTag({ status, percent }: { status: string | null; percent?: number | null }) {
  if (!status || status === 'Unknown') return <span className="muted">-</span>;
  const cls = status === 'OK' ? 'ok' : status === 'Warning' ? 'warn' : 'bad';
  const icon = status === 'OK' ? '✓' : status === 'Warning' ? '!' : '✕';
  return <span className={`tag ${cls}`} title={status}>{icon} {percent != null ? `${percent}%` : status}</span>;
}

type SortValue = string | number | null | undefined;
type Dir = 'asc' | 'desc';

const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

/**
 * Click-to-sort columns. `accessors` maps a column key to the value to sort by; empty values always sort last.
 * Returns the sorted rows and a `th` helper that renders an accessible sortable header.
 */
export function useSort<T>(rows: T[], accessors: Record<string, (row: T) => SortValue>, initial?: { key: string; dir: Dir }) {
  const [sort, setSort] = useState<{ key: string; dir: Dir } | undefined>(initial);

  // The accessors close over live data (measured sizes, thresholds). A memo keyed only on [rows, sort] kept showing the old order after that data
  // changed, so the sort keys themselves are compared: same rows, same sort and the same key of every row return the SAME array (stable for the
  // table's own memos); any change of a key sorts again.
  const cache = useRef<{ rows: T[]; sort?: { key: string; dir: Dir }; keys: SortValue[]; result: T[] } | null>(null);
  const sorted = (() => {
    const get = sort ? accessors[sort.key] : undefined;
    if (!sort || !get) return rows;
    const keys = rows.map(get);
    const hit = cache.current;
    if (hit && hit.rows === rows && hit.sort === sort && hit.keys.length === keys.length && hit.keys.every((k, i) => k === keys[i])) return hit.result;
    const sign = sort.dir === 'asc' ? 1 : -1;
    const order = keys.map((_, i) => i).sort((i, j) => {
      const x = keys[i];
      const y = keys[j];
      const xe = x == null || x === '';
      const ye = y == null || y === '';
      if (xe || ye) return xe && ye ? 0 : xe ? 1 : -1; // empty last, whichever direction
      if (typeof x === 'number' && typeof y === 'number') return (x - y) * sign;
      return collator.compare(String(x), String(y)) * sign;
    });
    const result = order.map((i) => rows[i]);
    cache.current = { rows, sort, keys, result };
    return result;
  })();

  const th = (key: string, label: ReactNode, className?: string) => {
    const active = sort?.key === key;
    return (
      <th className={className} aria-sort={active ? (sort!.dir === 'asc' ? 'ascending' : 'descending') : 'none'}>
        <button className="sort" onClick={() => setSort(active && sort!.dir === 'asc' ? { key, dir: 'desc' } : { key, dir: 'asc' })}>
          {label}<span aria-hidden="true" className="sort-arrow">{active ? (sort!.dir === 'asc' ? '▲' : '▼') : '↕'}</span>
        </button>
      </th>
    );
  };

  const toggle = (key: string) => setSort(sort?.key === key && sort.dir === 'asc' ? { key, dir: 'desc' } : { key, dir: 'asc' });

  return { sorted, th, sort, toggle };
}

/** CSV with a UTF-8 BOM (Excel), quoting, and a leading apostrophe on cells that would run as formulas. */
export function downloadCsv(filename: string, header: string[], rows: (string | number | null | undefined)[][]) {
  const cell = (v: string | number | null | undefined) => {
    let s = v == null ? '' : String(v);
    if (/^[=+\-@\t\r]/.test(s)) s = "'" + s;
    return /[",\r\n;]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  };
  const text = [header, ...rows].map((r) => r.map(cell).join(',')).join('\r\n');
  const url = URL.createObjectURL(new Blob(['\uFEFF' + text], { type: 'text/csv;charset=utf-8' }));
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

export const fmtPowerOn = (hours: number | null) => {
  if (hours == null) return '-';
  const days = Math.floor(hours / 24);
  return days >= 365 ? `${(days / 365).toFixed(1)} years` : `${days} days`;
};

/** Placeholder rows while data loads: the table keeps its height instead of jumping. */
export function SkeletonRows({ rows = 5, cols = 5 }: { rows?: number; cols?: number }) {
  return (
    <>
      {Array.from({ length: rows }, (_, r) => (
        <tr key={r} className="skeleton-row" aria-hidden="true">
          {Array.from({ length: cols }, (_, c) => <td key={c}><span className="skeleton" style={{ width: `${55 + ((r * 7 + c * 13) % 40)}%` }} /></td>)}
        </tr>
      ))}
    </>
  );
}
