import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { formatClock, formatDayClock, formatMinute, formatSecond } from './lib/format';
import type { Range } from './types';
import { findGaps } from './lib/freshness';

/* Chart system: area + 2px line, hairline grid, crosshair tooltip, range filter above, table view.
   Series colors are CSS variables (--series-1..3) validated against the dark surface. */

export interface Series {
  key: string;
  label: string;
  /** CSS color, normally a var(--series-n). */
  color: string;
  values: (number | null)[];
}

interface TimeChartProps {
  title: string;
  /** Epoch milliseconds, ascending. */
  times: number[];
  series: Series[];
  unit?: string;
  /** Fixed y maximum; omitted = fitted to the data. */
  yMax?: number;
  /** 'zero' keeps the baseline at 0 (default); 'auto' fits a tight range (temperatures). */
  yMin?: 'zero' | 'auto';
  decimals?: number;
  height?: number;
  threshold?: { value: number; label: string };
  headline?: ReactNode;
  emptyText?: string;
}

const M = { top: 12, right: 18, bottom: 26, left: 44 };

function useWidth<T extends HTMLElement>() {
  const ref = useRef<T>(null);
  const [w, setW] = useState(600);
  useLayoutEffect(() => {
    if (!ref.current) return;
    setW(ref.current.clientWidth || 600);
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(([e]) => setW(Math.max(1, Math.floor(e.contentRect.width))));
    ro.observe(ref.current);
    return () => ro.disconnect();
  }, []);
  return [ref, w] as const;
}

function niceCeil(v: number) {
  if (v <= 0) return 10;
  const pow = Math.pow(10, Math.floor(Math.log10(v)));
  const n = v / pow;
  const step = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
  return step * pow;
}

function fmtTime(ms: number, spanMs: number) {
  if (spanMs < 120_000) return formatSecond(ms);
  return spanMs > 36 * 3600_000 ? formatDayClock(ms) : formatClock(ms);
}

const fmtFull = (ms: number) => formatMinute(ms);

export function TimeChart({
  title, times: inputTimes, series: inputSeries, unit = '%', yMax, yMin = 'zero', decimals = 1, height = 210, threshold, headline,
  emptyText = 'No data in this range yet. Samples are collected every polling cycle.',
}: TimeChartProps) {
  const [wrap, width] = useWidth<HTMLDivElement>();
  const [hover, setHover] = useState<number | null>(null);
  const [asTable, setAsTable] = useState(false);
  const uid = useId().replace(/:/g, '');
  const { times, series } = useMemo(() => {
    const indices = inputTimes.map((t, i) => ({ t, i })).filter(p => Number.isFinite(p.t)).sort((a, b) => a.t - b.t);
    const unique = indices.filter((p, i) => i === indices.length - 1 || p.t !== indices[i + 1].t);
    return { times: unique.map(p => p.t), series: inputSeries.map(s => ({ ...s, values: unique.map(p => {
      const v = s.values[p.i];
      return v != null && Number.isFinite(v) && (unit !== '%' || (v >= 0 && v <= 100)) ? v : null;
    }) })) };
  }, [inputTimes, inputSeries, unit]);
  const gapStarts = new Set(findGaps(times).map(g => g.toMs));
  const withSeconds = times.some((time, i) => i > 0 && time - times[i - 1] < 60_000);
  const fullTime = (time: number) => withSeconds ? `${fmtFull(time)}:${formatSecond(time).slice(-2)}` : fmtFull(time);
  const activeHover = hover === null || times.length === 0 ? null : Math.min(hover, times.length - 1);

  const plotW = Math.max(10, width - M.left - M.right);
  const plotH = height - M.top - M.bottom;

  const geo = useMemo(() => {
    const all = series.flatMap((s) => s.values).filter((v): v is number => v != null);
    const max = all.length ? Math.max(...all) : 0;
    const min = all.length ? Math.min(...all) : 0;
    let lo = 0;
    let hi: number;
    if (yMax != null) hi = yMax;
    else if (unit === '%') hi = 100;
    else hi = Math.max(4, niceCeil(max * 1.15));
    if (yMin === 'auto' && all.length) {
      lo = Math.max(0, Math.floor((min - 6) / 5) * 5);
      hi = Math.max(hi, Math.ceil((max + 6) / 5) * 5);
    }
    if (hi <= lo) hi = lo + 4;
    hi = lo + Math.ceil((hi - lo) / 4) * 4; // four equal bands with whole-number ticks
    const t0 = times[0] ?? 0;
    const t1 = Math.max(times[times.length - 1] ?? t0, t0 + 1);
    const x = (t: number) => M.left + ((t - t0) / (t1 - t0)) * plotW;
    const y = (v: number) => M.top + plotH - ((v - lo) / (hi - lo)) * plotH;
    const ticks = Array.from({ length: 5 }, (_, i) => lo + ((hi - lo) * i) / 4);
    const nx = Math.max(2, Math.min(6, Math.floor(plotW / 110)));
    const xTicks = Array.from({ length: nx }, (_, i) => t0 + ((t1 - t0) * i) / (nx - 1));
    return { lo, hi, t0, t1, x, y, ticks, xTicks };
  }, [series, times, yMax, yMin, unit, plotW, plotH]);

  const span = geo.t1 - geo.t0;
  const fmt = (v: number | null) => (v == null ? '-' : `${v.toFixed(decimals)}${unit === '%' ? '%' : ' ' + unit}`);

  // Contiguous runs of non-null values, so a missing sample is a visible gap and not a false line.
  const segments = (s: Series) => {
    const runs: { t: number; v: number }[][] = [];
    let cur: { t: number; v: number }[] = [];
    s.values.forEach((v, i) => {
      if (gapStarts.has(times[i])) { if (cur.length) runs.push(cur); cur = []; }
      if (v == null) { if (cur.length) runs.push(cur); cur = []; } else cur.push({ t: times[i], v });
    });
    if (cur.length) runs.push(cur);
    return runs;
  };

  const onMove = (clientX: number, rect: DOMRect) => {
    const px = clientX - rect.left;
    const t = geo.t0 + ((px - M.left) / plotW) * (geo.t1 - geo.t0);
    let best = 0;
    let bestD = Infinity;
    times.forEach((tt, i) => { const d = Math.abs(tt - t); if (d < bestD) { bestD = d; best = i; } });
    setHover(best);
  };

  const hx = activeHover != null ? geo.x(times[activeHover]) : null;
  const tipLeft = hx == null ? 0 : hx > width * 0.6 ? hx - 188 : hx + 14;

  return (
    <figure className="chart">
      <figcaption className="chart-head">
        <span className="chart-title">{title}</span>
        {headline && <span className="chart-headline">{headline}</span>}
        <span className="spacer" />
        {series.length > 1 && (
          <span className="legend" aria-label="Legend">
            {series.map((s) => (
              <span key={s.key} className="legend-item"><i className="line-key" style={{ background: s.color }} />{s.label}</span>
            ))}
          </span>
        )}
        <button className="small ghost" onClick={() => setAsTable((v) => !v)} aria-pressed={asTable}>{asTable ? 'Chart' : 'Table'}</button>
      </figcaption>

      <div ref={wrap} className="chart-body">
      {times.length === 0 || series.every(s => s.values.every(v => v === null)) ? (
        <div className="empty">{emptyText}</div>
      ) : asTable ? (
        <div className="tablewrap" style={{ maxHeight: height + 40, overflow: 'auto' }}>
          <table>
            <thead><tr><th>Time</th>{series.map((s) => <th key={s.key} className="num">{s.label}</th>)}</tr></thead>
            <tbody>
              {times.map((_, i) => i).reverse().slice(0, 500).map((i) => (
                <tr key={i}><td>{fullTime(times[i])}</td>{series.map((s) => <td key={s.key} className="num">{fmt(s.values[i])}</td>)}</tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="chart-plot" style={{ height }}>
          <svg
            width={width} height={height} role="group" tabIndex={0}
            aria-label={`${title}. Use the left and right arrow keys to read values.`}
            onPointerMove={(e) => onMove(e.clientX, e.currentTarget.getBoundingClientRect())}
            onPointerLeave={() => setHover(null)}
            onFocus={() => setHover((h) => h ?? times.length - 1)}
            onBlur={() => setHover(null)}
            onKeyDown={(e) => {
              if (e.key === 'ArrowLeft') { e.preventDefault(); setHover((h) => Math.max(0, (h ?? times.length - 1) - 1)); }
              if (e.key === 'ArrowRight') { e.preventDefault(); setHover((h) => Math.min(times.length - 1, (h ?? 0) + 1)); }
            }}
          >
            {/* hairline grid + y labels */}
            {geo.ticks.map((t) => (
              <g key={t}>
                <line x1={M.left} x2={width - M.right} y1={geo.y(t)} y2={geo.y(t)} className="grid" />
                <text x={M.left - 8} y={geo.y(t) + 4} textAnchor="end" className="axis">{unit === '%' ? `${Math.round(t)}%` : Math.round(t)}</text>
              </g>
            ))}
            {geo.xTicks.map((t, i) => (
              <text key={i} x={geo.x(t)} y={height - 6} textAnchor={i === 0 ? 'start' : i === geo.xTicks.length - 1 ? 'end' : 'middle'} className="axis">
                {fmtTime(t, span)}
              </text>
            ))}

            {threshold && threshold.value >= geo.lo && threshold.value <= geo.hi && (
              <g>
                <line x1={M.left} x2={width - M.right} y1={geo.y(threshold.value)} y2={geo.y(threshold.value)} className="threshold" />
                <text x={width - M.right - 4} y={geo.y(threshold.value) - 5} textAnchor="end" className="axis">{threshold.label}</text>
              </g>
            )}

            <defs>
              {series.map((s) => (
                <linearGradient key={s.key} id={`${uid}-${s.key}`} x1="0" x2="0" y1="0" y2="1">
                  <stop offset="0%" stopColor={s.color} stopOpacity="0.22" />
                  <stop offset="100%" stopColor={s.color} stopOpacity="0.02" />
                </linearGradient>
              ))}
            </defs>

            {series.map((s) =>
              segments(s).map((run, ri) => {
                const pts = run.map((p) => `${geo.x(p.t).toFixed(1)},${geo.y(p.v).toFixed(1)}`);
                const base = geo.y(geo.lo).toFixed(1);
                return (
                  <g key={`${s.key}-${ri}`}>
                    {series.length === 1 && run.length > 1 && (
                      <path d={`M${pts[0]} L${pts.join(' L')} L${geo.x(run[run.length - 1].t).toFixed(1)},${base} L${geo.x(run[0].t).toFixed(1)},${base} Z`} fill={`url(#${uid}-${s.key})`} />
                    )}
                    <path d={`M${pts.join(' L')}`} fill="none" stroke={s.color} strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />
                    {run.length === 1 && <circle cx={geo.x(run[0].t)} cy={geo.y(run[0].v)} r={3} fill={s.color} />}
                  </g>
                );
              }),
            )}

            {/* end-dot on the latest value, with a surface ring */}
            {hover == null && series.map((s) => {
              const i = s.values.length - 1;
              const v = s.values[i];
              return v == null ? null : <circle key={s.key} cx={geo.x(times[i])} cy={geo.y(v)} r={4} fill={s.color} className="dot-ring" />;
            })}

            {hx != null && activeHover != null && (
              <g>
                <line x1={hx} x2={hx} y1={M.top} y2={M.top + plotH} className="crosshair" />
                {series.map((s) => {
                  const v = s.values[activeHover];
                  return v == null ? null : <circle key={s.key} cx={hx} cy={geo.y(v)} r={4.5} fill={s.color} className="dot-ring" />;
                })}
              </g>
            )}
          </svg>

          {activeHover != null && (
            <div className="tooltip" style={{ left: Math.max(4, tipLeft), top: 6 }} role="status">
              <div className="tooltip-time">{fullTime(times[activeHover])}</div>
              {series.map((s) => (
                <div key={s.key} className="tooltip-row">
                  <i className="line-key" style={{ background: s.color }} />
                  <b>{fmt(s.values[activeHover])}</b>
                  <span>{s.label}</span>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
      </div>
    </figure>
  );
}

/** The one range filter, shown in a single row above the charts it scopes. */
export function RangeTabs({ value, onChange }: { value: Range; onChange: (r: Range) => void }) {
  const items: { key: Range; label: string }[] = [
    { key: '1h', label: '1 hour' }, { key: '6h', label: '6 hours' }, { key: '24h', label: '24 hours' }, { key: '7d', label: '7 days' },
  ];
  return (
    <div className="segmented" role="group" aria-label="Time range">
      {items.map((i) => (
        <button key={i.key} className={value === i.key ? 'on' : ''} aria-pressed={value === i.key} onClick={() => onChange(i.key)}>{i.label}</button>
      ))}
    </div>
  );
}

/** Summary line under a chart: latest, average and peak over the visible range. */
export function stats(values: (number | null)[]) {
  const v = values.filter((x): x is number => x != null && Number.isFinite(x));
  if (!v.length) return null;
  const last = values.at(-1);
  return { last: last != null && Number.isFinite(last) ? last : null, avg: v.reduce((a, b) => a + b, 0) / v.length, max: Math.max(...v) };
}

export function StatLine({ values, decimals = 1, unit = '%' }: { values: (number | null)[]; decimals?: number; unit?: string }) {
  const s = stats(values.map(v => unit === '%' && v != null && (v < 0 || v > 100) ? null : v));
  if (!s) return <span className="muted">no data</span>;
  const f = (n: number | null) => n === null ? 'Unavailable' : `${n.toFixed(decimals)}${unit === '%' ? '%' : ' ' + unit}`;
  return <span><b>{f(s.last)}</b> <span className="muted">latest sample &middot; avg {f(s.avg)} &middot; peak {f(s.max)}</span></span>;
}

const STATUS_ICON: Record<string, string> = { OK: '✓', Warning: '!', Critical: '✕', Unknown: '?' };

/** Health gauge. The status is also spelled out with an icon, never color alone. */
export function HealthRing({ value, status, size = 104 }: { value: number | null; status: string; size?: number }) {
  const r = size / 2 - 9;
  const c = 2 * Math.PI * r;
  const pct = value == null ? 0 : Math.max(0, Math.min(100, value));
  const cls = status === 'OK' ? 'ok' : status === 'Warning' ? 'warn' : status === 'Critical' ? 'bad' : 'unk';
  return (
    <div className="ring" style={{ width: size }}>
      <svg width={size} height={size} role="img" aria-label={`Health ${value ?? 'unknown'} percent, ${status}`}>
        <circle cx={size / 2} cy={size / 2} r={r} className="ring-track" />
        <circle cx={size / 2} cy={size / 2} r={r} className={`ring-arc ${cls}`} strokeDasharray={`${(c * pct) / 100} ${c}`}
          transform={`rotate(-90 ${size / 2} ${size / 2})`} />
        <text x="50%" y="50%" textAnchor="middle" dominantBaseline="central" className="ring-value">{value == null ? '-' : `${value}%`}</text>
      </svg>
      <span className={`tag ${cls === 'unk' ? '' : cls}`}>{STATUS_ICON[status] ?? '?'} {status}</span>
    </div>
  );
}

/** Redraws when the container is hidden and shown again (tab switches). */
export function useRedrawKey(dep: unknown) {
  const [k, setK] = useState(0);
  useEffect(() => setK((x) => x + 1), [dep]);
  return k;
}
