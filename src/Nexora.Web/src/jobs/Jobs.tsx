import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { get } from '../api';
import { RelativeTime } from '../components/EndpointParts';
import { useHubOn } from '../realtime';
import { roleRank, type Me, type Paged } from '../types';
import { ErrorNotice, Loading, fmtDate } from '../ui';
import { useCatalogIndex } from './catalog';
import { HelperBanner } from './helper';
import { JobStatusBadge, RiskBadge, TargetCounts, fmtDuration } from './JobParts';
import { CATEGORY_LABEL, type CatalogEntry, type JobState, type JobSummary } from './jobTypes';

const STATES: JobState[] = ['Queued', 'WaitingForHelper', 'Running', 'Succeeded', 'Warning', 'PartiallyFailed', 'Failed', 'Cancelled'];

export function Jobs({ me }: { me: Me }) {
  const nav = useNavigate();
  const qc = useQueryClient();
  const isAdmin = roleRank[me.role] >= roleRank.Admin;
  const [status, setStatus] = useState('');
  const [action, setAction] = useState('');
  const [initiator, setInitiator] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [page, setPage] = useState(1);

  const catalog = useQuery({ queryKey: ['job-catalog'], queryFn: () => get<CatalogEntry[]>('/api/jobs/catalog'), staleTime: 300_000 });

  const params = new URLSearchParams({ page: String(page), pageSize: '50' });
  if (status) params.set('status', status);
  if (action) params.set('action', action);
  if (isAdmin && initiator.trim()) params.set('operator', initiator.trim());
  if (from) params.set('from', new Date(from).toISOString());
  if (to) params.set('to', new Date(to + 'T23:59:59').toISOString());
  const key = params.toString();

  const q = useQuery({ queryKey: ['jobs-list', key], queryFn: () => get<Paged<JobSummary>>(`/api/jobs?${key}`) });
  const index = useCatalogIndex();

  // Live: any change to a job refreshes the list (and the detail cache) without polling.
  useHubOn('JobChanged', (job: JobSummary) => {
    qc.setQueryData(['job-summary', job.id], job);
    qc.invalidateQueries({ queryKey: ['jobs-list'] });
  });

  const pages = q.data ? Math.max(1, Math.ceil(q.data.total / q.data.pageSize)) : 1;
  const reset = <T,>(set: (v: T) => void) => (v: T) => { set(v); setPage(1); };
  const grouped = Object.entries((catalog.data ?? []).reduce<Record<string, CatalogEntry[]>>((acc, e) => { (acc[e.category] ??= []).push(e); return acc; }, {}));

  return (
    <>
      <h1>Jobs</h1>
      <p className="sub">{isAdmin ? 'Remote actions started by everyone, live.' : 'Your remote actions, live.'}</p>
      <HelperBanner />

      <div className="filters" role="search" aria-label="Job filters">
        <div className="f"><label htmlFor="jf-status">Status</label>
          <select id="jf-status" value={status} onChange={(e) => reset(setStatus)(e.target.value)}>
            <option value="">Any</option>{STATES.map((s) => <option key={s} value={s}>{s}</option>)}
          </select></div>
        <div className="f"><label htmlFor="jf-action">Action</label>
          <select id="jf-action" value={action} onChange={(e) => reset(setAction)(e.target.value)}>
            <option value="">Any</option>
            {grouped.map(([cat, list]) => (
              <optgroup key={cat} label={CATEGORY_LABEL[cat] ?? cat}>{list.map((a) => <option key={a.code} value={a.code}>{a.displayName}</option>)}</optgroup>
            ))}
          </select></div>
        {isAdmin && (
          <div className="f"><label htmlFor="jf-op">Operator</label>
            <input id="jf-op" placeholder="Name or account" value={initiator} onChange={(e) => reset(setInitiator)(e.target.value)} /></div>
        )}
        <div className="f"><label htmlFor="jf-from">From</label><input id="jf-from" type="date" value={from} onChange={(e) => reset(setFrom)(e.target.value)} /></div>
        <div className="f"><label htmlFor="jf-to">To</label><input id="jf-to" type="date" value={to} onChange={(e) => reset(setTo)(e.target.value)} /></div>
      </div>

      {q.isLoading && <Loading />}
      <ErrorNotice error={q.error} />
      {q.data && (
        <>
          <div className="tablewrap">
            <table>
              <thead>
                <tr><th>Started</th><th>Action</th><th>Risk</th>{isAdmin && <th>Operator</th>}<th>Targets</th><th>Status</th><th>Duration</th></tr>
              </thead>
              <tbody>
                {q.data.items.length === 0 && <tr><td colSpan={isAdmin ? 7 : 6} className="empty">No jobs yet. Start one from a workstation's page.</td></tr>}
                {q.data.items.map((j) => (
                  <tr key={j.id} className="click" tabIndex={0} onClick={() => nav(`/jobs/${j.id}`)} onKeyDown={(e) => e.key === 'Enter' && nav(`/jobs/${j.id}`)}>
                    <td title={fmtDate(j.createdUtc)}><RelativeTime iso={j.createdUtc} online={false} /></td>
                    <td><Link to={`/jobs/${j.id}`} onClick={(e) => e.stopPropagation()}><b>{j.actionName}</b></Link>
                      {index.get(j.actionCode)?.command && <span className="acmd mono">{index.get(j.actionCode)?.command}</span>}
                      {j.protectedOverride && <span className="tag warn" style={{ marginLeft: 6 }} title="Admin override of protected workstations">override</span>}</td>
                    <td><RiskBadge risk={j.risk} /></td>
                    {isAdmin && <td>{j.initiatorAccount}</td>}
                    <td><TargetCounts job={j} /></td>
                    <td><JobStatusBadge status={j.status} /></td>
                    <td>{fmtDuration(j.finishedUtc ? (Date.parse(j.finishedUtc) - Date.parse(j.createdUtc)) / 1000 : null)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="toolbar" style={{ marginTop: 12 }}>
            <button disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button>
            <span className="muted">Page {page} of {pages} · {q.data.total} jobs</span>
            <button disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button>
          </div>
        </>
      )}
    </>
  );
}
