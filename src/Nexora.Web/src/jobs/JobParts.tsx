import { useVirtualizer } from '@tanstack/react-virtual';
import { useEffect, useRef, useState } from 'react';
import { useJobLog } from './jobLog';
import type { JobState, JobSummary, LogLineDto, RiskLevel, TargetState } from './jobTypes';

const RISK_TIP: Record<RiskLevel, string> = {
  Low: 'Low risk: runs immediately.',
  Medium: 'Medium risk: asks for confirmation.',
  High: 'High risk: needs a reason and the typed workstation name.',
  Critical: 'Critical: Admin only, a reason and typed confirmation.',
};

/** Low gray, Medium amber, High red, Critical red with a lock. The text says it too, never color alone. */
export function RiskBadge({ risk }: { risk: RiskLevel }) {
  return <span className={`risk ${risk.toLowerCase()}`} title={RISK_TIP[risk]}>{risk === 'Critical' && <span aria-hidden="true">🔒 </span>}{risk}</span>;
}

const JOB_TONE: Record<JobState, string> = {
  Queued: '', WaitingForHelper: 'warn', Running: 'info', Succeeded: 'ok', Warning: 'warn', PartiallyFailed: 'warn', Failed: 'bad', Cancelled: '',
};
const JOB_LABEL: Record<JobState, string> = {
  Queued: 'Queued', WaitingForHelper: 'Waiting for helper', Running: 'Running', Succeeded: 'Succeeded', Warning: 'Warning',
  PartiallyFailed: 'Partially failed', Failed: 'Failed', Cancelled: 'Cancelled',
};
export const JobStatusBadge = ({ status }: { status: JobState }) => <span className={`tag ${JOB_TONE[status]}`}>{JOB_LABEL[status]}</span>;

const TARGET_TONE: Record<TargetState, string> = {
  Queued: '', Running: 'info', Succeeded: 'ok', Warning: 'warn', Failed: 'bad', Cancelled: '', TimedOut: 'bad', Skipped: '',
};
export const TargetStatusBadge = ({ status }: { status: TargetState }) =>
  <span className={`tag ${TARGET_TONE[status]}`}>{status === 'TimedOut' ? 'Timed out' : status}</span>;

export function ProgressBar({ value, tone }: { value: number | null; tone?: string }) {
  if (value == null) return <span className="muted">—</span>;
  return (
    <span className="pbar" role="progressbar" aria-valuenow={value} aria-valuemin={0} aria-valuemax={100}>
      <span className={`pbar-track ${tone ?? ''}`}><i style={{ width: `${value}%` }} /></span>
      <span className="pbar-pct">{value}%</span>
    </span>
  );
}

/** "3 ✓ 1 ✕ 2 running" for a job's targets. */
export function TargetCounts({ job }: { job: JobSummary }) {
  const parts: [string, number, string][] = [
    ['succeeded', job.succeeded + job.warning, 'ok'], ['failed', job.failed + job.timedOut, 'bad'],
    ['running', job.running, 'info'], ['queued', job.queued, ''], ['cancelled', job.cancelled, ''], ['skipped', job.skipped, ''],
  ];
  return (
    <span className="counts">
      {parts.filter(([, n]) => n > 0).map(([label, n, tone]) => <span key={label} className={`count ${tone}`}>{n} {label}</span>)}
      {job.targetCount === 0 && <span className="muted">no targets</span>}
    </span>
  );
}

export const fmtDuration = (seconds: number | null) => {
  if (seconds == null) return '—';
  const s = Math.round(seconds);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  return m < 60 ? `${m}m ${s % 60}s` : `${Math.floor(m / 60)}h ${m % 60}m`;
};

/** The lines of a log, virtualized so very long outputs (sfc, DISM) stay fast. Auto-scroll follows the end while it is on. */
export function LogPane({ lines, auto, height }: { lines: LogLineDto[]; auto: boolean; height?: number | string }) {
  const scroller = useRef<HTMLDivElement>(null);
  const virt = useVirtualizer({ count: lines.length, getScrollElement: () => scroller.current, estimateSize: () => 20, overscan: 20 });
  useEffect(() => {
    if (auto && lines.length > 0) virt.scrollToIndex(lines.length - 1, { align: 'end' });
  }, [lines.length, auto, virt]);

  return (
    <div ref={scroller} className="log" role="log" aria-label="Job output" tabIndex={0} style={height ? { height } : undefined}>
      <div style={{ height: virt.getTotalSize(), position: 'relative' }}>
        {virt.getVirtualItems().map((v) => {
          const l = lines[v.index];
          return (
            <div key={l.seq} className={`logline ${l.stream}`} style={{ transform: `translateY(${v.start}px)` }}>
              <span className="ln">{l.seq}</span>{l.text || ' '}
            </div>
          );
        })}
      </div>
      {lines.length === 0 && <div className="empty">No output yet.</div>}
    </div>
  );
}

/** The output as plain text, as it would be copied or saved. */
export const logText = (lines: LogLineDto[]) => lines.map((l) => l.text).join('\n');

/** Live output of one target (see useJobLog: loads what exists, keeps up with pushes, recovers after a reconnect). */
export function LogViewer({ jobId, targetId, live }: { jobId: string; targetId: string; live: boolean }) {
  const [auto, setAuto] = useState(true);
  const lines = useJobLog(jobId, targetId);
  return (
    <div className="logbox">
      <div className="toolbar" style={{ marginBottom: 6 }}>
        <label className="inline"><input type="checkbox" checked={auto} onChange={(e) => setAuto(e.target.checked)} /> Auto-scroll</label>
        <span className="muted">{lines.length} lines{live && ' · live'}</span>
        <div className="spacer" />
        <a className="button small" href={`/api/jobs/${jobId}/targets/${targetId}/output`}>Download output</a>
      </div>
      <LogPane lines={lines} auto={auto} />
    </div>
  );
}
