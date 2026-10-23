import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { Link, NavLink, Navigate, useLocation, useNavigate, useParams, type RouteObject } from 'react-router-dom';
import { del } from '../api';
import { RelativeTime } from '../components/EndpointParts';
import { JobDetailPage } from '../jobs/JobDetail';
import { JobOutputDialog } from '../jobs/JobOutputDialog';
import { BackButton, EndpointLoadProblem, useCanonicalHost, useEndpointByRef } from '../nav/endpointRef';
import {
  ENDPOINT_ROUTE_PATHS, ENDPOINT_TABS, TAB_LABEL, backTarget, endpointJobPath, endpointPath, isEndpointTab, normalizeTab, type EndpointTab,
} from '../nav/origin';
import { ProfilesPanel } from '../profiles/ProfilesPanel';
import { useRealtime } from '../realtime';
import { roleRank, type EndpointSummary, type Me } from '../types';
import { ConfirmDialog, GroupChips, Loading, OnlineTag, fmtDate } from '../ui';
import { useGroups } from './Endpoints';
import { ActionsTab } from './endpoint/ActionsTab';
import { DiagnoseRunPage } from './endpoint/DiagnoseRunPage';
import { DiagnoseTab } from './endpoint/DiagnoseTab';
import { EditDialog } from './endpoint/EditDialog';
import { HardwareTab } from './endpoint/HardwareTab';
import { JobsTab } from './endpoint/JobsTab';
import { LogsTab } from './endpoint/LogsTab';
import { LiveProcesses, MonitoringTab } from './endpoint/MonitoringTab';
import { OverviewTab } from './endpoint/OverviewTab';
import { ServicesTab } from './endpoint/ServicesTab';
import { StorageTab } from './endpoint/StorageTab';
import { Val } from './endpoint/parts';
import { StartedJobContext } from './endpoint/startedJob';
import { LivePerformanceContext, useOnScreenPerformance } from './endpoint/livePerformance';
import { LiveLoad } from '../components/DisksOverview';
import { formatSecond } from '../lib/format';

/** The routes of the workstation area: one list for the app and the tests. */
export const endpointRoutes = (me: Me): RouteObject[] => [
  { path: ENDPOINT_ROUTE_PATHS.workstation, element: <EndpointDetailPage me={me} /> },
  { path: ENDPOINT_ROUTE_PATHS.tab, element: <EndpointDetailPage me={me} /> },
  { path: ENDPOINT_ROUTE_PATHS.actionsCategory, element: <EndpointDetailPage me={me} /> },
  { path: ENDPOINT_ROUTE_PATHS.diagnoseRun, element: <EndpointDetailPage me={me} /> },
  { path: ENDPOINT_ROUTE_PATHS.job, element: <JobDetailPage me={me} scoped /> },
];

/**
 * Workstation details: a persistent header and one URL per section (/endpoints/:host/overview, processes, services, profiles, disks,
 * diagnose, jobs, actions, logs), so every tab can be bookmarked and the browser's back/forward buttons walk through the tabs.
 */
export function EndpointDetailPage({ me }: { me: Me }) {
  const params = useParams();
  const { host = '', runId, category } = params;
  const tab = category !== undefined ? 'actions' : params.tab;
  const nav = useNavigate();
  const { pathname, search } = useLocation();
  const qc = useQueryClient();
  const isAdmin = roleRank[me.role] >= roleRank.Admin;
  const { connection } = useRealtime();
  const [editing, setEditing] = useState(false);
  const [removing, setRemoving] = useState(false);
  const [startedJob, setStartedJob] = useState<{ id: string; from: string } | null>(null);
  const groups = useGroups();
  const summary = useEndpointByRef(host);
  const id = summary.data?.id;
  const performance = useOnScreenPerformance(id, summary.data?.hostname);
  useCanonicalHost(summary.data, host);

  useEffect(() => {
    if (!connection || !id) return;
    void connection.invoke('SubscribeEndpoint', id).catch(() => undefined);
    return () => { void connection.invoke('UnsubscribeEndpoint', id).catch(() => undefined); };
  }, [connection, id]);

  const remove = useMutation({
    mutationFn: () => del<void>(`/api/endpoints/${id}`),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['endpoints'] }); nav(backTarget(pathname)); },
  });

  // Live data only: the cached hardware inventory has its own refresh button on the Overview tab.
  const refresh = useMutation({
    mutationFn: async () => {
      await Promise.all([
        qc.invalidateQueries({ queryKey: ['endpoint', id] }), qc.invalidateQueries({ queryKey: ['metrics', id] }),
        qc.invalidateQueries({ queryKey: ['disks', id] }), qc.invalidateQueries({ queryKey: ['jobs-list'] }),
        qc.invalidateQueries({ queryKey: ['endpoint-detail', id] }),
        qc.invalidateQueries({ queryKey: ['endpoint-services', id] }),
      ]);
    },
  });

  // Legacy disks links map to Storage; unknown sections fall back to Overview.
  if (tab !== undefined && !isEndpointTab(tab)) return <Navigate to={`${endpointPath(host, normalizeTab(tab) ?? 'overview')}${search}`} replace />;
  if (summary.isLoading) return <Loading />;
  if (summary.error || !summary.data) return <EndpointLoadProblem error={summary.error} host={host} />;
  const sample = performance.sample;
  const e = sample ? { ...summary.data, cpuPercent: sample.cpuLoadPercent, ramPercent: sample.ramPercentUsed,
    ramTotalBytes: sample.ramTotalBytes, ramFreeBytes: sample.ramFreeBytes } : summary.data;
  const current: EndpointTab = runId ? 'diagnose' : tab ?? 'overview';

  return (
    <div className="wsd">
      <header className="wsd-head">
        <nav aria-label="Breadcrumb" className="crumbs">
          <Link to="/endpoints">Endpoints</Link><span aria-hidden="true"> &gt; </span>
          {tab === undefined && !runId ? <span aria-current="page">{e.hostname}</span> : <Link to={endpointPath(e.hostname)}>{e.hostname}</Link>}
          {tab !== undefined && !runId && <><span aria-hidden="true"> &gt; </span><span aria-current="page">{TAB_LABEL[current]}</span></>}
          {runId && <><span aria-hidden="true"> &gt; </span><span aria-current="page">Diagnosis #{runId.slice(0, 8)}</span></>}
        </nav>
        <div className="wsd-title">
          <BackButton />
          <h1>{e.name}</h1> <OnlineTag online={e.online} />
          {e.online && (e.diskHealth === 'Critical' || e.alerts.length > 0 || e.diskHealth === 'Warning') && <span className={`tag ${e.diskHealth === 'Critical' ? 'bad' : 'warn'}`}>{e.diskHealth === 'Critical' ? 'Critical' : 'Warning'}</span>}
          <GroupChips ids={e.groupIds} groups={groups.data} />
          <div className="spacer" />
          <button onClick={() => refresh.mutate()} disabled={refresh.isPending} title="Reload the live data of this workstation">{refresh.isPending ? 'Refreshing...' : 'Refresh'}</button>
          {isAdmin && <button onClick={() => setEditing(true)}>Edit</button>}
          {isAdmin && <button className="danger" onClick={() => setRemoving(true)}>Remove</button>}
        </div>
        <dl className="wsd-meta" aria-label="Workstation summary">
          <div><dt>Address</dt><dd className="mono">{e.hostname}</dd></div>
          <div><dt>IP address</dt><dd className="mono"><Val v={e.ipAddress} /></dd></div>
          <div><dt>Logged-in user</dt><dd><Val v={e.lastUser} /></dd></div>
          <div><dt>Last successful refresh</dt><dd title={fmtDate(e.lastSeenUtc)}>{e.lastSeenUtc ? <RelativeTime iso={e.lastSeenUtc} online={e.online} /> : <span className="unavail">Never</span>}</dd></div>
        </dl>
      </header>

      <div className="wsd-performance" aria-label="Live CPU and RAM">
        <LiveLoad e={e} />
        <span className="muted small" role="status">{performance.status === 'live' ? 'CPU and RAM update every second' : performance.status === 'paused' ? 'Live updates paused' : performance.status === 'unavailable' ? 'Live updates unavailable; showing the last monitoring sample. Retrying…' : 'Connecting to live CPU and RAM…'}</span>
        {sample && <time className="muted small" dateTime={sample.sampledUtc}>Updated {formatSecond(sample.sampledUtc)}</time>}
      </div>

      <nav className="wsd-tabs" aria-label="Workstation sections">
        {ENDPOINT_TABS.map((t) => (
          <NavLink key={t} to={endpointPath(e.hostname, t)} className={() => (t === current ? 'on' : '')} aria-current={t === current ? 'page' : undefined}>{TAB_LABEL[t]}</NavLink>
        ))}
      </nav>

      {e.lastError && !e.online && <div className="notice warn">Last poll failed: {e.lastError}</div>}

      <LivePerformanceContext.Provider value={performance}>
      <StartedJobContext.Provider value={(jobId) => setStartedJob({ id: jobId, from: runId ? endpointPath(e.hostname, 'diagnose') : pathname + search })}>
        <section className="wsd-body" aria-label={TAB_LABEL[current]}>
          <TabBody tab={current} e={e} me={me} runId={runId} category={category} />
        </section>
      </StartedJobContext.Provider>
      </LivePerformanceContext.Provider>

      {startedJob && <JobOutputDialog jobId={startedJob.id} openJobTo={endpointJobPath(e.hostname, startedJob.id, { from: startedJob.from })} onClose={() => setStartedJob(null)} />}
      {editing && <EditDialog endpoint={e} onClose={() => setEditing(false)} />}
      {removing && (
        <ConfirmDialog
          title="Remove workstation" danger requireText={e.name} busy={remove.isPending}
          message={<>Stop monitoring <b>{e.name}</b> and delete its history? The PC itself is not touched.</>}
          confirmLabel="Remove" onConfirm={() => remove.mutate()} onCancel={() => setRemoving(false)}
        />
      )}
    </div>
  );
}

function TabBody({ tab, e, me, runId, category }: { tab: EndpointTab; e: EndpointSummary; me: Me; runId?: string; category?: string }) {
  switch (tab) {
    case 'overview': return <OverviewTab e={e} />;
    case 'hardware': return <HardwareTab e={e} me={me} />;
    case 'monitoring': return <MonitoringTab e={e} me={me} />;
    case 'processes': return <LiveProcesses e={e} me={me} />;
    case 'services': return <ServicesTab e={e} me={me} />;
    case 'profiles': return <ProfilesPanel endpoint={{ id: e.id, name: e.name, hostname: e.hostname, online: e.online }} me={me} />;
    case 'storage': return <StorageTab e={e} me={me} />;
    case 'diagnose': return runId ? <DiagnoseRunPage e={e} me={me} runId={runId} /> : <DiagnoseTab e={e} me={me} />;
    case 'jobs': return <JobsTab e={e} me={me} />;
    case 'actions': return <ActionsTab e={e} me={me} category={category} />;
    case 'logs': return <LogsTab e={e} me={me} />;
  }
}
