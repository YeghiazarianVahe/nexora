import type { ReactNode } from 'react';
import { fmtBytes, fmtDate } from '../../ui';

/** A value that may be missing. Missing is shown as "Unavailable", never as 0 or an invented value. */
export function Val({ v, children }: { v: unknown; children?: ReactNode }) {
  if (v === null || v === undefined || v === '') return <span className="unavail" title="The workstation did not report this value">Unavailable</span>;
  return <>{children ?? String(v)}</>;
}

export const Bytes = ({ v }: { v: number | null | undefined }) => <Val v={v}>{v != null && fmtBytes(v)}</Val>;
export const DateVal = ({ v }: { v: string | null | undefined }) => <Val v={v}>{fmtDate(v)}</Val>;

export function Card({ title, id, children, aside }: { title: string; id: string; children: ReactNode; aside?: ReactNode }) {
  return (
    <section className="icard" aria-labelledby={id}>
      <div className="icard-head"><h3 id={id}>{title}</h3>{aside}</div>
      {children}
    </section>
  );
}

/** Label/value rows as a real description list. */
export function Facts({ rows }: { rows: [string, ReactNode][] }) {
  return (
    <dl className="facts">
      {rows.map(([k, v]) => <div key={k} className="frow"><dt>{k}</dt><dd>{v}</dd></div>)}
    </dl>
  );
}

export function formatUptime(bootIso: string | null | undefined, now = Date.now()): string | null {
  if (!bootIso) return null;
  const s = Math.max(0, Math.floor((now - Date.parse(bootIso)) / 1000));
  const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
  return d > 0 ? `${d}d ${h}h ${m}m` : h > 0 ? `${h}h ${m}m` : `${m}m`;
}

export const mhz = (v: number | null | undefined) => (v == null ? null : v >= 1000 ? `${(v / 1000).toFixed(2).replace(/\.?0+$/, '')} GHz` : `${v} MHz`);
