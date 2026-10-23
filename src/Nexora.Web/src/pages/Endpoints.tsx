import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { endpointPath, saveListQuery } from '../nav/origin';
import { apiDownload, get, post } from '../api';
import { DriveHealthCell, EndpointCell, FreeSpaceCell, MetricCell, OuCell, RelativeTime, StatusBadge } from '../components/EndpointParts';
import { formatBytes, ramUsedBytes, systemVolume } from '../lib/disks';
import { ActionLabel } from '../jobs/ActionLabel';
import { RiskBadge } from '../jobs/JobParts';
import { RunActionDialog } from '../jobs/RunActionDialog';
import { useCatalog } from '../jobs/RemoteActions';
import type { CatalogEntry } from '../jobs/jobTypes';
import { exportLabel, filterableOu, formatModel, uptimeText } from '../lib/format';
import {
  emptySelection, headerState, hiddenCount, prune, toggle, toggleHeader, type Selection,
} from '../lib/selection';
import { roleRank, type EndpointSummary, type Group, type Me } from '../types';
import { ConfirmDialog, Dialog, ErrorNotice, Loading, useSort } from '../ui';

export function useGroups() {
  return useQuery({ queryKey: ['groups'], queryFn: () => get<Group[]>('/api/groups') });
}

/** The list API returns at most this many rows; beyond it the server-side export/count is the source of truth. */
const LIST_LIMIT = 5000;
const ROW_HEIGHT = 46;
const DEFAULT_SORT = { key: 'name', dir: 'asc' } as const;
const STORAGE_KEY = 'nexora.endpoints.hiddenColumns';

type ColKey = 'select' | 'status' | 'name' | 'ou' | 'model' | 'user' | 'uptime' | 'cpu' | 'ram' | 'ssd' | 'hdd' | 'free' | 'seen';
interface Col { key: ColKey; label: string; width: string; minPx: number; sortable: boolean; locked?: boolean; sticky?: boolean }

const COLS: Col[] = [
  { key: 'select', label: '', width: '40px', minPx: 40, sortable: false, locked: true, sticky: true },
  { key: 'status', label: 'Status', width: '90px', minPx: 90, sortable: true, sticky: true },
  { key: 'name', label: 'Endpoint', width: '180px', minPx: 180, sortable: true, locked: true, sticky: true },
  { key: 'ou', label: 'OU', width: 'minmax(140px, 1fr)', minPx: 140, sortable: true },
  { key: 'model', label: 'Model', width: '190px', minPx: 190, sortable: true },
  { key: 'user', label: 'User', width: '150px', minPx: 150, sortable: true },
  { key: 'uptime', label: 'Uptime', width: '90px', minPx: 90, sortable: true },
  { key: 'cpu', label: 'CPU', width: '120px', minPx: 120, sortable: true },
  { key: 'ram', label: 'RAM', width: '120px', minPx: 120, sortable: true },
  { key: 'ssd', label: 'SSD', width: '130px', minPx: 130, sortable: true },
  { key: 'hdd', label: 'HDD', width: '130px', minPx: 130, sortable: true },
  { key: 'free', label: 'Disk free', width: '160px', minPx: 160, sortable: true },
  { key: 'seen', label: 'Last seen', width: '110px', minPx: 110, sortable: true },
];

function loadHidden(): ColKey[] {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    const parsed = raw ? (JSON.parse(raw) as unknown) : [];
    return Array.isArray(parsed) ? (parsed.filter((k) => COLS.some((c) => c.key === k && !c.locked)) as ColKey[]) : [];
  } catch {
    return [];
  }
}
function saveHidden(keys: ColKey[]) {
  try { window.localStorage.setItem(STORAGE_KEY, JSON.stringify(keys)); } catch { /* storage unavailable: keep the choice for this visit only */ }
}

const uniq = (values: (string | null | undefined)[]) =>
  [...new Set(values.filter((v): v is string => !!v))].sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));

export function Endpoints({ me }: { me: Me }) {
  const nav = useNavigate();
  const qc = useQueryClient();
  const isAdmin = roleRank[me.role] >= roleRank.Admin;

  // The dashboard cards link here with the filter that produces their number: /endpoints?status=offline, ?alert=cpu, ?health=attention...
  const [urlFilters, setUrlFilters] = useSearchParams();
  const fromUrl = <T extends string>(name: string, allowed: readonly T[], fallback: T): T => {
    const v = urlFilters.get(name);
    return v && (allowed as readonly string[]).includes(v) ? (v as T) : fallback;
  };
  const text = (name: string) => (urlFilters.get(name) ?? '').slice(0, 200);
  const [search, setSearch] = useState(() => text('q'));
  const [status, setStatus] = useState<'all' | 'online' | 'offline'>(() => fromUrl('status', ['all', 'online', 'offline'] as const, 'all'));
  const [alert, setAlert] = useState<'' | 'cpu' | 'ram' | 'disk' | 'diskhealth'>(() => fromUrl('alert', ['', 'cpu', 'ram', 'disk', 'diskhealth'] as const, ''));
  const [groupId, setGroupId] = useState(() => text('group'));
  const [maker, setMaker] = useState(() => text('maker'));
  const [model, setModel] = useState(() => text('model'));
  const [ou, setOu] = useState(() => text('ou'));
  const [health, setHealth] = useState<'' | 'attention' | 'ok'>(() => fromUrl('health', ['', 'attention', 'ok'] as const, ''));

  const [adding, setAdding] = useState(false);
  const [selected, setSelected] = useState<Selection>(emptySelection);
  const [allMatching, setAllMatching] = useState(false); // "select all N matching endpoints": export by filter, not by id
  const [banner, setBanner] = useState(false);
  const [removing, setRemoving] = useState<'selected' | 'all' | null>(null);
  const [groupEdit, setGroupEdit] = useState<'add' | 'remove' | null>(null);
  const [hidden, setHidden] = useState<ColKey[]>(loadHidden);
  const [colMenu, setColMenu] = useState(false);
  const [bulkPicker, setBulkPicker] = useState(false);
  const [bulkEntry, setBulkEntry] = useState<CatalogEntry | null>(null);
  const catalog = useCatalog();
  const bulkActions = (catalog.data ?? []).filter((a) => a.allowed && a.implemented && a.supportsBulk);

  const groups = useGroups();
  const q = useQuery({ queryKey: ['endpoints'], queryFn: () => get<EndpointSummary[]>('/api/endpoints') });
  const all = useMemo(() => q.data ?? [], [q.data]);

  const importAd = useMutation({
    mutationFn: () => post<EndpointSummary[]>('/api/endpoints/import-ad', { prefix: null }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['endpoints'] }),
  });
  // Delete-all is two steps: the server says how many it would remove (never counting the pinned Nexora server) and issues a token for exactly
  // that number; the removal then only goes through if the inventory still has that number.
  const deletePreview = useMutation({
    mutationFn: () => post<{ count: number; confirmToken: string }>('/api/endpoints/remove/preview', {}),
  });
  const removeMany_ = useMutation({
    mutationFn: async (everything: boolean) => {
      if (everything) {
        const p = deletePreview.data;
        if (!p) throw new Error('The number of workstations to delete has not been confirmed by the server yet.');
        return post<{ removed: number }>('/api/endpoints/remove', { all: true, expectedCount: p.count, confirmToken: p.confirmToken });
      }
      // The server removes at most 500 per request.
      const ids = [...selected];
      let removed = 0;
      for (let i = 0; i < ids.length; i += 500) {
        removed += (await post<{ removed: number }>('/api/endpoints/remove', { ids: ids.slice(i, i + 500), all: false })).removed;
      }
      return { removed };
    },
    onSuccess: () => { setSelected(emptySelection); setAllMatching(false); setRemoving(null); deletePreview.reset(); qc.invalidateQueries(); },
    onError: () => { if (removing === 'all') deletePreview.mutate(); }, // the inventory changed or the token expired: show the new number to confirm
  });
  const openDeleteAll = () => { deletePreview.reset(); deletePreview.mutate(); setRemoving('all'); };

  // A workstation removed elsewhere (or by the last delete) must not stay selected.
  useEffect(() => {
    if (!q.data || q.data.length === 0) return;
    const existing = new Set(q.data.map((e) => e.id));
    setSelected((s) => prune(s, existing));
  }, [q.data]);

  const makers = useMemo(() => uniq(all.map((e) => e.manufacturer)), [all]);
  const models = useMemo(() => uniq(all.filter((e) => !maker || e.manufacturer === maker).map((e) => e.model)), [all, maker]);
  const ous = useMemo(() => uniq(all.map(filterableOu)), [all]);

  const filtered = useMemo(() => {
    const t = search.trim().toLowerCase();
    return all.filter((e) => {
      if (status === 'online' && !e.online) return false;
      if (status === 'offline' && e.online) return false;
      if (groupId && !e.groupIds.includes(groupId)) return false;
      if (maker && e.manufacturer !== maker) return false;
      if (model && e.model !== model) return false;
      if (ou && filterableOu(e) !== ou) return false;
      // "Needs attention" is the dashboard's drive-health alert (the server's own definition: online PC, weakest drive Warning or Critical).
      if (health === 'attention' && !e.alerts.includes('diskhealth')) return false;
      if (health === 'ok' && e.diskHealth !== 'OK') return false;
      if (alert && !e.alerts.includes(alert)) return false;
      return !t || [e.name, e.hostname, e.lastUser, e.ipAddress, e.manufacturer, e.model, filterableOu(e)].some((v) => v?.toLowerCase().includes(t));
    });
  }, [all, search, status, groupId, maker, model, ou, health, alert]);

  const initialSort: { key: string; dir: 'asc' | 'desc' } = (() => {
    const key = urlFilters.get('sort');
    const dir = urlFilters.get('dir');
    return COLS.some((c) => c.key === key && c.sortable) && (dir === 'asc' || dir === 'desc') ? { key: key!, dir } : DEFAULT_SORT;
  })();
  const { sorted: sortedRows, sort, toggle: toggleSort } = useSort(filtered, {
    status: (e) => (e.online ? 1 : 0),
    name: (e) => e.name,
    ou: filterableOu,
    model: (e) => formatModel(e.manufacturer, e.model),
    user: (e) => e.lastUser,
    uptime: (e) => e.uptimeSeconds,
    cpu: (e) => e.cpuPercent,
    ram: (e) => e.ramPercent,
    ssd: (e) => e.ssdHealthPercent,
    hdd: (e) => e.hddHealthPercent,
    free: (e) => systemVolume(e)?.freeBytes ?? null,
    seen: (e) => (e.lastSeenUtc ? Date.parse(e.lastSeenUtc) : null),
  }, initialSort);

  // The pinned Nexora server always stays at the top, whatever the sort order.
  const rows = useMemo(() => [...sortedRows.filter((r) => r.isPinned), ...sortedRows.filter((r) => !r.isPinned)], [sortedRows]);

  // Filters and sort (never the selection) live in the query string, so a refresh, a pasted link and Back from a workstation show the same list.
  useEffect(() => {
    const p = new URLSearchParams();
    if (search.trim()) p.set('q', search.trim());
    if (status !== 'all') p.set('status', status);
    if (alert) p.set('alert', alert);
    if (health) p.set('health', health);
    if (groupId) p.set('group', groupId);
    if (maker) p.set('maker', maker);
    if (model) p.set('model', model);
    if (ou) p.set('ou', ou);
    if (sort && (sort.key !== DEFAULT_SORT.key || sort.dir !== DEFAULT_SORT.dir)) { p.set('sort', sort.key); p.set('dir', sort.dir); }
    if (p.toString() !== urlFilters.toString()) setUrlFilters(p, { replace: true });
    saveListQuery(p.toString());
  }, [search, status, alert, health, groupId, maker, model, ou, sort, urlFilters, setUrlFilters]);

  const filtersActive = !!(search.trim() || status !== 'all' || groupId || maker || model || ou || health || alert);
  const clearFilters = () => { setSearch(''); setStatus('all'); setGroupId(''); setMaker(''); setModel(''); setOu(''); setHealth(''); setAlert(''); };

  // What the server should filter by when exporting: same fields as the filter bar.
  const filterDto = useMemo(() => ({
    search: search.trim() || undefined,
    online: status === 'all' ? undefined : status === 'online',
    groupId: groupId || undefined,
    manufacturer: maker || undefined,
    model: model || undefined,
    ou: ou || undefined,
    health: health || undefined,
  }), [search, status, groupId, maker, model, ou, health]);
  const sortDto = sort ? { key: sort.key, dir: sort.dir } : undefined;

  // The list is capped, so beyond the cap ask the server how many endpoints really match.
  const truncated = all.length >= LIST_LIMIT;
  const matchPreview = useQuery({
    queryKey: ['export-preview', filterDto],
    queryFn: () => post<{ count: number }>('/api/endpoints/export?preview=true', { filter: filterDto }),
    enabled: truncated, staleTime: 30_000,
  });
  const totalPreview = useQuery({
    queryKey: ['export-preview', 'all'],
    queryFn: () => post<{ count: number }>('/api/endpoints/export?preview=true', { filter: {} }),
    enabled: truncated, staleTime: 30_000,
  });
  const filteredCount = truncated ? (matchPreview.data?.count ?? rows.length) : rows.length;
  const totalCount = truncated ? (totalPreview.data?.count ?? all.length) : all.length;

  const visibleIds = useMemo(() => rows.map((r) => r.id), [rows]);
  const header = headerState(selected, visibleIds);
  const hiddenByFilter = hiddenCount(selected, visibleIds);
  const selectedCount = allMatching ? filteredCount : selected.size;
  const label = exportLabel({ selectedCount, filteredCount, totalCount, filtersActive });

  const doExport = useMutation({
    mutationFn: async () => {
      if (!allMatching && selected.size > 5000) throw new Error('At most 5000 selected endpoints can be exported at once. Use a filter instead.');
      const body = allMatching ? { filter: filterDto, sort: sortDto } : selected.size > 0 ? { ids: [...selected], sort: sortDto } : { filter: filterDto, sort: sortDto };
      const { blob, filename } = await apiDownload('/api/endpoints/export', body);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = filename;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
    },
  });

  // ---- columns
  const visibleCols = COLS.filter((c) => !hidden.includes(c.key));
  const template = visibleCols.map((c) => c.width).join(' ');
  const minWidth = visibleCols.reduce((sum, c) => sum + c.minPx, 0);
  const stickyLeft = useMemo(() => {
    const lefts: Partial<Record<ColKey, number>> = {};
    let x = 0;
    for (const c of visibleCols) if (c.sticky) { lefts[c.key] = x; x += c.minPx; }
    return lefts;
  }, [visibleCols]);
  const lastSticky = [...visibleCols].reverse().find((c) => c.sticky)?.key;
  const setColumn = (key: ColKey, on: boolean) => {
    const next = on ? hidden.filter((k) => k !== key) : [...hidden, key];
    setHidden(next);
    saveHidden(next);
  };

  // ---- virtualization
  const scrollRef = useRef<HTMLDivElement>(null);
  const virt = useVirtualizer({ count: rows.length, getScrollElement: () => scrollRef.current, estimateSize: () => ROW_HEIGHT, overscan: 12 });

  const headerRef = useRef<HTMLInputElement>(null);
  useEffect(() => { if (headerRef.current) headerRef.current.indeterminate = header === 'some'; }, [header]);

  const onHeader = () => {
    const selecting = header !== 'all';
    setSelected(toggleHeader(selected, visibleIds));
    setAllMatching(false);
    setBanner(selecting);
  };
  const onRow = (id: string, on: boolean) => { setSelected(toggle(selected, id, on)); setAllMatching(false); setBanner(false); };
  const clearSelection = () => { setSelected(emptySelection); setAllMatching(false); setBanner(false); };

  const cell = (c: Col, e: EndpointSummary) => {
    switch (c.key) {
      case 'select': return <input type="checkbox" aria-label={`Select ${e.name}`} checked={selected.has(e.id)} onChange={(ev) => onRow(e.id, ev.target.checked)} />;
      case 'status': return <StatusBadge online={e.online} />;
      case 'name': return <EndpointCell name={e.name} address={e.hostname} ip={e.ipAddress} pinned={e.isPinned} />;
      case 'ou': return <OuCell endpoint={e} onFilter={setOu} />;
      case 'model': {
        const text = formatModel(e.manufacturer, e.model);
        return text ? <span title={text}>{text}</span> : <span className="muted">—</span>;
      }
      case 'user': return e.lastUser ? <span title={e.lastUser}>{e.lastUser}</span> : <span className="muted-i">No user</span>;
      case 'uptime': return <span>{e.online ? uptimeText(e.uptimeSeconds) : <span className="muted">—</span>}</span>;
      case 'cpu': return <MetricCell value={e.cpuPercent} online={e.online} kind="cpu" />;
      case 'ram': {
        const used = ramUsedBytes(e);
        const title = used != null && e.ramTotalBytes != null ? `${formatBytes(used)} used of ${formatBytes(e.ramTotalBytes)} (${e.ramPercent?.toFixed(1)}%)` : undefined;
        return <MetricCell value={e.ramPercent} online={e.online} kind="ram" title={title} />;
      }
      case 'ssd': return <DriveHealthCell value={e.ssdHealthPercent} kind="SSD" />;
      case 'hdd': return <DriveHealthCell value={e.hddHealthPercent} kind="HDD" />;
      case 'free': return <FreeSpaceCell volume={systemVolume(e)} volumes={e.volumes} />;
      case 'seen': return <RelativeTime iso={e.lastSeenUtc} online={e.online} />;
    }
  };

  const exportDisabled = doExport.isPending || (selectedCount === 0 && filteredCount === 0);

  return (
    <>
      <h1>Workstations</h1>
      <p className="sub">{q.data ? `${rows.length} of ${all.length}${truncated ? '+' : ''} workstations` : 'Inventory'}</p>

      <div className="filters" role="search" aria-label="Filters">
        <div className="f"><label htmlFor="f-q">Search</label>
          <input id="f-q" placeholder="Name, address, user, model..." value={search} onChange={(e) => setSearch(e.target.value)} /></div>
        <div className="f"><label htmlFor="f-maker">Manufacturer</label>
          <select id="f-maker" value={maker} onChange={(e) => { setMaker(e.target.value); setModel(''); }}>
            <option value="">All manufacturers</option>{makers.map((m) => <option key={m}>{m}</option>)}
          </select></div>
        <div className="f"><label htmlFor="f-model">Model</label>
          <select id="f-model" value={model} onChange={(e) => setModel(e.target.value)}>
            <option value="">All models</option>{models.map((m) => <option key={m}>{m}</option>)}
          </select></div>
        <div className="f"><label htmlFor="f-ou">OU</label>
          <select id="f-ou" value={ou} onChange={(e) => setOu(e.target.value)}>
            <option value="">All OUs</option>{ous.map((o) => <option key={o}>{o}</option>)}
          </select></div>
        <div className="f"><label htmlFor="f-group">Group</label>
          <select id="f-group" value={groupId} onChange={(e) => setGroupId(e.target.value)}>
            <option value="">All groups</option>
            {groups.data?.map((g) => <option key={g.id} value={g.id}>{g.name} ({g.memberIds.length})</option>)}
          </select></div>
        <div className="f"><label htmlFor="f-status">Status</label>
          <select id="f-status" value={status} onChange={(e) => setStatus(e.target.value as typeof status)}>
            <option value="all">All</option><option value="online">Online</option><option value="offline">Offline</option>
          </select></div>
        <div className="f"><label htmlFor="f-health">Drive health</label>
          <select id="f-health" value={health} onChange={(e) => setHealth(e.target.value as typeof health)}>
            <option value="">Any</option><option value="attention">Needs attention</option><option value="ok">OK</option>
          </select></div>
        <div className="f"><label htmlFor="f-alert">Alert</label>
          <select id="f-alert" value={alert} onChange={(e) => setAlert(e.target.value as typeof alert)}>
            <option value="">Any</option><option value="cpu">CPU</option><option value="ram">RAM</option><option value="disk">Low disk space</option><option value="diskhealth">Drive health</option>
          </select></div>
        {filtersActive && <button onClick={clearFilters}>Clear filters</button>}
      </div>

      <div className="toolbar">
        <button className="primary" onClick={() => doExport.mutate()} disabled={exportDisabled} data-testid="export-button">{doExport.isPending ? 'Exporting...' : label}</button>
        <div className="menu">
          <button onClick={() => setColMenu((v) => !v)} aria-expanded={colMenu} aria-haspopup="true">Columns</button>
          {colMenu && (
            <div className="menu-pop" role="group" aria-label="Visible columns">
              {COLS.filter((c) => !c.locked).map((c) => (
                <label key={c.key}><input type="checkbox" checked={!hidden.includes(c.key)} onChange={(e) => setColumn(c.key, e.target.checked)} /> {c.label}</label>
              ))}
            </div>
          )}
        </div>
        <div className="spacer" />
        {isAdmin && all.length > 0 && <button className="danger" onClick={openDeleteAll}>Delete all</button>}
        {isAdmin && <button onClick={() => setAdding(true)}>Add workstation</button>}
        {isAdmin && (
          <button onClick={() => importAd.mutate()} disabled={importAd.isPending} title="Read-only query of AD computer accounts, with their OUs">
            {importAd.isPending ? 'Importing...' : 'Import from AD'}
          </button>
        )}
      </div>
      <ErrorNotice error={doExport.error ?? importAd.error ?? q.error} />

      {(selected.size > 0 || allMatching) && (
        <div className="bulkbar" role="region" aria-label="Selection">
          <b data-testid="selection-count">
            {allMatching ? `All ${filteredCount} matching selected` : `${selected.size} selected${hiddenByFilter > 0 ? ` (${hiddenByFilter} hidden by filter)` : ''}`}
          </b>
          <span aria-hidden="true">·</span>
          <button onClick={() => doExport.mutate()} disabled={doExport.isPending}>Export CSV</button>
          {!allMatching && bulkActions.length > 0 && <button onClick={() => setBulkPicker(true)}>Run action...</button>}
          {isAdmin && !allMatching && <button onClick={() => setGroupEdit('add')}>Add to group...</button>}
          {isAdmin && !allMatching && <button onClick={() => setGroupEdit('remove')}>Remove from group...</button>}
          {isAdmin && !allMatching && <button className="danger" onClick={() => setRemoving('selected')}>Delete selected</button>}
          <div className="spacer" />
          <button className="link ghost" onClick={clearSelection}>Clear</button>
        </div>
      )}

      {banner && header === 'all' && !allMatching && (
        <div className="banner" role="status">
          All {rows.length} rows on screen selected.{' '}
          {filteredCount > rows.length && (
            <button className="link" onClick={() => { setAllMatching(true); setBanner(false); }}>Select all {filteredCount} matching endpoints?</button>
          )}
        </div>
      )}

      {q.isLoading && <Loading />}

      {q.data && (
        <div className="vtable" ref={scrollRef} role="table" aria-label="Workstations" aria-rowcount={rows.length + 1}>
          <div className="vinner" style={{ minWidth, height: virt.getTotalSize() + 40 }}>
            <div className="vhead" role="row" style={{ gridTemplateColumns: template }}>
              {visibleCols.map((c) => {
                const active = sort?.key === c.key;
                const style = c.sticky ? { left: stickyLeft[c.key] } : undefined;
                const cls = `vcell${c.sticky ? ' sticky' : ''}${c.key === lastSticky ? ' edge' : ''}`;
                if (c.key === 'select') {
                  return (
                    <div key={c.key} role="columnheader" className={cls} style={style}>
                      <input ref={headerRef} type="checkbox" aria-label="Select all rows in the current view" checked={header === 'all'} onChange={onHeader} data-testid="select-all" />
                    </div>
                  );
                }
                return (
                  <div key={c.key} role="columnheader" className={cls} style={style} aria-sort={active ? (sort!.dir === 'asc' ? 'ascending' : 'descending') : 'none'}>
                    <button className="sort" onClick={() => toggleSort(c.key)}>
                      {c.label}<span aria-hidden="true" className="sort-arrow">{active ? (sort!.dir === 'asc' ? '▲' : '▼') : '↕'}</span>
                    </button>
                  </div>
                );
              })}
            </div>

            {rows.length === 0 && <div className="empty" style={{ position: 'absolute', top: 60, left: 0, right: 0 }}>No workstations match.</div>}

            {virt.getVirtualItems().map((v) => {
              const e = rows[v.index];
              return (
                <div
                  key={e.id} role="row" data-id={e.id} data-testid="endpoint-row" aria-rowindex={v.index + 2}
                  className={`vrow${selected.has(e.id) ? ' sel' : ''}`} tabIndex={0}
                  style={{ gridTemplateColumns: template, transform: `translateY(${v.start + 40}px)`, minWidth }}
                  onClick={() => nav(endpointPath(e.hostname))}
                  // Enter opens the PC only when the ROW has the focus: Enter on a checkbox or link inside it belongs to that control.
                  onKeyDown={(ev) => { if (ev.key === 'Enter' && ev.target === ev.currentTarget) nav(endpointPath(e.hostname)); }}
                >
                  {visibleCols.map((c) => (
                    <div
                      key={c.key} role="cell"
                      className={`vcell${c.sticky ? ' sticky' : ''}${c.key === lastSticky ? ' edge' : ''}`}
                      style={c.sticky ? { left: stickyLeft[c.key] } : undefined}
                      onClick={c.key === 'select' ? (ev) => ev.stopPropagation() : undefined}
                      onKeyDown={c.key === 'select' ? (ev) => ev.stopPropagation() : undefined}
                    >
                      {cell(c, e)}
                    </div>
                  ))}
                </div>
              );
            })}
          </div>
        </div>
      )}

      {removing && (
        <ConfirmDialog
          title={removing === 'all' ? 'Delete ALL workstations' : 'Delete selected workstations'} danger
          requireText={removing === 'all' ? 'DELETE ALL' : `delete ${selected.size}`}
          confirmLabel={removing === 'all' ? `Delete all ${deletePreview.data?.count ?? '…'}` : `Delete ${selected.size}`}
          busy={removeMany_.isPending || (removing === 'all' && !deletePreview.data)}
          message={
            <>
              <p>{removing === 'all'
                ? <>This removes <b>every workstation</b> ({deletePreview.data ? deletePreview.data.count : '…'}) from Nexora. The Nexora server itself always stays.</>
                : <>This removes <b>{selected.size}</b> selected workstation(s) from Nexora.</>}</p>
              <p className="muted">
                Their CPU/RAM history, drive-health history and group memberships are deleted. The PCs themselves are not touched, and groups,
                scripts and the audit log are kept. This is recorded in the audit log.
              </p>
              <ErrorNotice error={removeMany_.error ?? deletePreview.error} />
            </>
          }
          onConfirm={() => removeMany_.mutate(removing === 'all')} onCancel={() => setRemoving(null)}
        />
      )}
      {bulkPicker && (
        <Dialog title={`Run an action on ${selected.size} workstations`} onClose={() => setBulkPicker(false)}>
          <div className="action-grid" style={{ gridTemplateColumns: '1fr' }}>
            {bulkActions.map((a) => (
              <button key={a.code} className="action-btn" onClick={() => { setBulkEntry(a); setBulkPicker(false); }} title={a.description}>
                <ActionLabel name={a.displayName} command={a.command} /> <RiskBadge risk={a.risk} />
              </button>
            ))}
          </div>
        </Dialog>
      )}
      {bulkEntry && (
        <RunActionDialog
          entry={bulkEntry} me={me} onClose={() => setBulkEntry(null)}
          targets={all.filter((e) => selected.has(e.id)).map((e) => ({ id: e.id, name: e.name, hostname: e.hostname }))}
          onCreated={(id) => { setBulkEntry(null); nav(`/jobs/${id}?output=1`); }}
        />
      )}
      {groupEdit && <BulkGroupDialog mode={groupEdit} ids={[...selected]} groups={groups.data ?? []} onClose={() => setGroupEdit(null)} />}
      {adding && <AddDialog groups={groups.data ?? []} onClose={() => setAdding(false)} />}
    </>
  );
}

/** Add the selected workstations to groups, or take them out of groups, in one step. */
function BulkGroupDialog({ mode, ids, groups, onClose }: { mode: 'add' | 'remove'; ids: string[]; groups: Group[]; onClose: () => void }) {
  const qc = useQueryClient();
  const [picked, setPicked] = useState<string[]>([]);
  const apply = useMutation({
    mutationFn: () => post<{ updated: number }>('/api/endpoints/bulk-groups', {
      ids,
      addGroupIds: mode === 'add' ? picked : [],
      removeGroupIds: mode === 'remove' ? picked : [],
    }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['endpoints'] }); qc.invalidateQueries({ queryKey: ['groups'] }); onClose(); },
  });
  return (
    <Dialog title={mode === 'add' ? `Add ${ids.length} workstations to groups` : `Remove ${ids.length} workstations from groups`} onClose={onClose}>
      <GroupPicker groups={groups} selected={picked} onChange={setPicked} />
      <ErrorNotice error={apply.error} />
      <div className="actions">
        <button onClick={onClose}>Cancel</button>
        <button className="primary" disabled={picked.length === 0 || apply.isPending} onClick={() => apply.mutate()}>
          {mode === 'add' ? 'Add to selected groups' : 'Remove from selected groups'}
        </button>
      </div>
    </Dialog>
  );
}

/** Lines like "Cashier 3, 10.1.4.23" or just "WKS-PC-0101" or "10.1.4.23". */
function parseLines(text: string) {
  return text.split(/\r?\n/).map((l) => l.trim()).filter(Boolean).map((l) => {
    const parts = l.split(/[,;\t]/).map((p) => p.trim()).filter(Boolean);
    return parts.length >= 2 ? { name: parts[0], address: parts[1] } : { name: null, address: parts[0] };
  });
}

export function GroupPicker({ groups, selected, onChange }: { groups: Group[]; selected: string[]; onChange: (ids: string[]) => void }) {
  if (!groups.length) return <p className="muted">No groups yet. Create one on the Groups page.</p>;
  return (
    <div className="pick-list" role="group" aria-label="Groups">
      {groups.map((g) => (
        <label key={g.id}>
          <input type="checkbox" checked={selected.includes(g.id)}
            onChange={(e) => onChange(e.target.checked ? [...selected, g.id] : selected.filter((x) => x !== g.id))} />
          {g.name} <span className="muted">{g.description}</span>
        </label>
      ))}
    </div>
  );
}

function AddDialog({ groups, onClose }: { groups: Group[]; onClose: () => void }) {
  const qc = useQueryClient();
  const [mode, setMode] = useState<'one' | 'many'>('one');
  const [name, setName] = useState('');
  const [address, setAddress] = useState('');
  const [text, setText] = useState('');
  const [selected, setSelected] = useState<string[]>([]);

  const add = useMutation({
    mutationFn: () =>
      post<EndpointSummary[]>('/api/endpoints', {
        items: mode === 'one' ? [{ name: name.trim() || null, address: address.trim() }] : parseLines(text),
        groupIds: selected,
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['endpoints'] });
      qc.invalidateQueries({ queryKey: ['groups'] });
      onClose();
    },
  });
  const ready = mode === 'one' ? address.trim().length > 0 : text.trim().length > 0;

  return (
    <Dialog title="Add workstation" onClose={onClose}>
      <div className="segmented" role="group" aria-label="Mode" style={{ marginBottom: 14 }}>
        <button className={mode === 'one' ? 'on' : ''} onClick={() => setMode('one')}>One workstation</button>
        <button className={mode === 'many' ? 'on' : ''} onClick={() => setMode('many')}>Several</button>
      </div>

      {mode === 'one' ? (
        <div className="inline-form">
          <div className="form-row">
            <label htmlFor="wn">Workstation name</label>
            <input id="wn" value={name} placeholder="Cashier desk 3" maxLength={100} onChange={(e) => setName(e.target.value)} data-autofocus />
          </div>
          <div className="form-row">
            <label htmlFor="wa">IP address or host name</label>
            <input id="wa" value={address} placeholder="10.1.4.23 or WKS-PC-0103" onChange={(e) => setAddress(e.target.value)} />
          </div>
        </div>
      ) : (
        <div className="form-row">
          <label htmlFor="hosts">One per line: <span className="mono">name, address</span> or just the address</label>
          <textarea id="hosts" value={text} onChange={(e) => setText(e.target.value)}
            placeholder={'Cashier desk 3, 10.1.4.23\nWKS-PC-0102\n10.1.4.24'} />
        </div>
      )}

      <div className="form-row" style={{ marginTop: 12 }}>
        <span id="add-groups-label" className="label">Add to groups (optional)</span>
        <GroupPicker groups={groups} selected={selected} onChange={setSelected} />
      </div>
      <ErrorNotice error={add.error} />
      <div className="actions">
        <button onClick={onClose}>Cancel</button>
        <button className="primary" disabled={!ready || add.isPending} onClick={() => add.mutate()}>Add</button>
      </div>
    </Dialog>
  );
}
