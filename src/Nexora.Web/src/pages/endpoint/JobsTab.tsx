import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { get } from '../../api';
import { RelativeTime } from '../../components/EndpointParts';
import { useCatalogIndex } from '../../jobs/catalog';
import { JobStatusBadge, RiskBadge, TargetCounts, fmtDuration } from '../../jobs/JobParts';
import type { JobSummary } from '../../jobs/jobTypes';
import { endpointJobPath } from '../../nav/origin';
import { useHubOn } from '../../realtime';
import type { EndpointSummary, Me, Paged } from '../../types';
import { ErrorNotice, Loading, fmtDate } from '../../ui';

export function useEndpointJobs(endpointId: string, page = 1, pageSize = 25) {
  return useQuery({
    queryKey: ['jobs-list', 'endpoint', endpointId, page, pageSize],
    queryFn: () => get<Paged<JobSummary>>(`/api/jobs?endpointId=${endpointId}&page=${page}&pageSize=${pageSize}`),
  });
}

/** Jobs that targeted this workstation. Each job opens under this workstation, and Back returns to the workstation page. */
export function JobsTab({ e, me }: { e: EndpointSummary; me: Me }) {
  const qc = useQueryClient();
  const [page, setPage] = useState(1);
  const q = useEndpointJobs(e.id, page);
  const index = useCatalogIndex();
  useHubOn('JobChanged', () => { void qc.invalidateQueries({ queryKey: ['jobs-list', 'endpoint', e.id] }); });
  const pages = q.data ? Math.max(1, Math.ceil(q.data.total / q.data.pageSize)) : 1;
  const isAdmin = me.role === 'Admin' || me.role === 'SuperAdmin';

  return (
    <>
      <p className="sub">{isAdmin ? 'All jobs that ran on this workstation.' : 'Your jobs that ran on this workstation.'}</p>
      {q.isLoading && <Loading />}
      <ErrorNotice error={q.error} />
      {q.data && (
        <>
          <div className="tablewrap">
            <table aria-label="Jobs on this workstation">
              <thead><tr><th>Started</th><th>Action</th><th>Risk</th>{isAdmin && <th>Operator</th>}<th>Targets</th><th>Status</th><th>Duration</th></tr></thead>
              <tbody>
                {q.data.items.length === 0 && <tr><td colSpan={isAdmin ? 7 : 6} className="empty">No jobs have run on this workstation yet. Start one from Remote Actions.</td></tr>}
                {q.data.items.map((j) => (
                  <tr key={j.id}>
                    <td title={fmtDate(j.createdUtc)}><RelativeTime iso={j.createdUtc} online={false} /></td>
                    <td><Link to={endpointJobPath(e.hostname, j.id, { from: `/endpoints/${encodeURIComponent(e.hostname)}/jobs` })}><b>{j.actionName}</b></Link>{index.get(j.actionCode)?.command && <span className="acmd mono">{index.get(j.actionCode)?.command}</span>}</td>
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
