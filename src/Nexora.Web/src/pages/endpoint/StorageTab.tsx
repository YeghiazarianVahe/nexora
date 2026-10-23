import { formatBytes, volumeUsedPercent } from '../../lib/disks';
import { dataFreshness, FRESHNESS_TEXT } from '../../lib/freshness';
import { metricColorFor, useThresholds } from '../../lib/thresholds';
import { DriveHealth } from '../../components/DriveHealth';
import { roleRank, type EndpointSummary, type Me } from '../../types';
import { fmtDate } from '../../ui';

/** Physical disks (health, SMART) and logical volumes (capacity) are separate sections: one says nothing about the other. */
export function StorageTab({ e, me }: { e: EndpointSummary; me: Me }) {
  const th = useThresholds();
  const canAct = roleRank[me.role] >= roleRank.Operator;
  const volumes = e.volumes ?? [];
  const fresh = dataFreshness(e);
  return (
    <>
      {e.diskScanError && e.online && <div className="notice warn" role="status" data-testid="disk-scan-error">Drive data is out of date: the last drive scan failed ({e.diskScanError}). CPU and RAM are current.</div>}

      <DriveHealth endpointId={e.id} range="6h" canRefresh={canAct && e.online} />

      <section aria-labelledby="vol-h" className="icard wide" style={{ marginTop: 22 }}>
        <div className="icard-head">
          <h3 id="vol-h">Logical volumes</h3>
          <span className="muted">Capacity as of the last poll: {e.lastSeenUtc ? fmtDate(e.lastSeenUtc) : 'never'}{fresh !== 'live' && ` · ${FRESHNESS_TEXT[fresh]}`}</span>
        </div>
        <div className="tablewrap">
          <table aria-label="Logical volumes">
            <thead><tr><th>Volume</th><th>File system</th><th className="num">Size</th><th className="num">Used</th><th className="num">Free</th><th>Usage</th><th>Status</th></tr></thead>
            <tbody>
              {volumes.length === 0 && <tr><td colSpan={7} className="empty">{e.lastSeenUtc ? 'No fixed volumes were reported.' : 'Volumes have not been read yet.'}</td></tr>}
              {volumes.map((v) => {
                const used = volumeUsedPercent(v);
                const low = used != null && used >= th.diskAlertPercent;
                return (
                  <tr key={v.letter}>
                    <td><b>{v.letter}</b></td>
                    <td>{v.fileSystem ?? <span className="unavail">Unavailable</span>}</td>
                    <td className="num">{formatBytes(v.totalBytes)}</td>
                    <td className="num">{formatBytes(v.totalBytes - v.freeBytes)}</td>
                    <td className="num">{formatBytes(v.freeBytes)}</td>
                    <td>
                      {used != null
                        ? <span className="metric"><span className="metric-bar" aria-hidden="true"><i style={{ width: `${used}%`, background: metricColorFor(Math.round(used), 'disk', th) }} /></span><span className="metric-pct">{used.toFixed(0)}%</span></span>
                        : <span className="unavail">Unavailable</span>}
                    </td>
                    <td><span className={`tag ${used == null ? '' : low ? 'bad' : 'ok'}`}>{used == null ? 'Unavailable' : low ? 'Low free space' : 'Capacity normal'}</span><span className="muted small"> · Volume health unavailable</span></td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </section>
    </>
  );
}
