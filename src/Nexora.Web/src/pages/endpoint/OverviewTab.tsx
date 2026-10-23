import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post } from '../../api';
import { dataFreshness, FRESHNESS_TEXT } from '../../lib/freshness';
import { MetricCell, OuCell } from '../../components/EndpointParts';
import { parseOverview } from '../../lib/overview';
import type { EndpointSummary, HardwareInventory } from '../../types';
import { ErrorNotice, Loading, fmtBytes, fmtDate } from '../../ui';
import { Bytes, Card, DateVal, Facts, formatUptime, mhz, Val } from './parts';
import { OverviewSignals } from './OverviewSignals';
import { useLivePerformance } from './livePerformance';

export function useHardware(id: string) {
  return useQuery({ queryKey: ['hardware', id], queryFn: () => get<HardwareInventory>(`/api/endpoints/${id}/hardware`), staleTime: 60_000 });
}

/** Manual refresh of the inventory only: a read-only WMI query, never a remote action. */
export function useHardwareRefresh(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => post<HardwareInventory>(`/api/endpoints/${id}/hardware/refresh`),
    onSuccess: (data) => qc.setQueryData(['hardware', id], data),
  });
}

export function CollectionBanner({ hw, online }: { hw: HardwareInventory; online: boolean }) {
  if (hw.status === 'Complete' && hw.errors.length === 0) return null;
  const unavailable = hw.status === 'Unavailable';
  return (
    <div className={`notice ${unavailable ? 'warn' : ''}`} role="status" data-testid="inventory-status">
      <b>{unavailable ? 'Inventory unavailable.' : 'Inventory partly collected.'}</b>{' '}
      {unavailable && !online ? 'The workstation is offline and has not been inventoried yet. ' : ''}
      {hw.errors.length > 0 && <ul className="plain">{hw.errors.map((x) => <li key={x}>{x}</li>)}</ul>}
    </div>
  );
}

export function useOverview(id: string) {
  return useQuery({ queryKey: ['overview', id], queryFn: async () => parseOverview(await get<unknown>(`/api/endpoints/${id}/overview`)), staleTime: 30_000 });
}

export function OverviewTab({ e }: { e: EndpointSummary }) {
  const performance = useLivePerformance();
  const q = useHardware(e.id);
  const ovq = useOverview(e.id);
  const ov = ovq.data;
  const hwq = q.data;
  // The overview payload is authoritative; the plain inventory only fills in when the overview request failed.
  const hw: HardwareInventory | null | undefined = ov ? ov.inventory : hwq;
  const fresh = dataFreshness(e);
  const offline = ov ? !ov.online : false;
  const stale = ov ? offline || ov.stale : false;
  const live = performance.sample !== null || (ov ? ov.online && !ov.stale : fresh === 'live');
  const lv = performance.sample ?? ov?.live ?? null;
  const mem = hw?.memory;
  const os = hw?.os;
  const cpuLoad = ov ? lv?.cpuLoadPercent ?? null : e.cpuPercent;
  const ramPct = ov ? lv?.ramPercentUsed ?? null : e.ramPercent;
  const total = lv?.ramTotalBytes ?? e.ramTotalBytes ?? mem?.usableBytes ?? null;
  const used = lv?.ramUsedBytes ?? (total != null && ramPct != null ? Math.round(total * ramPct / 100) : null);
  const free = lv?.ramFreeBytes ?? (total != null && used != null ? total - used : null);
  const uptime = live ? formatUptime(os?.bootTimeUtc ?? null) ?? (!ov && e.uptimeSeconds != null ? formatUptime(new Date(Date.now() - e.uptimeSeconds * 1000).toISOString()) : null) : null;
  const fullBoot = os?.uptimeBasis === 'fullBoot';
  const build = [os?.version, os?.build].filter(Boolean).join(' / ') || null;
  const lastKnown = (v: number | null) => (v != null ? `${Math.round(v)}% (last known)` : null);

  return (
    <>
      {(q.isLoading || ovq.isLoading) && <Loading label="Reading hardware inventory" />}
      <ErrorNotice error={ovq.error ?? q.error} />
      {ov && stale && (
        <div className="notice warn" role="status" data-testid="overview-stale">
          <b>{offline ? 'Offline' : 'Data is stale'}</b>
          {ov.inventory ? <> - showing last known values from {fmtDate(ov.collectedUtc)}.</> : ' - no data collected yet.'}
          {' '}{ov.lastSeenUtc ? <>Last seen {fmtDate(ov.lastSeenUtc)}.</> : 'Never seen online.'}
        </div>
      )}
      {ov && !ov.inventory && (
        <div className="notice" role="status" data-testid="overview-empty">
          <b>No data collected yet</b>
          {ov.unavailableReason && <>{' '}<span data-testid="overview-reason">{ov.unavailableReason}</span></>}
        </div>
      )}
      {ov && ov.inventory && !stale && ov.unavailableReason && <div className="notice" role="status"><span data-testid="overview-reason">{ov.unavailableReason}</span></div>}
      {hw && <CollectionBanner hw={hw} online={e.online} />}
      <dl className="overview-context" aria-label="Session and inventory context">
        <div><dt>Domain</dt><dd><Val v={hw?.computer?.domain} /></dd></div>
        <div><dt>Last full boot</dt><dd><DateVal v={os?.bootTimeUtc} /></dd></div>
        <div><dt>Uptime</dt><dd><Val v={uptime} /></dd></div>
        <div><dt>Device</dt><dd><Val v={[hw?.computer?.manufacturer ?? e.manufacturer, hw?.computer?.model ?? e.model].filter(Boolean).join(' ') || null} /></dd></div>
      </dl>
      <OverviewSignals e={e} />
      <div className={`cards${stale ? ' stale-values' : ''}`}>
        <Card title="Operating system" id="c-os">
          <Facts rows={[
            ['Windows', <Val key="p" v={os?.productName ?? e.osCaption} />],
            ['Edition', <Val key="e" v={os?.edition} />],
            ['Version / build', <Val key="v" v={build ?? e.osVersion} />],
            ['Architecture', <Val key="a" v={os?.architecture} />],
            ['Installed', <DateVal key="i" v={os?.installedUtc} />],
            ['Last boot', <span key="bt"><DateVal v={os?.bootTimeUtc} />{fullBoot && <FastStartupNote />}</span>],
            ['Uptime', <span key="u"><Val v={uptime} />{fullBoot && uptime && <FastStartupNote />}</span>],
          ]} />
        </Card>

        <Card title="Processor" id="c-cpu">
          <Facts rows={[
            ['Model', <Val key="md" v={hw?.cpu?.model} />],
            ['Manufacturer', <Val key="m" v={hw?.cpu?.manufacturer} />],
            ['Physical cores', <Val key="pc" v={hw?.cpu?.physicalCores} />],
            ['Logical processors', <Val key="lp" v={hw?.cpu?.logicalProcessors} />],
            ['Architecture', <Val key="ar" v={hw?.cpu?.architecture} />],
            ['Base clock', <Val key="mc" v={mhz(hw?.cpu?.maxClockMhz)} />],
            ['Current load', live ? <MetricCell key="ut" value={cpuLoad} online kind="cpu" /> : <span key="ut" className="unavail" title={FRESHNESS_TEXT[fresh]}>{lastKnown(cpuLoad) ?? 'Unavailable'}</span>],
          ]} />
        </Card>

        <Card title="Memory" id="c-mem">
          <Facts rows={[
            ['Total (usable)', <Bytes key="to" v={total} />],
            ['Installed', <Bytes key="in" v={mem?.installedBytes} />],
            ['Used', live ? <Bytes key="us" v={used} /> : <span key="us" className="unavail">Unavailable</span>],
            ['Free', live ? <Bytes key="av" v={free} /> : <span key="av" className="unavail" title={FRESHNESS_TEXT[fresh]}>Unavailable</span>],
            ['Used %', live ? <MetricCell key="ut" value={ramPct} online kind="ram" /> : <span key="ut" className="unavail" title={FRESHNESS_TEXT[fresh]}>{lastKnown(ramPct) ?? 'Unavailable'}</span>],
            ['Modules', mem?.modulesReported ? <span key="mo">{mem.modules.length || mem.occupiedSlots}</span> : <span key="mo" className="unavail">Not reported</span>],
            ['Slots', mem?.slotCount != null ? <span key="sl">{mem.slotCount} total{mem.modulesReported ? ` · ${mem.occupiedSlots} occupied` : ''}{mem.emptySlots != null ? ` · ${mem.emptySlots} empty` : ''}</span> : <span key="sl" className="unavail">Unknown</span>],
            ['Technology', <Val key="te" v={mem?.technology} />],
            ['Speed', <Val key="sp" v={mhz(mem?.speedMhz)} />],
          ]} />
        </Card>

        <Card title="Motherboard and firmware" id="c-board">
          <Facts rows={[
            ['Manufacturer', <Val key="m" v={hw?.board?.manufacturer} />],
            ['Model', <Val key="md" v={hw?.board?.model} />],
            ['Serial number', hw?.serialsHidden ? <span key="s" className="muted" title="Serial numbers are shown to Operators and above">Hidden for your role</span> : <Val key="s" v={hw?.board?.serial} />],
            ['BIOS / UEFI version', <Val key="bv" v={hw?.bios?.version} />],
            ['BIOS release date', <DateVal key="bd" v={hw?.bios?.releaseDateUtc} />],
          ]} />
        </Card>

        <Card title="System" id="c-sys">
          <Facts rows={[
            ['Hostname', <Val key="h" v={e.reportedHostname ?? hw?.computer?.hostname ?? e.hostname} />],
            ['Computer', <Val key="c" v={[hw?.computer?.manufacturer ?? e.manufacturer, hw?.computer?.model ?? e.model].filter(Boolean).join(' ') || null} />],
            ['Domain', <Val key="d" v={hw?.computer?.domain} />],
            ['Logged-in user', <Val key="u" v={e.lastUser} />],
            ['Local IP', <Val key="ip" v={e.ipAddress} />],
            ['Organizational unit', <OuCell key="ou" endpoint={e} />],
            ['Last inventory', hw?.collectedUtc ? <span key="li">{fmtDate(hw.collectedUtc)}{hw.cached ? ' (cached)' : ''}</span> : <span key="li" className="unavail">Not collected</span>],
            ['Collection status', <span key="cs" className={`tag ${hw?.status === 'Complete' ? 'ok' : hw?.status === 'Partial' ? 'warn' : ''}`}>{hw?.status ?? 'Unavailable'}</span>],
          ]} />
        </Card>
      </div>
      <p className="muted small">{FRESHNESS_TEXT[fresh]}. Installed memory: {mem?.installedBytes != null ? fmtBytes(mem.installedBytes) : 'Unavailable'}.</p>
    </>
  );
}

function FastStartupNote() {
  return <span className="muted small" title="Windows Fast Startup keeps the kernel session across shutdowns, so this is the last full boot, not the last power-on."> (Fast Startup is on - last full boot)</span>;
}
