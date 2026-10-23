import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { endpointPath } from '../nav/origin';
import { get } from '../api';
import { RangeTabs, StatLine, TimeChart } from '../charts';
import type { Dashboard as DashboardData, FleetPoint, Range } from '../types';
import { useCatalogIndex } from '../jobs/catalog';
import { auditActionName } from '../lib/auditLabel';
import { formatMinute } from '../lib/format';
import { useThresholds } from '../lib/thresholds';
import { DiskHealthTag, ErrorNotice, Loading, Meter, fmtDate } from '../ui';

function Tile({ to, n, label, cls = '', title }: { to: string; n: number; label: string; cls?: string; title?: string }) {
  return (
    <Link to={to} className={`tile ${cls}`} title={title ?? `Show these ${label.toLowerCase()} in the list`} style={{ textDecoration: 'none', color: 'inherit' }}>
      <div className="n">{n}</div><div className="l">{label}</div>
    </Link>
  );
}

const ALERT_LABEL: Record<string, string> = { cpu: 'CPU', ram: 'RAM', disk: 'Disk space', diskhealth: 'Drive health' };

export function Dashboard() {
  const [range, setRange] = useState<Range>('6h');
  const index = useCatalogIndex();
  const q = useQuery({ queryKey: ['dashboard'], queryFn: () => get<DashboardData>('/api/dashboard') });
  const fleet = useQuery({
    queryKey: ['fleet-history', range],
    queryFn: () => get<FleetPoint[]>(`/api/dashboard/history?range=${range}`),
    refetchInterval: 60_000,
  });
  const t = useThresholds();
  if (q.isLoading) return <Loading />;
  if (q.error || !q.data) return <><ErrorNotice error={q.error} /><button onClick={() => void q.refetch()}>Try again</button></>;
  const d = q.data;
  const f = fleet.data ?? [];

  return (
    <>
      <h1>Dashboard</h1>
      <p className="sub" data-testid="dashboard-updated">
        Live fleet status; the figures change as PCs are polled. Updated {q.dataUpdatedAt ? formatMinute(q.dataUpdatedAt) : '-'}{' '}
        <button className="small" onClick={() => void q.refetch()} disabled={q.isFetching}>{q.isFetching ? 'Refreshing...' : 'Refresh'}</button>
      </p>

      {/* Each card opens the Workstations list with the filter that produces its number, so the count and the list always agree. */}
      <div className="tiles">
        <Tile to="/endpoints" n={d.total} label="Workstations" />
        <Tile to="/endpoints?status=online" n={d.online} label="Online" cls="accent" />
        <Tile to="/endpoints?status=offline" n={d.offline} label="Offline" cls={d.offline > 0 ? 'bad' : ''} />
        <Tile to="/endpoints?alert=cpu" n={d.cpuAlerts} label="CPU alerts" cls={d.cpuAlerts > 0 ? 'alert' : ''} title={`Online PCs with CPU at ${t.cpuAlertPercent}% or more`} />
        <Tile to="/endpoints?alert=ram" n={d.ramAlerts} label="RAM alerts" cls={d.ramAlerts > 0 ? 'alert' : ''} title={`Online PCs with RAM at ${t.ramAlertPercent}% or more`} />
        <Tile to="/endpoints?alert=disk" n={d.diskAlerts} label="Low disk space" cls={d.diskAlerts > 0 ? 'alert' : ''} title={`Online PCs with a disk ${t.diskAlertPercent}% full or more`} />
        <Tile to="/endpoints?health=attention" n={d.diskHealthAlerts} label="Drive health alerts" cls={d.diskHealthAlerts > 0 ? 'bad' : ''} title="Online PCs whose weakest drive is Warning or Critical" />
      </div>

      <h2>Fleet load</h2>
      <div className="toolbar"><RangeTabs value={range} onChange={setRange} /></div>
      <div className="charts-grid">
        <TimeChart
          title="Average CPU, online PCs"
          times={f.map((p) => Date.parse(p.timeUtc))}
          series={[{ key: 'cpu', label: 'CPU', color: 'var(--series-1)', values: f.map((p) => p.avgCpu) }]}
          headline={<StatLine values={f.map((p) => p.avgCpu)} />}
        />
        <TimeChart
          title="Average RAM, online PCs"
          times={f.map((p) => Date.parse(p.timeUtc))}
          series={[{ key: 'ram', label: 'RAM', color: 'var(--series-2)', values: f.map((p) => p.avgRam) }]}
          headline={<StatLine values={f.map((p) => p.avgRam)} />}
        />
      </div>

      <h2>Workstations needing attention</h2>
      {d.alertEndpointTotal > d.alertEndpoints.length && (
        <p className="muted" role="status" data-testid="alert-cap">
          Showing the worst {d.alertEndpoints.length} of {d.alertEndpointTotal}. <Link to="/endpoints?health=attention">See them all in the list</Link>.
        </p>
      )}
      <div className="tablewrap">
        <table>
          <thead><tr><th>Workstation</th><th>User</th><th>CPU</th><th>RAM</th><th>Disk space</th><th>Drives</th><th>Alerts</th></tr></thead>
          <tbody>
            {d.alertEndpoints.length === 0 && <tr><td colSpan={7} className="empty">Nothing needs attention.</td></tr>}
            {d.alertEndpoints.map((e) => (
              <tr key={e.id}>
                <td><Link to={endpointPath(e.hostname)}>{e.name}</Link>{e.name !== e.hostname && <span className="muted"> {e.hostname}</span>}</td>
                <td>{e.lastUser ?? '-'}</td>
                <td><Meter value={e.cpuPercent} kind="cpu" /></td>
                <td><Meter value={e.ramPercent} kind="ram" /></td>
                <td><Meter value={e.diskPercent} kind="disk" /></td>
                <td><DiskHealthTag status={e.diskHealth} percent={e.diskHealthPercent} /></td>
                <td>{e.alerts.map((a) => <span key={a} className="tag warn" style={{ marginRight: 4 }}>{ALERT_LABEL[a] ?? a}</span>)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <h2>Recent actions</h2>
      <div className="tablewrap">
        <table>
          <thead><tr><th>When</th><th>Operator</th><th>Action</th><th>Target</th><th>Result</th></tr></thead>
          <tbody>
            {d.recentActions.length === 0 && <tr><td colSpan={5} className="empty">No remote actions yet.</td></tr>}
            {d.recentActions.map((a) => (
              <tr key={a.id}>
                <td>{fmtDate(a.timestampUtc)}</td>
                <td>{a.actorAccount}</td>
                <td><span className="mono">{a.action}</span>{auditActionName(a, index) && <div><b>{auditActionName(a, index)}</b></div>}</td>
                <td>{a.targetId ?? '-'}</td>
                <td><span className={`tag ${a.success ? 'ok' : 'bad'}`}>{a.success ? 'OK' : 'Failed'}</span></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
