import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { ApiError, get, post } from '../../api';
import { parseHistory, parseProfiles, pollWhileRunning, severityTone, statusTone } from '../../lib/diagnose';
import { diagnoseRunPath } from '../../nav/origin';
import { roleRank, type DiagnoseHistoryItem, type EndpointSummary, type Me } from '../../types';
import { ErrorNotice, Loading, fmtDate } from '../../ui';
import { OperatorRequired } from './liveDetails';

export const DISABLED_MESSAGE = 'Diagnostics is not enabled on this server.';

/** Symptom picker + run history. Nothing ever runs by itself: a diagnosis starts only from the button. */
export function DiagnoseTab({ e, me }: { e: EndpointSummary; me: Me }) {
  const canUse = roleRank[me.role] >= roleRank.Operator;
  const nav = useNavigate();
  const qc = useQueryClient();
  const [picked, setPicked] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const since = useRef(Date.now());

  const profiles = useQuery({
    queryKey: ['diagnose-profiles'], queryFn: async () => parseProfiles(await get<unknown>('/api/diagnose/profiles')),
    enabled: canUse, staleTime: 300_000,
  });
  const history = useQuery({
    queryKey: ['diagnose-runs', e.id], queryFn: async () => parseHistory(await get<unknown>(`/api/endpoints/${e.id}/diagnose/runs`)),
    enabled: canUse,
    refetchInterval: (q) => pollWhileRunning(!!q.state.data?.some((r) => r.status === 'Running'), since.current, Date.now()),
  });

  if (!canUse) return <OperatorRequired what="diagnostics" />;

  const running = history.data?.some((r) => r.status === 'Running') ?? false;
  const offline = !e.online;
  const blocked = running ? 'A diagnosis is already running on this workstation.'
    : offline ? 'The workstation is offline; a diagnosis needs it to be reachable.'
    : !picked ? 'Pick a symptom first.' : null;

  const start = async () => {
    if (!picked || blocked) return;
    setStarting(true); setProblem(null);
    try {
      const r = await post<{ runId: string; jobId: string }>(`/api/endpoints/${e.id}/diagnose`, { profileId: picked });
      void qc.invalidateQueries({ queryKey: ['diagnose-runs', e.id] });
      nav(diagnoseRunPath(e.hostname, r.runId));
    } catch (err) {
      if (err instanceof ApiError && err.status === 501) setProblem(`${DISABLED_MESSAGE} ${err.message}`.trim());
      else setProblem(err instanceof Error ? err.message : String(err));
    } finally { setStarting(false); }
  };

  return (
    <>
      <div className="notice" role="note">Rules-based diagnostics · Nexora checks measured evidence against explicit thresholds. Confidence describes the rule match, not certainty of the root cause. Missing checks remain unverified. Nothing is repaired automatically.</div>
      <section aria-labelledby="dg-h" className="icard wide">
        <div className="icard-head"><h3 id="dg-h">What is the problem?</h3></div>
        {profiles.isLoading && <Loading label="Loading symptoms" />}
        <ErrorNotice error={profiles.error} />
        <div className="dg-picker" role="radiogroup" aria-label="Symptom">
          {(profiles.data ?? []).map((p) => (
            <button key={p.id} type="button" role="radio" aria-checked={picked === p.id} className={`dg-card${picked === p.id ? ' on' : ''}`} onClick={() => setPicked(p.id)}>
              <b className="dg-trunc" title={p.name}>{p.name}</b>
              <span className="muted small dg-wrap" title={p.description}>{p.description}</span>
              <span className="muted small">{p.collectors} check{p.collectors === 1 ? '' : 's'}</span>
            </button>
          ))}
          {profiles.data?.length === 0 && <div className="empty">No diagnosis profiles are available.</div>}
        </div>
        <div className="toolbar">
          <button className="primary" onClick={() => void start()} disabled={!!blocked || starting} title={blocked ?? 'Collect evidence from this workstation and rank the likely causes'}>
            {starting ? 'Starting...' : 'Run diagnosis'}
          </button>
          {blocked && (picked || running || offline) && <span className="muted small" data-testid="dg-blocked">{blocked}</span>}
        </div>
        {problem && <div className="notice warn" role="alert" data-testid="dg-problem">{problem}</div>}
      </section>

      <section aria-labelledby="dgh-h" className="icard wide">
        <div className="icard-head"><h3 id="dgh-h">Previous diagnoses</h3></div>
        {history.isLoading && <Loading label="Loading history" />}
        <ErrorNotice error={history.error} />
        {history.data && history.data.length === 0 && <div className="empty">No diagnosis has been run on this workstation yet.</div>}
        {history.data && history.data.length > 0 && (
          <ul className="dg-history" aria-label="Diagnosis history">
            {history.data.map((r) => <HistoryRow key={r.id} r={r} host={e.hostname} />)}
          </ul>
        )}
      </section>
    </>
  );
}

function HistoryRow({ r, host }: { r: DiagnoseHistoryItem; host: string }) {
  return (
    <li>
      <Link to={diagnoseRunPath(host, r.id)} className="dg-row" aria-label={`Diagnosis ${r.id.slice(0, 8)}`}>
        <span className="mono">{fmtDate(r.startedUtc)}</span>
        <span className="dg-trunc" title={r.profileName}>{r.profileName}</span>
        <span className={`tag ${statusTone(r.status)}`}>{r.status}</span>
        <span>{r.findingCount} finding{r.findingCount === 1 ? '' : 's'}</span>
        {r.topSeverity ? <span className={`tag ${severityTone(r.topSeverity)}`}>{r.topSeverity}</span> : <span className="muted">-</span>}
      </Link>
    </li>
  );
}
