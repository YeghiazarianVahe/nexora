import { useQuery } from '@tanstack/react-query';
import { get } from '../api';
import { formatBytes, formatPair, hddHealth, ramUsedBytes, ssdHealth, systemVolume, volumeUsedPercent } from '../lib/disks';
import { healthColorFor, metricColorFor, useThresholds } from '../lib/thresholds';
import type { DiskHealth, EndpointSummary, Range, Volume } from '../types';
import { ErrorNotice, fmtPowerOn } from '../ui';
import { MetricCell } from './EndpointParts';

/** CPU and RAM now, at the top of the page: the same snapshot the table shows. */
export function LiveLoad({ e }: { e: EndpointSummary }) {
  const used = ramUsedBytes(e);
  return (
    <div className="liveload" role="group" aria-label="Live load">
      <div><span className="l">CPU</span> <MetricCell value={e.cpuPercent} online={e.online} kind="cpu" /></div>
      <div>
        <span className="l">RAM</span> <MetricCell value={e.ramPercent} online={e.online} kind="ram" />
        {e.online && used != null && e.ramTotalBytes != null && <span className="muted num"> {formatBytes(used)} used of {formatBytes(e.ramTotalBytes)}</span>}
      </div>
    </div>
  );
}

function HealthCell({ drive }: { drive: DiskHealth }) {
  const th = useThresholds();
  if (drive.healthPercent == null) {
    return <span className="muted" title="This drive does not report SMART health (some USB, RAID and virtual drives do not).">N/A</span>;
  }
  return (
    <span className="metric" title={drive.summary || drive.status}>
      <span className="metric-bar" aria-hidden="true"><i style={{ width: `${drive.healthPercent}%`, background: healthColorFor(drive.healthPercent, th) }} /></span>
      <span className="metric-pct">{drive.healthPercent}%</span>
    </span>
  );
}

function VolumeCell({ v }: { v: Volume }) {
  const th = useThresholds();
  const used = volumeUsedPercent(v);
  return (
    <span className="volcell" title={`${v.letter} ${v.fileSystem ?? ''}: ${formatBytes(v.freeBytes)} free of ${formatBytes(v.totalBytes)}`}>
      <b>{v.letter}</b> <span className="muted">{v.fileSystem ?? ''}</span>{' '}
      <span className="num">{formatPair(v.freeBytes, v.totalBytes)} free</span>{' '}
      {used != null && <span className="num" style={{ color: metricColorFor(Math.round(used), 'disk', th) }}>{used.toFixed(0)}% used</span>}
    </span>
  );
}

/**
 * Every physical drive of the PC with its health, temperature, power-on time and size, and the volumes with their free space.
 * Reads what the scanner already stored; nothing is collected here.
 */
export function DisksOverview({ e, range }: { e: EndpointSummary; range: Range }) {
  const q = useQuery({
    queryKey: ['disks', e.id, range],
    queryFn: () => get<DiskHealth[]>(`/api/endpoints/${e.id}/disks?range=${range}`),
    refetchInterval: 120_000,
  });
  const drives = q.data ?? [];
  const volumes = e.volumes ?? [];
  const sys = systemVolume(e);
  // The worst-of figures come from the server's one shared helper (the same numbers the table and the CSV show), never re-derived here.
  const ssd = ssdHealth(e);
  const hdd = hddHealth(e);
  const sysUsed = volumeUsedPercent(sys);

  const parts = [
    ssd != null && `SSD health ${ssd}%`,
    hdd != null && `HDD health ${hdd}%`,
    sys && `${sys.letter} ${formatBytes(sys.freeBytes)} free of ${formatBytes(sys.totalBytes)}${sysUsed != null ? ` (${sysUsed.toFixed(0)}% used)` : ''}`,
  ].filter(Boolean) as string[];

  return (
    <section aria-labelledby="disks-h">
      <h2 id="disks-h">Disks</h2>
      <p className="sub num" data-testid="disk-summary">{parts.length ? parts.join(' · ') : 'No drive data has been read from this PC yet.'}</p>
      <ErrorNotice error={q.error} />

      <div className="tablewrap">
        <table className="dtable">
          <thead>
            <tr><th>Drive</th><th>Type</th><th>Health</th><th className="num">Temp</th><th className="num">Power-on</th><th className="num">Size</th><th>Volumes</th></tr>
          </thead>
          <tbody>
            {q.isLoading && <tr><td colSpan={7} className="empty">Reading the stored drive data...</td></tr>}
            {!q.isLoading && drives.length === 0 && <tr><td colSpan={7} className="empty">No drives stored yet. The drive scan runs on its own schedule; use "Read now" below.</td></tr>}
            {drives.map((d) => (
              <tr key={d.diskKey}>
                <td title={`${d.model}${d.serial ? ` · ${d.serial}` : ''}`}><b>{d.model}</b>{d.serial && <div className="mono muted">{d.serial}</div>}</td>
                <td><span className={`tag ${d.type === 'Unknown' ? 'warn' : ''}`} title={d.type === 'Unknown' ? 'Media type and bus are not reported, so the drive cannot be told apart.' : `${d.mediaType}${d.busType ? ` on ${d.busType}` : ''}`}>{d.type === 'Unknown' ? 'Unknown type' : d.type}</span></td>
                <td><HealthCell drive={d} /></td>
                <td className="num">{d.temperatureC != null
                  ? <>{d.temperatureC} °C{(d.temperature?.status ?? d.temperatureStatus) && (d.temperature?.status ?? d.temperatureStatus) !== 'OK' && <> <span className={`tag ${(d.temperature?.status ?? d.temperatureStatus) === 'Critical' ? 'bad' : 'warn'}`}>{d.temperature?.status ?? d.temperatureStatus}</span></>}</>
                  : <span className="muted" title="No temperature reported">N/A</span>}</td>
                <td className="num">{d.powerOnHours != null ? fmtPowerOn(d.powerOnHours) : <span className="muted" title="No power-on time reported">N/A</span>}</td>
                <td className="num">{formatBytes(d.sizeBytes)}</td>
                <td>
                  {drives.length === 1 && volumes.length > 0
                    ? volumes.map((v) => <div key={v.letter}><VolumeCell v={v} /></div>)
                    : <span className="muted" title="Which volume lives on which drive is not stored; see the volume list below.">see volumes</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {volumes.length > 0 && (
        <>
          <h3 className="subhead">Volumes</h3>
          <div className="tablewrap">
            <table className="dtable">
              <thead><tr><th>Drive</th><th>File system</th><th className="num">Free</th><th className="num">Total</th><th className="num">Used</th></tr></thead>
              <tbody>
                {volumes.map((v) => {
                  const used = volumeUsedPercent(v);
                  return (
                    <tr key={v.letter}>
                      <td><b>{v.letter}</b></td><td>{v.fileSystem ?? '—'}</td>
                      <td className="num">{formatBytes(v.freeBytes)}</td><td className="num">{formatBytes(v.totalBytes)}</td>
                      <td className="num">{used != null ? `${used.toFixed(1)}%` : '—'}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </>
      )}
    </section>
  );
}
