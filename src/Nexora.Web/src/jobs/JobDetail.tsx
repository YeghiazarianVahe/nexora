import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { ApiError, get, post } from '../api';
import { EndpointLoadProblem, NotFound, useCanonicalHost, useEndpointByRef } from '../nav/endpointRef';
import { ORIGIN_PARAM, TAB_LABEL, endpointJobPath, endpointPath, jobPath, parseOrigin } from '../nav/origin';
import { useRealtime, useHubOn } from '../realtime';
import type { Me } from '../types';
import { ConfirmDialog, ErrorNotice, Loading, fmtDate } from '../ui';
import { ActionLabel } from './ActionLabel';
import { HelperBanner } from './helper';
import { JobOutputDialog } from './JobOutputDialog';
import { JobStatusBadge, LogViewer, ProgressBar, RiskBadge, TargetCounts, TargetStatusBadge, fmtDuration } from './JobParts';
import { RunActionDialog, type TargetRef } from './RunActionDialog';
import { isFinalJob, isFinalTarget, type CatalogEntry, type JobDetailDto, type JobSummary, type JobTargetInfo } from './jobTypes';

/**
 * A job. `scoped` = opened as /endpoints/:host/jobs/:jobId: the workstation is resolved from the URL, the job must have a target on it,
 * and Back goes to that workstation. Otherwise it is the bulk-job page /jobs/:id.
 */
export function JobDetailPage({ me, scoped = false }: { me: Me; scoped?: boolean }) {
  const { id: bulkId, host = '', jobId } = useParams();
  const id = scoped ? jobId : bulkId;
  const nav = useNavigate();
  const qc = useQueryClient();
  const { connection, epoch } = useRealtime();
  const [search, setSearch] = useSearchParams();
  // Where the user came from, validated: a workstation tab (or /jobs). Anything else, or no value (a direct link), falls back to the Jobs list.
  const origin = parseOrigin(search.get(ORIGIN_PARAM));
  const originRef = scoped ? host : origin?.kind === 'workstation' ? origin.ref : null;
  const originEndpoint = useEndpointByRef(originRef);
  useCanonicalHost(scoped ? originEndpoint.data : undefined, host);
  const matchesWorkstation = origin?.kind === 'workstation'
    && [host, originEndpoint.data?.id].some((ref) => ref?.toLowerCase() === origin.ref.toLowerCase());
  const validOrigin = !scoped || matchesWorkstation ? origin : null;
  const back = validOrigin ?? (scoped ? { kind: 'workstation' as const, path: endpointPath(host), ref: host, tab: 'overview' as const } : { kind: 'jobs' as const, path: '/jobs' });
  const backLabel = back.kind === 'workstation' ? `${originEndpoint.data?.name ?? 'Workstation'} · ${TAB_LABEL[back.tab]}` : 'Jobs';
  const [outputOpen, setOutputOpen] = useState(search.get('output') === '1'); // a job just started from a dialog opens its output window
  const [open, setOpen] = useState<string | null>(null);
  const [cancelling, setCancelling] = useState(false);
  const [rerun, setRerun] = useState(false);

  const q = useQuery({ queryKey: ['job', id], queryFn: () => get<JobDetailDto>(`/api/jobs/${id}`), refetchInterval: 15_000 });
  const catalog = useQuery({ queryKey: ['job-catalog'], queryFn: () => get<CatalogEntry[]>('/api/jobs/catalog'), staleTime: 300_000 });

  // Live: subscribe to this job's group (the server only allows the initiator or an Admin). Groups die with the connection, so subscribe
  // again whenever it is re-established (epoch), and read the job once more for what happened while it was down.
  useEffect(() => {
    if (!connection || !id) return;
    void connection.invoke('SubscribeJob', id).catch(() => undefined);
    return () => { void connection.invoke('UnsubscribeJob', id).catch(() => undefined); };
  }, [connection, epoch, id]);
  useEffect(() => { if (epoch > 1) void qc.invalidateQueries({ queryKey: ['job', id] }); }, [epoch, id, qc]);
  const closeOutput = () => {
    setOutputOpen(false);
    if (search.get('output')) { search.delete('output'); setSearch(search, { replace: true }); }
  };

  useHubOn('JobChanged', (job: JobSummary) => {
    if (job.id !== id) return;
    qc.setQueryData<JobDetailDto>(['job', id], (old) => (old ? { ...old, job } : old));
  });
  useHubOn('JobTargetChanged', (t: JobTargetInfo) => {
    if (t.jobId !== id) return;
    qc.setQueryData<JobDetailDto>(['job', id], (old) =>
      old ? { ...old, targets: old.targets.map((x) => (x.id === t.id ? t : x)) } : old);
  });

  const cancel = useMutation({
    mutationFn: () => post<void>(`/api/jobs/${id}/cancel`),
    onSuccess: () => { setCancelling(false); qc.invalidateQueries({ queryKey: ['job', id] }); },
  });

  if (scoped && originEndpoint.isLoading) return <Loading />;
  if (scoped && (originEndpoint.error || !originEndpoint.data)) return <EndpointLoadProblem error={originEndpoint.error} host={host} />;
  if (q.isLoading) return <Loading />;
  if (scoped && q.error instanceof ApiError && q.error.status === 404) return <NotFound what="Job" detail="This job does not exist." />;
  if (q.error || !q.data) return <><p className="muted"><Link to={back.path}>&larr; {backLabel}</Link></p><ErrorNotice error={q.error} /></>;
  const scopedEndpoint = scoped ? originEndpoint.data : undefined;
  // A job is only shown under a workstation it ran on.
  if (scopedEndpoint && !q.data.targets.some((t) => t.endpointId === scopedEndpoint.id)) {
    return <NotFound what="Job" detail={`This job has no target on ${scopedEndpoint.hostname}.`} />;
  }

  const { job, targets } = q.data;
  const entry = catalog.data?.find((c) => c.code === job.actionCode);
  const active = !isFinalJob(job.status);
  const failed = targets.filter((t) => t.status === 'Failed' || t.status === 'TimedOut');
  const params = (() => { try { return JSON.parse(q.data.parametersJson) as Record<string, string>; } catch { return {}; } })();
  const anyRunning = targets.some((t) => t.status === 'Running');

  return (
    <>
      {scopedEndpoint ? (
        <>
          <nav aria-label="Breadcrumb" className="crumbs">
            <Link to="/endpoints">Endpoints</Link><span aria-hidden="true"> &gt; </span>
            <Link to={endpointPath(scopedEndpoint.hostname)}>{scopedEndpoint.hostname}</Link><span aria-hidden="true"> &gt; </span>
            <span aria-current="page">Job {job.id.slice(0, 8)}</span>
          </nav>
          <p><Link to={back.path} className="back" data-testid="job-back">&larr; {backLabel}</Link></p>
        </>
      ) : (
        <p className="muted"><Link to={back.path} data-testid="job-back">&larr; {backLabel}</Link></p>
      )}
      <div className="toolbar" style={{ marginBottom: 2 }}>
        <h1 style={{ margin: 0 }}><ActionLabel name={job.actionName} command={entry?.command} /></h1>
        <JobStatusBadge status={job.status} /> <RiskBadge risk={job.risk} />
        <div className="spacer" />
        <button onClick={() => setOutputOpen(true)} title="Live output of every workstation in one window">Output window</button>
        {active && <button className="danger" onClick={() => setCancelling(true)} disabled={cancel.isPending}>Cancel job</button>}
        {!active && failed.length > 0 && entry && (
          <button onClick={() => setRerun(true)} title="Starts a new job on the failed and timed-out workstations">Re-run on {failed.length} failed</button>
        )}
      </div>
      <p className="sub">
        Started by <b>{job.initiatorAccount}</b> · {fmtDate(job.createdUtc)}{job.finishedUtc && <> · finished {fmtDate(job.finishedUtc)}</>} · <TargetCounts job={job} />
        {Object.keys(params).length > 0 && <> · <span className="mono">{Object.entries(params).map(([k, v]) => `${k}=${v}`).join(' ')}</span></>}
      </p>
      {job.reason && <div className="notice"><b>Reason:</b> {job.reason}{job.protectedOverride && <> <span className="tag warn">protected override</span></>}</div>}
      {job.status === 'WaitingForHelper' && <HelperBanner />}
      <ErrorNotice error={cancel.error} />

      <div className="tablewrap">
        <table>
          <thead><tr><th /><th>Workstation</th><th>Status</th><th>Progress</th><th>Duration</th><th>Exit</th><th>Result</th></tr></thead>
          <tbody>
            {targets.map((t) => (
              <TargetRows key={t.id} t={t} jobId={job.id} open={open === t.id} onToggle={() => setOpen(open === t.id ? null : t.id)} />
            ))}
          </tbody>
        </table>
      </div>
      {anyRunning && <p className="muted" role="status">Running targets update live.</p>}

      {outputOpen && <JobOutputDialog jobId={job.id} onClose={closeOutput} onRerun={entry && failed.length > 0 ? () => { closeOutput(); setRerun(true); } : undefined} />}
      {cancelling && (
        <ConfirmDialog
          title="Cancel this job" danger confirmLabel="Cancel job" busy={cancel.isPending}
          message={<>Stop <b>{job.actionName}</b>? Targets that have not started are cancelled right away.{entry?.supportsCancel === false ? ' Targets already running cannot be interrupted and will finish.' : ' Running targets are told to stop.'}</>}
          onConfirm={() => cancel.mutate()} onCancel={() => setCancelling(false)}
        />
      )}
      {rerun && entry && (
        <RunActionDialog
          entry={entry} me={me} onClose={() => setRerun(false)}
          targets={failed.map<TargetRef>((t) => ({ id: t.endpointId, name: t.endpointName, hostname: t.host }))}
          preset={params} presetReason={job.reason ?? ''}
          onCreated={(newId) => { setRerun(false); nav(scopedEndpoint ? endpointJobPath(scopedEndpoint.hostname, newId, { from: back.path }) : jobPath(newId, origin?.path)); }}
        />
      )}
    </>
  );
}

function TargetRows({ t, jobId, open, onToggle }: { t: JobTargetInfo; jobId: string; open: boolean; onToggle: () => void }) {
  const running = !isFinalTarget(t.status);
  return (
    <>
      <tr>
        <td style={{ width: 36 }}>
          <button className="ghost small" onClick={onToggle} aria-expanded={open} aria-label={`${open ? 'Hide' : 'Show'} output of ${t.endpointName}`}>{open ? '▾' : '▸'}</button>
        </td>
        <td><Link to={endpointPath(t.host, 'overview')}><b>{t.endpointName}</b></Link><div className="muted mono">{t.host}</div></td>
        <td><TargetStatusBadge status={t.status} />{t.skipReason && <div className="muted">{t.skipReason}</div>}</td>
        <td><ProgressBar value={t.progress} tone={t.status === 'Failed' || t.status === 'TimedOut' ? 'bad' : t.status === 'Warning' ? 'warn' : ''} /></td>
        <td>{fmtDuration(t.durationSeconds)}</td>
        <td className="mono">{t.exitCode ?? '—'}</td>
        <td>{t.resultSummary ?? (running ? <span className="muted">in progress</span> : '—')}</td>
      </tr>
      {open && (
        <tr><td colSpan={7} style={{ background: 'var(--code-bg)' }}><LogViewer jobId={jobId} targetId={t.id} live={running} /></td></tr>
      )}
    </>
  );
}
