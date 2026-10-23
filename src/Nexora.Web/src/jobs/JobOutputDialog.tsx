import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { get, post } from '../api';
import { useToasts } from '../toasts';
import { ConfirmDialog, Dialog, ErrorNotice, Loading } from '../ui';
import { useJobLog } from './jobLog';
import { isFinalJob, isFinalTarget, type JobDetailDto, type JobTargetInfo } from './jobTypes';
import { JobStatusBadge, LogPane, RiskBadge, TargetStatusBadge, logText } from './JobParts';
import { parseOutput } from './outputParsers';

interface Props {
  jobId: string;
  onClose: () => void;
  /** Starts the same action again on the failed targets (the page owns the dialog that does it). */
  onRerun?: () => void;
  /** Where the job's own page is; shows an "Open job" link when given. */
  openJobTo?: string;
}

/**
 * The output window of a job: the live log of one target in sequence order, a plain-language summary of what sfc / DISM / updates / pending
 * reboot printed, copy and download, cancel and re-run. A job on many workstations shows a list of them to pick from. Esc closes the window
 * and nothing else: it never cancels the job (cancelling is its own button with its own confirmation).
 */
export function JobOutputDialog({ jobId, onClose, onRerun, openJobTo }: Props) {
  const qc = useQueryClient();
  const toasts = useToasts();
  const q = useQuery({ queryKey: ['job', jobId], queryFn: () => get<JobDetailDto>(`/api/jobs/${jobId}`), refetchInterval: 15_000 });
  const [picked, setPicked] = useState<string | null>(null);
  const [auto, setAuto] = useState(true);
  const [confirming, setConfirming] = useState(false);

  const targets = useMemo(() => q.data?.targets ?? [], [q.data]);
  // Default to the target that needs looking at: a failed one, else a running one, else the first.
  const defaultId = useMemo(() => {
    const bad = targets.find((t) => t.status === 'Failed' || t.status === 'TimedOut');
    return (bad ?? targets.find((t) => !isFinalTarget(t.status)) ?? targets[0])?.id ?? null;
  }, [targets]);
  const selectedId = picked && targets.some((t) => t.id === picked) ? picked : defaultId;
  const selected = targets.find((t) => t.id === selectedId) ?? null;

  const cancel = useMutation({
    mutationFn: () => post<void>(`/api/jobs/${jobId}/cancel`),
    onSuccess: () => { setConfirming(false); void qc.invalidateQueries({ queryKey: ['job', jobId] }); },
  });

  if (q.isLoading) return <Dialog title="Job output" onClose={onClose}><Loading /></Dialog>;
  if (q.error || !q.data) return <Dialog title="Job output" onClose={onClose}><ErrorNotice error={q.error} /><div className="actions"><button onClick={onClose}>Close</button></div></Dialog>;

  const { job } = q.data;
  const active = !isFinalJob(job.status);
  const failed = targets.filter((t) => t.status === 'Failed' || t.status === 'TimedOut');

  return (
    // Esc closes only the topmost window: while the cancel confirmation is open it closes that, not this one.
    <Dialog title={`${job.actionName}: output`} onClose={confirming ? () => setConfirming(false) : onClose} wide>
      <div className="toolbar" style={{ marginBottom: 6 }}>
        <JobStatusBadge status={job.status} /> <RiskBadge risk={job.risk} />
        <span className="muted">{targets.length} workstation{targets.length === 1 ? '' : 's'} · started by {job.initiatorAccount}</span>
      </div>

      <div className={targets.length > 1 ? 'output-split' : ''}>
        {targets.length > 1 && (
          <ul className="output-targets" aria-label="Workstations">
            {targets.map((t) => (
              <li key={t.id}>
                <button
                  className={`ghost output-target${t.id === selectedId ? ' on' : ''}`} aria-pressed={t.id === selectedId}
                  onClick={() => setPicked(t.id)} title={t.resultSummary ?? t.status}
                >
                  <b>{t.endpointName}</b> <TargetStatusBadge status={t.status} />
                </button>
              </li>
            ))}
          </ul>
        )}
        <div className="output-main">
          {selected ? <TargetOutput key={selected.id} jobId={jobId} actionCode={job.actionCode} target={selected} auto={auto} onAuto={setAuto} toastsCopy={(ok) => toasts.push(ok ? { tone: 'ok', title: 'Output copied' } : { tone: 'bad', title: 'Could not copy', body: 'The browser did not allow access to the clipboard.' })} /> : <p className="muted">No workstations.</p>}
        </div>
      </div>

      <ErrorNotice error={cancel.error} />
      <div className="actions">
        {active && <button className="danger" onClick={() => setConfirming(true)} disabled={cancel.isPending}>Cancel job</button>}
        {!active && failed.length > 0 && onRerun && <button onClick={onRerun} title="Starts a new job on the failed and timed-out workstations">Re-run on {failed.length} failed</button>}
        {openJobTo && <Link className="button" to={openJobTo}>Open job</Link>}
        <button onClick={onClose} data-autofocus>Close</button>
      </div>

      {confirming && (
        <ConfirmDialog
          title="Cancel this job" danger confirmLabel="Cancel job" busy={cancel.isPending}
          message={<>Stop <b>{job.actionName}</b>? Targets that have not started are cancelled right away; running ones are told to stop. The output window stays open.</>}
          onConfirm={() => cancel.mutate()} onCancel={() => setConfirming(false)}
        />
      )}
    </Dialog>
  );
}

function TargetOutput({ jobId, actionCode, target, auto, onAuto, toastsCopy }: {
  jobId: string; actionCode: string; target: JobTargetInfo; auto: boolean; onAuto: (v: boolean) => void; toastsCopy: (ok: boolean) => void;
}) {
  const lines = useJobLog(jobId, target.id);
  const live = !isFinalTarget(target.status);
  const summary = useMemo(() => parseOutput(actionCode, lines.map((l) => l.text)), [actionCode, lines]);

  const copy = async () => {
    try { await navigator.clipboard.writeText(logText(lines)); toastsCopy(true); } catch { toastsCopy(false); }
  };

  return (
    <div>
      <p className="output-head">
        <b>{target.endpointName}</b> <span className="mono muted">{target.host}</span> <TargetStatusBadge status={target.status} />
        {target.resultSummary && <span className="muted"> · {target.resultSummary}</span>}
      </p>
      {summary && (
        <div className={`notice output-summary ${summary.tone}`} role="status" data-testid="output-summary">
          <b>{summary.headline}</b>
          {summary.details.length > 0 && <ul>{summary.details.map((d) => <li key={d}>{d}</li>)}</ul>}
        </div>
      )}
      <div className="toolbar" style={{ marginBottom: 6 }}>
        <label className="inline"><input type="checkbox" checked={auto} onChange={(e) => onAuto(e.target.checked)} /> Auto-scroll</label>
        <span className="muted">{lines.length} lines{live ? ' · live' : ''}</span>
        <div className="spacer" />
        <button className="small" onClick={copy} disabled={lines.length === 0}>Copy</button>
        <a className="button small" href={`/api/jobs/${jobId}/targets/${target.id}/output`}>Download</a>
      </div>
      <LogPane lines={lines} auto={auto} height={360} />
    </div>
  );
}
