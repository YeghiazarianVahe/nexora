import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, get } from '../../api';
import { useCatalog } from '../../jobs/catalog';
import type { CatalogEntry } from '../../jobs/jobTypes';
import { RunActionDialog } from '../../jobs/RunActionDialog';
import { disabledReason, parseServices } from '../../lib/services';
import { roleRank, type EndpointSummary, type Me, type ServiceAction, type ServiceRowDto } from '../../types';
import { ErrorNotice, Loading, useSort } from '../../ui';
import { OperatorRequired } from './liveDetails';
import { useOnJobStarted } from './startedJob';

const CODE: Record<ServiceAction, string> = { start: 'svc.start', stop: 'svc.stop', restart: 'svc.restart' };
const LABEL: Record<ServiceAction, string> = { start: 'Start', stop: 'Stop', restart: 'Restart' };
const ORDER: ServiceAction[] = ['start', 'stop', 'restart'];
type StatusFilter = 'all' | 'running' | 'stopped';

/** Services of one workstation. Which buttons are enabled comes only from the server's allowed[]; the server enforces it again on every request. */
export function ServicesTab({ e, me }: { e: EndpointSummary; me: Me }) {
  const onStarted = useOnJobStarted();
  const catalog = useCatalog();
  const canAct = roleRank[me.role] >= roleRank.Operator;
  const [filter, setFilter] = useState('');
  const [status, setStatus] = useState<StatusFilter>('all');
  const [job, setJob] = useState<{ entry: CatalogEntry; preset: Record<string, unknown> } | null>(null);

  const q = useQuery({
    queryKey: ['endpoint-services', e.id],
    queryFn: async () => parseServices(await get<unknown>(`/api/endpoints/${e.id}/services`)),
    retry: (n, err) => !(err instanceof ApiError && (err.status === 403 || err.status === 401)) && n < 1,
    staleTime: 30_000,
    refetchInterval: e.online && canAct ? 15_000 : false,
  });
  const forbidden = q.error instanceof ApiError && q.error.status === 403;
  const items = q.data?.items ?? [];
  const f = filter.trim().toLowerCase();
  const rows = items.filter((s) => (!f || s.name.toLowerCase().includes(f) || s.displayName.toLowerCase().includes(f))
    && (status === 'all' || (status === 'running') === (s.state.toLowerCase() === 'running')));
  const sort = useSort(rows, { name: (s) => s.name, display: (s) => s.displayName, state: (s) => s.state, start: (s) => s.startMode }, { key: 'display', dir: 'asc' });
  const offline = !e.online;
  const showActions = canAct && !forbidden;

  const run = (action: ServiceAction, s: ServiceRowDto) => {
    const entry = catalog.data?.find((c) => c.code === CODE[action]);
    if (entry) setJob({ entry, preset: { serviceName: s.name } });
  };

  return (
    <section aria-labelledby="svc-h" className="icard wide">
      <div className="icard-head">
        <h3 id="svc-h">Services</h3>
        <button className="small" onClick={() => void q.refetch()} disabled={q.isFetching}>{q.isFetching ? 'Refreshing...' : 'Refresh'}</button>
      </div>
      {offline && (
        <div className="notice warn" role="status" data-testid="services-offline">
          {q.isLoading
            ? <>Host offline. Checking for the last known state...</>
            : items.length > 0
              ? <>Host offline. This is the last known state of its services; actions are disabled.</>
              : <>Host offline. Its services cannot be read, and actions are disabled.</>}
        </div>
      )}
      {q.data?.unavailable && !offline && <div className="notice warn" role="status">Services are unavailable right now{q.data.reason ? `: ${q.data.reason}` : '.'}</div>}
      {forbidden
        ? <OperatorRequired what="live services" />
        : <ErrorNotice error={q.error} />}
      <ErrorNotice error={catalog.error} />
      {q.isLoading && <Loading label="Reading services" />}
      {!forbidden && q.data && (
        <>
          <div className="toolbar">
            <input type="search" placeholder="Search services" aria-label="Search services" value={filter} onChange={(ev) => setFilter(ev.target.value)} />
            <select aria-label="Status filter" value={status} onChange={(ev) => setStatus(ev.target.value as StatusFilter)}>
              <option value="all">All statuses</option><option value="running">Running</option><option value="stopped">Not running</option>
            </select>
            <span className="muted small">{rows.length} of {items.length}</span>
          </div>
          <div className="tablewrap tall"><table aria-label="Services">
            <thead><tr>{sort.th('name', 'Name')}{sort.th('display', 'Display name')}{sort.th('state', 'Status')}{sort.th('start', 'Startup type')}{showActions && <th>Actions</th>}</tr></thead>
            <tbody>
              {items.length === 0 && <tr><td colSpan={showActions ? 5 : 4} className="empty">No services to show.</td></tr>}
              {items.length > 0 && sort.sorted.length === 0 && <tr><td colSpan={showActions ? 5 : 4} className="empty">No services match your search and status filter.</td></tr>}
              {sort.sorted.map((s) => (
                <tr key={s.name}>
                  <td className="mono">{s.name}</td>
                  <td>{s.displayName}{s.protected && <> <span className="tag warn" title="Protected: cannot be stopped or restarted">Protected</span></>}</td>
                  <td><span className={`tag ${s.state.toLowerCase() === 'running' ? 'ok' : ''}`}>{s.state}</span></td>
                  <td>{s.startMode}</td>
                  {showActions && (
                    <td style={{ whiteSpace: 'nowrap' }}>
                      {ORDER.map((a) => {
                        const ok = !offline && !q.isFetching && !q.isError && !q.data?.unavailable && s.allowed.includes(a) && !!catalog.data?.some((c) => c.code === CODE[a] && c.allowed && c.implemented);
                        return (
                          <button key={a} className={`small${a === 'stop' ? ' danger' : ''}`} disabled={!ok} aria-label={`${LABEL[a]} ${s.name}`}
                            title={ok ? `${LABEL[a]} ${s.displayName}` : disabledReason(s, a, !offline)}
                            onClick={() => run(a, s)}>{LABEL[a]}</button>
                        );
                      })}
                    </td>
                  )}
                </tr>))}
            </tbody></table></div>
        </>
      )}
      {job && (
        <RunActionDialog entry={job.entry} preset={job.preset} me={me} targets={[{ id: e.id, name: e.name, hostname: e.hostname }]}
          onClose={() => setJob(null)} onCreated={(id) => { setJob(null); onStarted(id); void q.refetch(); }} />
      )}
    </section>
  );
}
