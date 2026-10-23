import { useState } from 'react';
import { roleRank, type EndpointSummary, type Me } from '../../types';
import { LiveError, OperatorRequired, canReadLiveDetails, useLiveDetail } from './liveDetails';
import { ErrorNotice, Loading, fmtDate, useSort } from '../../ui';
import { Bytes, Card, DateVal, Facts, mhz, Val } from './parts';
import { CollectionBanner, useHardware, useHardwareRefresh } from './OverviewTab';

/** `embedded`: shown under the Overview cards, which already show the loading state, the load error and the collection banner. */
export function HardwareTab({ e, me, embedded = false }: { e: EndpointSummary; me: Me; embedded?: boolean }) {
  const q = useHardware(e.id);
  const refresh = useHardwareRefresh(e.id);
  const hw = q.data;
  const mem = hw?.memory;
  const canRefresh = roleRank[me.role] >= roleRank.Operator;
  const rows = mem?.modules ?? [];
  const { sorted, th } = useSort(rows, {
    slot: (m) => m.slot, capacity: (m) => m.capacityBytes, maker: (m) => m.manufacturer, speed: (m) => m.configuredSpeedMhz ?? m.speedMhz,
  }, { key: 'slot', dir: 'asc' });

  return (
    <>
      <div className="toolbar">
        <span className="muted">
          {hw?.collectedUtc ? <>Inventory collected {fmtDate(hw.collectedUtc)}{hw.cached ? ' (cached)' : ''}.</> : 'Not collected yet.'}
          {' '}Read-only; collected on demand, not on every poll.
        </span>
        <div className="spacer" />
        {canRefresh && (
          <button onClick={() => refresh.mutate()} disabled={refresh.isPending || !e.online} title={e.online ? 'Reads the hardware inventory over WMI now' : 'The workstation is offline'}>
            {refresh.isPending ? 'Collecting...' : 'Refresh inventory'}
          </button>
        )}
      </div>
      {q.isLoading && !embedded && <Loading label="Reading hardware inventory" />}
      <ErrorNotice error={embedded ? refresh.error : q.error ?? refresh.error} />
      {hw && !embedded && <CollectionBanner hw={hw} online={e.online} />}

      <div className="cards">
        <Card title="Operating system" id="h-os">
          <Facts rows={[
            ['Product name', <Val key="p" v={hw?.os?.productName ?? e.osCaption} />],
            ['Edition', <Val key="e" v={hw?.os?.edition} />],
            ['Display version', <Val key="v" v={hw?.os?.version} />],
            ['OS version', <Val key="ov" v={hw?.os?.kernelVersion ?? e.osVersion} />],
            ['Build number', <Val key="b" v={hw?.os?.build} />],
            ['Architecture', <Val key="a" v={hw?.os?.architecture} />],
            ['Install date', <DateVal key="i" v={hw?.os?.installedUtc} />],
          ]} />
        </Card>
        <Card title="Computer" id="h-comp">
          <Facts rows={[
            ['Manufacturer', <Val key="m" v={hw?.computer?.manufacturer ?? e.manufacturer} />],
            ['Model', <Val key="md" v={hw?.computer?.model ?? e.model} />],
            ['Domain', <Val key="d" v={hw?.computer?.domain} />],
          ]} />
        </Card>
        <Card title="Processor" id="h-cpu">
          <Facts rows={[
            ['Manufacturer', <Val key="m" v={hw?.cpu?.manufacturer} />],
            ['Model', <Val key="md" v={hw?.cpu?.model} />],
            ['Cores / threads', <Val key="c" v={hw?.cpu?.physicalCores != null && hw.cpu.logicalProcessors != null ? `${hw.cpu.physicalCores} / ${hw.cpu.logicalProcessors}` : null} />],
            ['Architecture', <Val key="a" v={hw?.cpu?.architecture} />],
            ['Max clock', <Val key="mc" v={mhz(hw?.cpu?.maxClockMhz)} />],
          ]} />
        </Card>
        <Card title="Motherboard" id="h-board">
          <Facts rows={[
            ['Manufacturer', <Val key="m" v={hw?.board?.manufacturer} />],
            ['Model', <Val key="md" v={hw?.board?.model} />],
            ['Serial number', hw?.serialsHidden ? <span key="s" className="muted">Hidden for your role</span> : <Val key="s" v={hw?.board?.serial} />],
          ]} />
        </Card>
        <Card title="BIOS / UEFI" id="h-bios">
          <Facts rows={[
            ['Vendor', <Val key="m" v={hw?.bios?.manufacturer} />],
            ['Version', <Val key="v" v={hw?.bios?.version} />],
            ['Release date', <DateVal key="d" v={hw?.bios?.releaseDateUtc} />],
          ]} />
        </Card>
      </div>

      <section aria-labelledby="h-mem" className="icard wide">
        <div className="icard-head"><h3 id="h-mem">Memory</h3></div>
        <Facts rows={[
          ['Installed', <Bytes key="i" v={mem?.installedBytes} />],
          ['Technology', <Val key="t" v={mem?.technology} />],
          ['Speed', <Val key="s" v={mhz(mem?.speedMhz)} />],
          ['Slots', mem?.slotCount != null ? `${mem.slotCount} total` : <span key="sl" className="unavail">Unknown</span>],
          ['Occupied', mem?.modulesReported ? mem.occupiedSlots : <span key="o" className="unavail">Unknown</span>],
          ['Empty', mem?.emptySlots != null ? mem.emptySlots : <span key="em" className="unavail">Unknown</span>],
          ['Maximum supported', <Bytes key="mx" v={mem?.maxCapacityBytes} />],
        ]} />
        <div className="tablewrap">
          <table aria-label="Memory modules">
            <thead><tr>{th('slot', 'Slot')}<th>Bank</th>{th('capacity', 'Capacity', 'num')}{th('maker', 'Manufacturer')}<th>Type</th>{th('speed', 'Speed', 'num')}<th>Part number</th></tr></thead>
            <tbody>
              {!mem?.modulesReported && <tr><td colSpan={7} className="empty">{hw ? 'The workstation did not report its memory modules (SMBIOS data is not available, for example on some virtual machines).' : 'Not collected yet.'}</td></tr>}
              {mem?.modulesReported && sorted.map((m, i) => (
                <tr key={`${m.slot}-${i}`}>
                  <td><Val v={m.slot} /></td><td><Val v={m.bank} /></td><td className="num"><Bytes v={m.capacityBytes} /></td>
                  <td><Val v={m.manufacturer} /></td><td><Val v={m.technology} /></td>
                  <td className="num"><Val v={mhz(m.configuredSpeedMhz ?? m.speedMhz)} /></td><td className="mono"><Val v={m.partNumber} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <Software e={e} me={me} />
    </>
  );
}

/** Installed software is a heavier registry read, so it is loaded only when asked for. */
function Software({ e, me }: { e: EndpointSummary; me: Me }) {
  const allowed = canReadLiveDetails(me);
  const [asked, setAsked] = useState(false);
  const [filter, setFilter] = useState('');
  const q = useLiveDetail(e, me, asked);;
  const f = filter.trim().toLowerCase();
  const list = (q.data?.software ?? []).filter((s) => !f || s.name.toLowerCase().includes(f));
  const { sorted, th } = useSort(list, { name: (s) => s.name, version: (s) => s.version, publisher: (s) => s.publisher }, { key: 'name', dir: 'asc' });
  return (
    <section aria-labelledby="h-sw" className="icard wide">
      <div className="icard-head">
        <h3 id="h-sw">Installed software</h3>
        {!asked && allowed && <button className="small" disabled={!e.online} onClick={() => setAsked(true)}>{e.online ? 'Load list' : 'Offline'}</button>}
        {asked && <input aria-label="Filter software" placeholder="Filter" value={filter} onChange={(ev) => setFilter(ev.target.value)} />}
      </div>
      {q.isFetching && <Loading label="Reading the installed software" />}
      {!allowed && <OperatorRequired />}
      <LiveError error={q.error} />
      {q.data && (
        <div className="tablewrap tall">
          <table>
            <thead><tr>{th('name', 'Name')}{th('version', 'Version')}{th('publisher', 'Publisher')}</tr></thead>
            <tbody>{sorted.map((s) => <tr key={s.name + s.version}><td>{s.name}</td><td><Val v={s.version} /></td><td><Val v={s.publisher} /></td></tr>)}</tbody>
          </table>
        </div>
      )}
    </section>
  );
}
