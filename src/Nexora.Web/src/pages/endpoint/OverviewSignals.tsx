import { useQuery } from '@tanstack/react-query';
import { get } from '../../api';
import { TimeChart } from '../../charts';
import type { DiskHealth, EndpointSummary, MetricPoint } from '../../types';
import { ErrorNotice, Loading, fmtBytes, fmtDate } from '../../ui';
import { Card, Facts, Val } from './parts';
import { mergePerformanceHistory, useLivePerformance } from './livePerformance';

/** Reads stored history only; opening Overview never dispatches a drive scan or a helper job. */
export function OverviewSignals({ e }: { e: EndpointSummary }) {
  const performance = useLivePerformance();
  const metrics = useQuery({ queryKey: ['metrics', e.id, '1h'], queryFn: () => get<MetricPoint[]>(`/api/endpoints/${e.id}/metrics?range=1h`), refetchInterval: 30_000 });
  const disks = useQuery({ queryKey: ['disks', e.id, '6h'], queryFn: () => get<DiskHealth[]>(`/api/endpoints/${e.id}/disks?range=6h`), staleTime: 120_000 });
  const points = mergePerformanceHistory(Array.isArray(metrics.data) ? metrics.data : [], performance.samples);
  const drives = Array.isArray(disks.data) ? disks.data : [];
  // No disk-to-volume association is reported. Never guess that enumeration order identifies the system SSD.
  const ssds = drives.filter(d => d.type === 'SSD');
  const ssd = ssds.length === 1 ? ssds[0] : null;
  const temperature = ssd && !ssd.stale && e.online && Date.now() - Date.parse(ssd.updatedUtc) <= 30 * 60_000 ? ssd.temperatureC : null;
  const volumes = (e.volumes ?? []).filter(v => v.totalBytes > 0 && v.freeBytes >= 0 && v.freeBytes <= v.totalBytes);
  const total = volumes.reduce((sum, v) => sum + v.totalBytes, 0);
  const free = volumes.reduce((sum, v) => sum + v.freeBytes, 0);
  return <>
    <div className="console-section-title"><h2>Performance</h2><span className="muted small">Last hour · stored samples{!e.online ? ' · last known' : ''}</span></div>
    {metrics.isLoading && <Loading label="Loading performance history" />}
    <ErrorNotice error={metrics.error} />
    <div className="charts-grid overview-charts">
      <TimeChart title="CPU usage" height={160} yMax={100} times={points.map(p => Date.parse(p.timeUtc))} series={[{ key: 'cpu', label: 'CPU', color: 'var(--series-1)', values: points.map(p => p.cpu) }]} />
      <TimeChart title="RAM usage" height={160} yMax={100} times={points.map(p => Date.parse(p.timeUtc))} series={[{ key: 'ram', label: 'RAM', color: 'var(--series-2)', values: points.map(p => p.ram) }]} />
    </div>
    <ErrorNotice error={disks.error} />
    <div className="cards overview-storage">
      <Card title="Storage summary" id="ov-storage"><Facts rows={[
        ['Physical disk health', <Val key="h" v={e.diskHealth && e.diskHealth !== 'Unknown' ? `${e.diskHealth}${e.diskHealthPercent != null ? ` · ${e.diskHealthPercent}% estimated` : ''}` : null} />],
        ['SSD temperature', <Val key="t" v={temperature != null && temperature > 0 && temperature < 120 ? `${temperature} °C measured` : null} />],
        ['Logical volume usage', <Val key="v" v={total ? `${fmtBytes(total - free)} used of ${fmtBytes(total)}` : null} />],
        ['Drive health updated', <span key="d">{drives.length ? fmtDate(drives.map(d => d.updatedUtc).sort()[0]) : 'Unavailable'}</span>],
      ]} />{ssds.length > 1 && <p className="muted small">Multiple SSDs reported; temperatures are listed in Storage. The primary SSD is not identified by this inventory.</p>}</Card>
    </div>
  </>;
}
