import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post } from '../api';
import { HealthRing, StatLine, TimeChart } from '../charts';
import type { DiskHealth, Range } from '../types';
import { HealthLabel, InterfaceWarning, ReasonsList, TemperatureBlock } from './DriveHealthParts';
import { ErrorNotice, Loading, fmtDate, fmtPowerOn } from '../ui';

/** Independent drive health estimate, measured temperature, source confidence and SMART evidence. */
export function DriveHealth({ endpointId, range, canRefresh }: { endpointId: string; range: Range; canRefresh: boolean }) {
  const qc = useQueryClient();
  const q = useQuery({
    queryKey: ['disks', endpointId, range],
    queryFn: () => get<DiskHealth[]>(`/api/endpoints/${endpointId}/disks?range=${range}`),
    refetchInterval: 120_000,
  });
  const refresh = useMutation({
    mutationFn: () => post<DiskHealth[]>(`/api/endpoints/${endpointId}/disks/refresh?range=${range}`),
    onSuccess: (data) => { qc.setQueryData(['disks', endpointId, range], data); qc.invalidateQueries({ queryKey: ['endpoint', endpointId] }); },
  });

  return (
    <>
      <div className="toolbar" style={{ marginTop: 22, marginBottom: 10 }}>
        <h2 style={{ margin: 0 }}>Drive health</h2>
        <div className="spacer" />
        {canRefresh && <button onClick={() => refresh.mutate()} disabled={refresh.isPending}>{refresh.isPending ? 'Reading drives...' : 'Read now'}</button>}
      </div>
      <ErrorNotice error={refresh.error ?? q.error} />
      {q.isLoading && <Loading />}
      {q.data?.length === 0 && (
        <div className="notice">No drive data yet. It is read every few minutes while the workstation is online{canRefresh ? ', or use Read now' : ''}.</div>
      )}
      {q.data?.map((k) => <DiskCard key={k.diskKey} disk={k} />)}
    </>
  );
}

function DiskCard({ disk: k }: { disk: DiskHealth }) {
  const verdict = k.status === 'Critical' ? 'bad' : k.status === 'Warning' ? 'warn' : '';
  const temps = k.temperatureHistory;
  const attention = (id: number, raw: number) => [5, 187, 196, 197, 198, 199].includes(id) && raw > 0;
  const lifeLeft = k.wearPercent != null && k.wearPercent >= 0 && k.wearPercent <= 100 ? 100 - k.wearPercent : null;

  return (
    <section className="disk" aria-label={`Drive ${k.model}`}>
      <div><HealthRing value={k.healthPercent} status={k.status} /><HealthLabel /></div>
      <div style={{ minWidth: 0 }}>
        <h3>{k.model} <span className="tag">{k.mediaType}{k.busType && k.busType !== k.mediaType ? ` · ${k.busType}` : ''}</span></h3>
        <div className="muted mono">{k.serial ?? 'serial unknown'} &middot; {(k.sizeBytes / 1e9).toFixed(0)} GB &middot; read {fmtDate(k.updatedUtc)}</div>
        <div className="disk-evidence" role="status">
          <span className="tag">{k.healthPercent == null ? 'Unavailable' : 'Estimated health'}</span>
          <span className="tag">{k.confidence ?? 'Low'} confidence</span>
          {k.stale && <span className="tag warn">Stale · last known health</span>}
          {k.currentHealthModelVersion != null && k.healthModelVersion != null && k.healthModelVersion < k.currentHealthModelVersion
            ? <span className="tag warn">Earlier health model · refresh scan</span>
            : (k.currentHealthModelVersion == null || k.healthModelVersion == null || k.healthModelVersion !== k.currentHealthModelVersion)
              && <span className="tag warn">Health model not verified · refresh scan</span>}
          <span className="muted small">Firmware: {k.firmware ?? 'Unavailable'}</span>
        </div>
        {k.sourceNote && <p className="notice">{k.sourceNote}</p>}
        {k.scanCompletedUtc && k.scanCompletedUtc !== k.updatedUtc && <p className="muted small">Scan completed {fmtDate(k.scanCompletedUtc)}. Evidence sampled {fmtDate(k.updatedUtc)}.</p>}
        {(k.stale || k.temperatureC == null) && k.lastKnownTemperatureC != null && <p className="muted small">Last known temperature: {k.lastKnownTemperatureC} °C. A new scan is needed for a current reading.</p>}

        <div className="disk-facts">
          {k.temperature
            ? <TemperatureBlock disk={k} />
            : <div className="fact"><div className="l">Temperature</div><div className="v">{k.temperatureC != null ? `${k.temperatureC} °C` : '-'}{k.maxTemperatureC ? <small> max {k.maxTemperatureC} °C</small> : null}</div></div>}
          <div className="fact"><div className="l">Power-on time</div><div className="v">{fmtPowerOn(k.powerOnHours)}{k.powerOnHours != null && <small> {k.powerOnHours.toLocaleString()} h</small>}</div></div>
          {lifeLeft != null && <div className="fact"><div className="l">Rated endurance remaining</div><div className="v">{lifeLeft}%</div></div>}
          {k.reallocatedSectors != null && <div className="fact"><div className="l">Reallocated sectors</div><div className="v">{k.reallocatedSectors}</div></div>}
          {k.pendingSectors != null && <div className="fact"><div className="l">Pending sectors</div><div className="v">{k.pendingSectors}</div></div>}
          {(k.readErrors != null || k.writeErrors != null) && <div className="fact"><div className="l">Read / write errors</div><div className="v">{k.readErrors ?? 'Unavailable'} / {k.writeErrors ?? 'Unavailable'}</div></div>}
        </div>

        <div className={`verdict ${verdict}`} role="status"><b>Status:</b> {k.summary}</div>
        <InterfaceWarning text={k.interfaceWarning} />
        <ReasonsList reasons={k.reasons} score={k.healthPercent} />

        {temps.some((t) => t.temperatureC != null) && (
          <div style={{ marginTop: 12 }}>
            <TimeChart
              title="Temperature" unit="°C" yMin="auto" height={170}
              times={temps.map((t) => Date.parse(t.timeUtc))}
              series={[{ key: 't', label: 'Temperature', color: 'var(--series-3)', values: temps.map((t) => t.temperatureC) }]}
              headline={<StatLine values={temps.map((t) => t.temperatureC)} unit="°C" />}
              emptyText="Temperature is recorded with every drive reading."
            />
          </div>
        )}

        {k.attributes.length > 0 && (
          <details className="smart">
            <summary>SMART attributes ({k.attributes.length})</summary>
            <div className="tablewrap" style={{ marginTop: 8 }}>
              <table>
                <thead><tr><th className="num">ID</th><th>Attribute</th><th className="num">Current</th><th className="num">Worst</th><th className="num">Threshold</th><th className="num">Raw</th></tr></thead>
                <tbody>
                  {k.attributes.map((a) => (
                    <tr key={a.id} className={attention(a.id, a.raw) ? 'attn' : ''}>
                      <td className="num mono">{a.id}</td><td>{a.name}</td><td className="num">{a.current}</td><td className="num">{a.worst}</td><td className="num">{a.threshold || '-'}</td><td className="num mono">{a.raw.toLocaleString()}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </details>
        )}
      </div>
    </section>
  );
}

