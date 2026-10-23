import { useQuery } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { ApiError, get } from '../../api';
import { useCatalog } from '../../jobs/catalog';
import type { CatalogEntry } from '../../jobs/jobTypes';
import { RunActionDialog } from '../../jobs/RunActionDialog';
import { POLL_MAX_MS, capText, confidenceTone, parseRun, pollWhileRunning, severityTone, statusTone } from '../../lib/diagnose';
import { NotFound } from '../../nav/endpointRef';
import { endpointJobPath, endpointPath } from '../../nav/origin';
import { useRealtime } from '../../realtime';
import { roleRank, type DiagnoseAction, type DiagnoseFinding, type DiagnoseRun, type EndpointSummary, type Me } from '../../types';
import { ErrorNotice, Loading, fmtDate } from '../../ui';
import { OperatorRequired } from './liveDetails';
import { useOnJobStarted } from './startedJob';
import { DiagnosticEvidence } from './DiagnosticEvidence';

const EVIDENCE_CAP = 20_000;

export function DiagnoseRunPage({ e, me, runId }: { e: EndpointSummary; me: Me; runId: string }) {
  const canUse = roleRank[me.role] >= roleRank.Operator;
  const since = useRef(Date.now());
  const { connection, epoch } = useRealtime();
  const q = useQuery({
    queryKey: ['diagnose-run', runId], queryFn: async () => parseRun(await get<unknown>(`/api/diagnose/runs/${encodeURIComponent(runId)}`)),
    enabled: canUse,
    retry: (n, err) => !(err instanceof ApiError && [403, 404].includes(err.status)) && n < 1,
    refetchInterval: (query) => pollWhileRunning(query.state.data?.status === 'Running', since.current, Date.now()),
  });
  const jobId = q.data?.jobId ?? null;
  const running = q.data?.status === 'Running';

  // The completion push goes to the job's group: join it while the run is in flight (again after a reconnect), leave on unmount.
  useEffect(() => {
    if (!connection || !jobId || !running) return;
    void connection.invoke('SubscribeJob', jobId).catch(() => undefined);
    return () => { void connection.invoke('UnsubscribeJob', jobId).catch(() => undefined); };
  }, [connection, jobId, running, epoch]);

  // When the polling cap is reached the page stops claiming it is still collecting; the timer dies with the page.
  const [capped, setCapped] = useState(false);
  useEffect(() => {
    if (!running) return;
    const t = window.setTimeout(() => setCapped(true), Math.max(0, POLL_MAX_MS - (Date.now() - since.current)));
    return () => window.clearTimeout(t);
  }, [running]);

  if (!canUse) return <OperatorRequired what="diagnostics" />;
  if (q.isLoading) return <Loading label="Loading diagnosis" />;
  if (q.error instanceof ApiError && q.error.status === 404) return <NotFound what="Diagnosis" detail="This diagnosis does not exist." />;
  if (q.error instanceof ApiError && q.error.status === 403) return <OperatorRequired what="diagnostics" />;
  if (q.error) return <ErrorNotice error={q.error} />;
  if (!q.data || (q.data.endpointId && q.data.endpointId !== e.id)) return <NotFound what="Diagnosis" detail="This diagnosis does not belong to this workstation." />;
  return <RunView run={q.data} e={e} me={me} capped={capped} />;
}

function RunView({ run, e, me, capped }: { run: DiagnoseRun; e: EndpointSummary; me: Me; capped: boolean }) {
  const running = run.status === 'Running';
  const bad = run.collectors.filter((c) => c.status !== 'ok');
  return (
    <>
      <section className="icard wide" aria-label="Diagnosis summary">
        <dl className="dg-meta">
          <div><dt>Profile</dt><dd className="dg-trunc" title={run.profileName}>{run.profileName}</dd></div>
          <div><dt>Started</dt><dd>{fmtDate(run.startedUtc)}</dd></div>
          <div><dt>Finished</dt><dd>{run.finishedUtc ? fmtDate(run.finishedUtc) : '-'}</dd></div>
          <div><dt>Status</dt><dd><span className={`tag ${statusTone(run.status)}`}>{run.status}</span></dd></div>
        </dl>
      </section>

      {running ? (
        <div className="icard wide" role="status" data-testid="dg-running">
          {capped
            ? <p className="muted" style={{ textAlign: 'center' }} data-testid="dg-capped">Still running - check the job page.</p>
            : <><div className="spinner" /><p className="muted" style={{ textAlign: 'center' }}>Collecting evidence from <span className="mono">{e.hostname}</span>...</p></>}
          {run.jobId && <p style={{ textAlign: 'center' }}><Link to={endpointJobPath(e.hostname, run.jobId, { from: endpointPath(e.hostname, 'diagnose') })}>View the job</Link></p>}
        </div>
      ) : (
        <>
          {run.status === 'Failed' && <div className="notice error" role="alert">The diagnosis failed before it could produce results.</div>}
          {!run.complete && (
            <div className="notice warn" role="status" data-testid="dg-incomplete">
              <b>Incomplete result.</b> Some checks did not finish, so the findings below may be partial.
              {bad.length > 0 && (
                <ul className="dg-collectors">
                  {bad.map((c) => <li key={c.id} className="dg-wrap"><span className="mono">{c.id}</span>: {c.status}{c.reason ? ` - ${c.reason}` : ''}</li>)}
                </ul>
              )}
            </div>
          )}
          <Findings findings={run.findings} e={e} me={me} />
          {run.notEvaluated.length > 0 && (
            <details className="icard wide dg-details" data-testid="dg-notevaluated">
              <summary>Not evaluated ({run.notEvaluated.length})</summary>
              <p className="muted small">These rules were skipped because there was not enough data.</p>
              <ul>{run.notEvaluated.map((n, i) => <li key={`${n}-${i}`} className="dg-wrap">{n}</li>)}</ul>
            </details>
          )}
          <RawEvidence runId={run.id} />
        </>
      )}
    </>
  );
}

function Findings({ findings, e, me }: { findings: DiagnoseFinding[]; e: EndpointSummary; me: Me }) {
  if (findings.length === 0) return <div className="empty" data-testid="dg-nofindings">No problems were found with the data collected.</div>;
  return (
    <section aria-label="Findings" className="dg-findings">
      {findings.map((f, i) => <FindingCard key={f.id} f={f} e={e} me={me} rank={i + 1} />)}
    </section>
  );
}

function FindingCard({ f, e, me, rank }: { f: DiagnoseFinding; e: EndpointSummary; me: Me; rank: number }) {
  const [open, setOpen] = useState(false);
  const bodyId = `dg-f-${f.id}`;
  return (
    <article className="icard wide dg-finding" aria-label={`Finding ${rank}`}>
      <button type="button" className="dg-fhead" aria-expanded={open} aria-controls={bodyId} onClick={() => setOpen(!open)}>
        <span className={`tag ${severityTone(f.severity)}`}>{f.severity}</span>
        <span className={`tag ${confidenceTone(f.confidence)}`}>confidence: {f.confidence}</span>
        <span className="dg-cause dg-wrap" title={f.cause}>{f.cause}</span>
      </button>
      {open && (
        <div id={bodyId} className="dg-fbody">
          {f.evidence.length > 0 && (
            <dl className="dg-evidence" aria-label="Evidence">
              {f.evidence.map((ev, i) => <div key={`${ev.label}-${i}`}><dt title={ev.label}>{ev.label}</dt><dd className="dg-wrap" title={ev.value}>{ev.value}</dd></div>)}
            </dl>
          )}
          {f.actions.length > 0 && (
            <div className="dg-actions" aria-label="Recommended actions">
              <b>Recommended</b>
              {f.actions.map((a, i) => <ActionItem key={`${a.label}-${i}`} a={a} e={e} me={me} />)}
            </div>
          )}
        </div>
      )}
    </article>
  );
}

function ActionItem({ a, e, me }: { a: DiagnoseAction; e: EndpointSummary; me: Me }) {
  const catalog = useCatalog();
  const onStarted = useOnJobStarted();
  const [entry, setEntry] = useState<CatalogEntry | null>(null);
  if (a.kind === 'manual') {
    return <div className="dg-manual"><b className="dg-wrap">{a.label}</b>{a.instructions && <p className="dg-instructions dg-wrap">{a.instructions}</p>}</div>;
  }
  const found = catalog.data?.find((c) => c.code === a.actionCode);
  const available = !!found && found.allowed;
  return (
    <div>
      <button className="small" disabled={!available} title={available ? a.label : catalog.isLoading ? 'Loading the action catalog' : 'Not available for your role'}
        onClick={() => found && setEntry(found)}>
        Run: {a.label}
      </button>
      {entry && (
        <RunActionDialog entry={entry} preset={a.params} me={me} targets={[{ id: e.id, name: e.name, hostname: e.hostname }]}
          onClose={() => setEntry(null)} onCreated={(id) => { setEntry(null); onStarted(id); }} />
      )}
    </div>
  );
}

/** Raw collector output, fetched only when the section is opened. Read-only text. */
function RawEvidence({ runId }: { runId: string }) {
  const [open, setOpen] = useState(false);
  const [cap, setCap] = useState(EVIDENCE_CAP);
  const q = useQuery({
    queryKey: ['diagnose-evidence', runId], queryFn: () => get<Record<string, unknown>>(`/api/diagnose/runs/${encodeURIComponent(runId)}/evidence`),
    enabled: open, staleTime: 60_000,
  });
  const text = q.data ? JSON.stringify(q.data, null, 2) : '';
  const { shown, truncated } = capText(text, cap);
  return (
    <details className="icard wide dg-details" data-testid="dg-raw" onToggle={(ev) => setOpen((ev.currentTarget as HTMLDetailsElement).open)}>
      <summary>Collected evidence</summary>
      {open && q.isLoading && <Loading label="Loading evidence" />}
      {open && <ErrorNotice error={q.error} />}
      {open && q.data && (
        <>
          <DiagnosticEvidence evidence={q.data} />
          <details className="dg-details">
          <summary>Raw JSON for troubleshooting</summary>
          <pre className="dg-json" aria-label="Raw evidence JSON">{shown}</pre>
          {truncated && <button className="small" onClick={() => setCap(cap + EVIDENCE_CAP)}>Show more ({Math.round((text.length - shown.length) / 1024)} KB left)</button>}
          </details>
        </>
      )}
    </details>
  );
}
