import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { get } from '../../api';
import { RangeTabs, StatLine, TimeChart } from '../../charts';
import { LiveLoad } from '../../components/DisksOverview';
import { dataFreshness, findGaps, formatDuration, FRESHNESS_TEXT } from '../../lib/freshness';
import { useThresholds } from '../../lib/thresholds';
import { roleRank, type EndpointSummary, type Me, type MetricPoint, type Range } from '../../types';
import { ErrorNotice, Loading, fmtBytes, fmtDate, useSort } from '../../ui';
import { RunActionDialog } from '../../jobs/RunActionDialog';
import type { CatalogEntry } from '../../jobs/jobTypes';
import { useCatalog } from '../../jobs/catalog';
import { LiveError, OperatorRequired, canReadLiveDetails, useLiveDetail } from './liveDetails';
import { useOnJobStarted } from './startedJob';
import { mergePerformanceHistory, useLivePerformance } from './livePerformance';

export function MonitoringTab({ e }: { e: EndpointSummary; me: Me }) {
  const performance = useLivePerformance();
  const [range, setRange] = useState<Range>('6h');
  const thresholds = useThresholds();
  const metrics = useQuery({
    queryKey: ['metrics', e.id, range],
    queryFn: () => get<MetricPoint[]>(`/api/endpoints/${e.id}/metrics?range=${range}`),
    refetchInterval: 30_000,
  });
  const m = metrics.data ?? [];
  const liveMetrics = mergePerformanceHistory(m, performance.samples);
  const liveTimes = liveMetrics.map(p => Date.parse(p.timeUtc));
  const times = m.map((p) => Date.parse(p.timeUtc));
  const gaps = findGaps(times);
  const fresh = dataFreshness(e);
  const lastSample = m.length ? m[m.length - 1].timeUtc : null;

  return (
    <>
      <div className={`notice fresh-${fresh}`} role="status" data-testid="freshness">
        <b>{fresh === 'live' ? 'Live' : fresh === 'stale' ? 'Stale' : fresh === 'offline' ? 'Offline' : 'Unavailable'}</b> · {FRESHNESS_TEXT[fresh]}.
        {' '}Last successful poll: {e.lastSeenUtc ? fmtDate(e.lastSeenUtc) : 'never'}.
        {lastSample && <> Last stored sample in this range: {fmtDate(lastSample)}.</>}
      </div>
      {fresh === 'live' && <LiveLoad e={e} />}
      {e.lastError && !e.online && <div className="notice warn">Last poll failed: {e.lastError}</div>}
      {e.diskScanError && <div className="notice warn" role="status">Drive scan error: {e.diskScanError}</div>}

      <div className="toolbar"><RangeTabs value={range} onChange={setRange} /></div>
      <ErrorNotice error={metrics.error} />
      {metrics.isLoading && <Loading />}
      <div className="charts-grid">
        <TimeChart title="CPU" times={liveTimes} series={[{ key: 'cpu', label: 'CPU', color: 'var(--series-1)', values: liveMetrics.map((p) => p.cpu) }]}
          headline={<StatLine values={liveMetrics.map((p) => p.cpu)} />} threshold={{ value: thresholds.cpuAlertPercent, label: `alert ${thresholds.cpuAlertPercent}%` }} />
        <TimeChart title="RAM" times={liveTimes} series={[{ key: 'ram', label: 'RAM', color: 'var(--series-2)', values: liveMetrics.map((p) => p.ram) }]}
          headline={<StatLine values={liveMetrics.map((p) => p.ram)} />} threshold={{ value: thresholds.ramAlertPercent, label: `alert ${thresholds.ramAlertPercent}%` }} />
        <TimeChart title="Disk (fullest volume)" times={times} series={[{ key: 'disk', label: 'Disk used', color: 'var(--series-3)', values: m.map((p) => p.disk) }]}
          headline={<StatLine values={m.map((p) => p.disk)} />} threshold={{ value: thresholds.diskAlertPercent, label: `alert ${thresholds.diskAlertPercent}%` }} />
      </div>

      <section aria-labelledby="gaps-h" className="icard wide">
        <div className="icard-head"><h3 id="gaps-h">Connectivity changes</h3></div>
        <p className="muted small">Derived from missing samples: an unreachable workstation produces no sample, so a hole in the history is recorded as a gap. It shows that data is missing, not why.</p>
        {gaps.length === 0
          ? <p>No sampling gaps in this range.</p>
          : (
            <table aria-label="Sampling gaps">
              <thead><tr><th>No data from</th><th>to</th><th className="num">Duration</th></tr></thead>
              <tbody>{gaps.map((g) => <tr key={g.fromMs}><td>{fmtDate(new Date(g.fromMs).toISOString())}</td><td>{fmtDate(new Date(g.toMs).toISOString())}</td><td className="num">{formatDuration(g.toMs - g.fromMs)}</td></tr>)}</tbody>
            </table>
          )}
      </section>


    </>
  );
}

/** Processes are read live over WMI on request; stopping one goes through the job pipeline (the confirmation flow and live job popup). Services have their own tab. */
export function LiveProcesses({ e, me }: { e: EndpointSummary; me: Me }) {
  const onStarted = useOnJobStarted();
  const canAct = roleRank[me.role] >= roleRank.Operator;
  const allowed = canReadLiveDetails(me);
  const catalog = useCatalog();
  const [asked, setAsked] = useState(false);
  const [filter, setFilter] = useState('');
  const [job, setJob] = useState<{ entry: CatalogEntry; preset: Record<string, unknown> } | null>(null);
  const q = useLiveDetail(e, me, asked);
  const f = filter.trim().toLowerCase();
  const proc = useSort((q.data?.processes ?? []).filter((p) => !f || (p.name.toLowerCase().includes(f) || String(p.pid).includes(f) || p.owner?.toLowerCase().includes(f))),
    { name: (p) => p.name, pid: (p) => p.pid, mem: (p) => p.workingSetBytes, owner: (p) => p.owner, cpu: (p) => p.cpuPercent }, { key: 'mem', dir: 'desc' });
  const stop = (pid: number, processName: string) => {
    const entry = catalog.data?.find((c) => c.code === 'proc.kill');
    if (entry) setJob({ entry, preset: { pid, processName } });
  };

  return (
    <section aria-labelledby="live-h" className="icard wide">
      <div className="icard-head">
        <h3 id="live-h">Live processes</h3>
        {!allowed ? null : !asked
          ? <button className="small" disabled={!e.online} onClick={() => setAsked(true)}>{e.online ? 'Read from workstation' : 'Offline'}</button>
          : <button className="small" onClick={() => void q.refetch()} disabled={q.isFetching || !e.online}>{q.isFetching ? 'Refreshing...' : 'Refresh'}</button>}
      </div>
      {!e.online && <div className="notice warn" role="status">Workstation offline. Cached processes may have exited; actions are disabled.</div>}
      <p className="muted small">CPU is a sampled percentage of total workstation capacity where available. Unknown metrics and owners are shown as unavailable.</p>
      <p className="muted small">Start and restart require a verified executable and launch arguments, which process inventory does not provide. For managed applications, use Start or Restart in Services.</p>
      {asked && (
        <div className="toolbar">
          <input placeholder="Filter" aria-label="Filter" value={filter} onChange={(ev) => setFilter(ev.target.value)} />
        </div>
      )}
      {q.isFetching && <Loading label="Collecting from the workstation over WMI" />}
      {!allowed && <OperatorRequired />}
      <LiveError error={q.error} />
      {q.data?.warnings.map((w) => <div key={w} className="notice warn">{w}</div>)}
      {q.data && (
        <div className="tablewrap tall"><table aria-label="Processes">
          <thead><tr>{proc.th('name', 'Name')}{proc.th('pid', 'PID', 'num')}{proc.th('mem', 'Memory', 'num')}{proc.th('cpu', 'CPU %', 'num')}{proc.th('owner', 'Owner')}{canAct && <th />}</tr></thead>
          <tbody>{proc.sorted.length === 0 && <tr><td colSpan={canAct ? 6 : 5} className="empty">No matching processes.</td></tr>}{proc.sorted.map((p) => (
            <tr key={p.pid}>
              <td>{p.name}</td><td className="num mono">{p.pid}</td><td className="num">{fmtBytes(p.workingSetBytes)}</td><td className="num">{p.cpuPercent == null ? "Unavailable" : p.cpuPercent.toFixed(1)}</td><td>{p.owner ?? "Unavailable"}</td>
              {canAct && <td>{p.pid > 4 && <button className="small danger" disabled={!e.online || q.isFetching || q.isError} aria-label={`Stop process ${p.name} (${p.pid})`} onClick={() => stop(p.pid, p.name)}>Stop</button>}</td>}
            </tr>))}</tbody></table></div>
      )}
      {job && (
        <RunActionDialog entry={job.entry} preset={job.preset} me={me} targets={[{ id: e.id, name: e.name, hostname: e.hostname }]}
          onClose={() => setJob(null)} onCreated={(id) => { setJob(null); onStarted(id); }} />
      )}
    </section>
  );
}
