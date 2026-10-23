import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { get } from '../../api';
import { auditActionName } from '../../lib/auditLabel';
import { useCatalogIndex } from '../../jobs/catalog';
import { JobStatusBadge } from '../../jobs/JobParts';
import { endpointJobPath } from '../../nav/origin';
import type { AuditEntry, EndpointSummary, Me, Paged } from '../../types';
import { ErrorNotice, Loading, fmtDate } from '../../ui';
import { LiveError, OperatorRequired, canReadLiveDetails, useLiveDetail } from './liveDetails';
import { useEndpointJobs } from './JobsTab';

/**
 * Two separate things: what Nexora recorded about operator actions on this workstation (job history and audit entries), and the raw
 * Windows event log errors, which are read live over WMI on request. They are never mixed.
 */
export function LogsTab({ e, me }: { e: EndpointSummary; me: Me }) {
  const jobs = useEndpointJobs(e.id, 1, 50);
  const index = useCatalogIndex();
  const audit = useQuery({
    queryKey: ['audit', 'endpoint', e.id, e.hostname],
    queryFn: () => get<Paged<AuditEntry>>(`/api/audit?text=${encodeURIComponent(e.hostname)}&pageSize=50`),
  });

  return (
    <>
      <section aria-labelledby="ops-h" className="icard wide">
        <div className="icard-head"><h3 id="ops-h">Operational audit</h3><span className="muted">What operators did through Nexora</span></div>

        <h4 className="subhead">Job history</h4>
        {jobs.isLoading && <Loading />}
        <ErrorNotice error={jobs.error} />
        {jobs.data && (
          <div className="tablewrap tall">
            <table aria-label="Job history">
              <thead><tr><th>Time</th><th>Action</th><th>Operator</th><th>Outcome</th></tr></thead>
              <tbody>
                {jobs.data.items.length === 0 && <tr><td colSpan={4} className="empty">No jobs have run on this workstation.</td></tr>}
                {jobs.data.items.map((j) => (
                  <tr key={j.id}>
                    <td>{fmtDate(j.createdUtc)}</td>
                    <td><Link to={endpointJobPath(e.hostname, j.id, { from: `/endpoints/${encodeURIComponent(e.hostname)}/logs` })}>{j.actionName}</Link></td>
                    <td>{j.initiatorAccount}</td>
                    <td><JobStatusBadge status={j.status} />{j.failed + j.timedOut > 0 && <span className="muted"> · {j.failed + j.timedOut} failed</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        <h4 className="subhead">Audit entries mentioning {e.hostname}</h4>
        <p className="muted small">Matched by the workstation address. Administrators see every operator; other roles see their own entries.</p>
        <ErrorNotice error={audit.error} />
        {audit.isLoading && <Loading />}
        {audit.data && (
          <div className="tablewrap tall">
            <table aria-label="Audit entries">
              <thead><tr><th>Time</th><th>Operator</th><th>Event</th><th>Result</th></tr></thead>
              <tbody>
                {audit.data.items.length === 0 && <tr><td colSpan={4} className="empty">No audit entries.</td></tr>}
                {audit.data.items.map((a) => (
                  <tr key={a.id}>
                    <td>{fmtDate(a.timestampUtc)}</td><td>{a.actorAccount}</td>
                    <td>{auditActionName(a, index) ?? a.action}<span className="muted mono"> {a.action}</span></td>
                    <td><span className={`tag ${a.success ? 'ok' : 'bad'}`}>{a.success ? 'Success' : 'Failed'}</span></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <WindowsErrors e={e} me={me} />
    </>
  );
}

function WindowsErrors({ e, me }: { e: EndpointSummary; me: Me }) {
  const [asked, setAsked] = useState(false);
  const allowed = canReadLiveDetails(me);
  const q = useLiveDetail(e, me, asked);;
  return (
    <section aria-labelledby="win-h" className="icard wide">
      <div className="icard-head">
        <h3 id="win-h">Windows event log</h3>
        <span className="muted">Error events of the last 24 hours (System, Application), read live over WMI</span>
        <div className="spacer" />
        {!allowed ? null : !asked
          ? <button className="small" disabled={!e.online} onClick={() => setAsked(true)}>{e.online ? 'Read from workstation' : 'Offline'}</button>
          : <button className="small" onClick={() => void q.refetch()} disabled={q.isFetching}>{q.isFetching ? 'Reading...' : 'Refresh'}</button>}
      </div>
      {!e.online && <p className="muted">The workstation is offline; its event log cannot be read.</p>}
      {q.isFetching && <Loading label="Reading the event log over WMI" />}
      {!allowed && <OperatorRequired />}
      <LiveError error={q.error} />
      {q.data && (
        <div className="tablewrap tall">
          <table aria-label="Windows event errors">
            <thead><tr><th>Time</th><th>Log</th><th>Source</th><th className="num">ID</th><th>Message</th></tr></thead>
            <tbody>
              {q.data.errors.length === 0 && <tr><td colSpan={5} className="empty">No errors in the last 24 hours.</td></tr>}
              {q.data.errors.map((x, i) => <tr key={i}><td>{fmtDate(x.timeUtc)}</td><td>{x.log}</td><td>{x.source}</td><td className="num mono">{x.eventId}</td><td>{x.message}</td></tr>)}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
