import { HDS_LABEL, HDS_TOOLTIP, reasonsTotal, signed } from '../lib/disks';
import type { DiskHealth, DriveTemperature, HealthReason } from '../types';

const tempClass = (s: DriveTemperature['status']): string => (s === 'Critical' ? 'bad' : s === 'Warning' ? 'warn' : s === 'OK' ? 'ok' : '');

/** Label for the score with the short explanation. All text is plain text. */
export function HealthLabel() {
  return (
    <div className="muted" style={{ fontSize: 12, marginTop: 4 }}>
      {HDS_LABEL}{' '}
      <button type="button" className="small" title={HDS_TOOLTIP} aria-label={HDS_TOOLTIP} style={{ cursor: 'help' }}>{'ⓘ'}</button>
    </div>
  );
}

/** Expandable "Why this score?": deductions, notes and points given back, then the equation when it holds. */
export function ReasonsList({ reasons, score }: { reasons: HealthReason[] | null | undefined; score: number | null }) {
  if (!reasons || reasons.length === 0) return null;
  const { sum, holds } = reasonsTotal(reasons, score);
  return (
    <details className="smart" aria-label="Why this score?">
      <summary>Why this score?</summary>
      <ul style={{ margin: '8px 0', paddingLeft: 18 }}>
        {reasons.map((r, i) => (
          <li key={`${r.code}-${i}`}>
            <span className="mono">{r.code}</span>: {r.text}{' '}
            {r.delta < 0 && <b className="bad" data-kind="deduct">{signed(r.delta)}</b>}
            {r.delta === 0 && <span className="tag info">info</span>}
            {r.delta > 0 && <><b className="ok" data-kind="given-back">{signed(r.delta)}</b> <span className="muted">given back</span></>}
          </li>
        ))}
      </ul>
      {holds && <div className="muted" data-testid="reasons-total">Total: 100 {sum < 0 ? `- ${-sum}` : `+ ${sum}`} = {score}</div>}
    </details>
  );
}

/** Temperature, separate from the health score: current, limits with their source, lifetime max, status. */
export function TemperatureBlock({ disk }: { disk: DiskHealth }) {
  const t = disk.temperature;
  if (!t) return null;
  const cur = t.currentC ?? disk.temperatureC;
  return (
    <div className="fact" data-testid="temperature-block">
      <div className="l">Temperature (not part of the health score)</div>
      <div className="v">
        {cur != null ? `${cur} °C` : '-'}{' '}
        {t.status && <span className={`tag ${tempClass(t.status)}`}>{t.status}</span>}
      </div>
      <small className="muted">
        {t.warnC != null && t.critC != null ? `Warn ${t.warnC} °C / Crit ${t.critC} °C ` : ''}
        <span className="tag">{t.source === 'drive' ? 'drive-reported' : 'default (Admin setting)'}</span>
        {t.lifetimeMaxC != null && <> &middot; lifetime max {t.lifetimeMaxC} °C</>}
      </small>
    </div>
  );
}

/** Cable / connection warning (SMART 199). Deliberately not styled as a score problem. */
export function InterfaceWarning({ text }: { text: string | null | undefined }) {
  if (!text) return null;
  return <div style={{ marginTop: 8 }}><span className="tag info" role="status" data-testid="interface-warning">Cable/connection: {text}</span></div>;
}
