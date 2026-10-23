import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { ApiError, get, post } from '../api';
import { RiskBadge } from '../jobs/JobParts';
import type { JobDetailDto } from '../jobs/jobTypes';
import { formatMinute } from '../lib/format';
import { parseReasons, sourceLabel, parseDeleteConfirm, parseDeleteResult, parseProfiles, staleDeletable, visibleUserProfile } from '../lib/profiles';
import { endpointJobPath } from '../nav/origin';
import { OperatorRequired } from '../pages/endpoint/liveDetails';
import { useOnJobStarted } from '../pages/endpoint/startedJob';
import { roleRank, type Me, type ProfileDeleteConfirm, type ProfileRowDto } from '../types';
import { useCatalog } from '../jobs/catalog';
import { Dialog, ErrorNotice, Loading, fmtBytes, useSort } from '../ui';

export interface EndpointRef { id: string; name: string; hostname: string; online: boolean }

const errText = (e: unknown) => (e instanceof Error ? e.message : String(e));
const WAIT_MS = 10 * 60_000;

interface DeleteOutcome { items: { sid: string; account: string | null; path: string }[]; jobIds: string[] }

/**
 * The profiles of one workstation. Sizes are measured by the helper (job `profile.sizes`) and stored by the server; this panel
 * only asks for a measurement and re-reads the list. Deleting is Admin-only, two-step (confirm token, then typed hostname + reason),
 * and the age filter only ever selects rows. Every string from the server is rendered as text.
 */
export function ProfilesPanel({ endpoint, me }: { endpoint: EndpointRef; me: Me }) {
  const onStarted = useOnJobStarted();
  const isOperator = roleRank[me.role] >= roleRank.Operator;
  const isAdmin = roleRank[me.role] >= roleRank.Admin;

  const [filter, setFilter] = useState('');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [daysText, setDaysText] = useState('90');
  const [note, setNote] = useState<string | null>(null);
  const [sizeError, setSizeError] = useState<string | null>(null);
  const [sizeJob, setSizeJob] = useState<string | null>(null);
  const [pending, setPending] = useState<Record<string, { prev: string | null; until: number }>>({});
  const [confirm, setConfirm] = useState<ProfileDeleteConfirm | null>(null);
  const [confirmBusy, setConfirmBusy] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [deleteReasons, setDeleteReasons] = useState<{ sid: string; reason: string }[]>([]);
  const [outcome, setOutcome] = useState<DeleteOutcome | null>(null);

  const q = useQuery({
    queryKey: ['profiles', endpoint.id],
    queryFn: async () => parseProfiles(await get<unknown>(`/api/endpoints/${endpoint.id}/profiles`)),
    enabled: isOperator, staleTime: 15_000,
    retry: (n, err) => !(err instanceof ApiError && (err.status === 403 || err.status === 401)) && n < 1,
    refetchInterval: () => (Object.values(pending).some((p) => p.until > Date.now()) ? 4000 : false),
  });
  const items = useMemo(() => (q.data?.items ?? []).filter(visibleUserProfile), [q.data]);
  const offline = !endpoint.online;
  const grey = offline || !!q.data?.unavailable;
  const canAct = !grey;

  const busySids = useMemo(() => {
    const s = new Set<string>();
    for (const r of items) {
      const p = pending[r.sid];
      if (p && p.until > Date.now() && r.sizeMeasuredUtc === p.prev) s.add(r.sid);
    }
    return s;
  }, [pending, items]);

  const f = filter.trim().toLowerCase();
  const rows = items.filter((r) => !f || (r.account ?? '').toLowerCase().includes(f) || r.sid.toLowerCase().includes(f) || r.localPath.toLowerCase().includes(f));
  const { sorted, th } = useSort(rows, {
    account: (r) => r.account ?? r.sid,
    size: (r) => r.sizeBytes,
    used: (r) => (r.lastUsedUtc ? Date.parse(r.lastUsedUtc) : null),
  }, { key: 'used', dir: 'desc' });

  const deletableRows = sorted.filter((r) => r.deletable);
  const selectedRows = items.filter((r) => selected.has(r.sid) && r.deletable);

  const toggle = (sid: string) => setSelected((s) => { const n = new Set(s); if (n.has(sid)) n.delete(sid); else n.add(sid); return n; });
  const allChecked = deletableRows.length > 0 && deletableRows.every((r) => selected.has(r.sid));
  const toggleAll = () => setSelected((s) => {
    const n = new Set(s);
    for (const r of deletableRows) { if (allChecked) n.delete(r.sid); else n.add(r.sid); }
    return n;
  });

  const daysValid = /^\d{1,4}$/.test(daysText.trim()) && Number(daysText.trim()) > 0;
  const applyAge = () => {
    const ids = staleDeletable(items, Number(daysText.trim()), Date.now());
    setSelected((s) => new Set([...s, ...ids]));
    setNote(`${ids.length} profile${ids.length === 1 ? '' : 's'} matched and selected. Nothing has been deleted.`);
  };

  const calculate = async (sid?: string) => {
    setSizeError(null);
    const targets = sid ? [sid] : items.map((r) => r.sid);
    try {
      const created = await post<JobDetailDto>('/api/jobs', { actionCode: 'profile.sizes', endpointIds: [endpoint.id], parameters: sid ? { sid } : {} });
      const until = Date.now() + WAIT_MS;
      setPending((p) => {
        const n = { ...p };
        for (const r of items) if (targets.includes(r.sid)) n[r.sid] = { prev: r.sizeMeasuredUtc, until };
        return n;
      });
      setSizeJob(created.job.id);
      onStarted(created.job.id);
    } catch (e) {
      setSizeError(errText(e));
    }
  };

  const startDelete = async () => {
    setDeleteError(null); setDeleteReasons([]); setOutcome(null); setConfirmBusy(true);
    try {
      setConfirm(parseDeleteConfirm(await post<unknown>(`/api/endpoints/${endpoint.id}/profiles/delete-batch/confirm`, { sids: selectedRows.map((r) => r.sid) })));
    } catch (e) {
      setDeleteError(errText(e));
      setDeleteReasons(parseReasons(e));
    } finally {
      setConfirmBusy(false);
    }
  };

  if (!isOperator) return <OperatorRequired what="user profiles" />;

  const measuredTotal = items.reduce((sum, r) => sum + (r.sizeBytes ?? 0), 0);

  return (
    <div className="profiles">
      {offline && (
        <div className="notice warn" role="status" data-testid="profiles-offline">
          {endpoint.name} is offline. {items.length > 0 ? 'These are the last known profiles; actions are disabled.' : 'Its profiles cannot be read, and actions are disabled.'}
        </div>
      )}
      {q.data?.unavailable && !offline && <div className="notice warn" role="status" data-testid="profiles-unavailable">Profiles are unavailable right now{q.data.reason ? `: ${q.data.reason}` : '.'}</div>}
      <ErrorNotice error={q.error} />
      {sizeError && <div className="notice error" role="alert">Could not start the size calculation: {sizeError}</div>}
      {deleteError && !confirm && <div className="notice error" role="alert">{deleteError}<Reasons reasons={deleteReasons} items={items} /></div>}

      <div className="toolbar">
        <input type="search" placeholder="Search profiles" aria-label="Search profiles" value={filter} onChange={(ev) => setFilter(ev.target.value)} />
        <button onClick={() => void q.refetch()} disabled={q.isFetching}>{q.isFetching ? 'Reading...' : 'Refresh'}</button>
        <button onClick={() => void calculate()} disabled={!canAct || items.length === 0 || busySids.size > 0}
          title="The helper adds up each profile folder with your own rights. Read-only.">Calculate all</button>
        <span className="muted small" role="status">{rows.length} of {items.length}{measuredTotal > 0 ? ` · ${fmtBytes(measuredTotal)} calculated` : ''}</span>
        {sizeJob && <Link className="small" to={endpointJobPath(endpoint.hostname, sizeJob, { from: `/endpoints/${encodeURIComponent(endpoint.hostname)}/profiles` })}>Open size job</Link>}
      </div>

      <div className="toolbar" role="group" aria-label="Select by age">
        <span className="tag">Selection only</span>
        <label>Select profiles not used for{' '}
          <input inputMode="numeric" aria-label="Days" value={daysText} onChange={(ev) => setDaysText(ev.target.value)} style={{ width: 60 }} /> days
        </label>
        <button onClick={applyAge} disabled={!daysValid || items.length === 0}>Select matching</button>
        <span className="muted small">Only removable profiles with a known last use are selected. This does not delete anything.</span>
      </div>
      {note && <div className="notice" role="status">{note}</div>}

      {isAdmin ? (
        <div className="toolbar">
          <button className="danger" onClick={() => void startDelete()} disabled={!canAct || selectedRows.length === 0 || confirmBusy}>
            {confirmBusy ? 'Checking...' : `Delete selected (${selectedRows.length})`}
          </button>
          <span className="muted small">You will review every profile and confirm before anything is deleted.</span>
        </div>
      ) : (
        <div className="muted small" data-testid="delete-admin-only">Deleting profiles requires the Admin role.</div>
      )}

      {outcome && (
        <div className="notice" role="status" data-testid="delete-results">
          <b>Deletion started for {outcome.items.length} profile{outcome.items.length === 1 ? '' : 's'}.</b>
          <ul>
            {outcome.items.map((it, i) => {
              const id = outcome.jobIds[i];
              return (
                <li key={it.sid}>{it.account ?? it.sid} <span className="mono muted">{it.path}</span>{' '}
                  {id ? <Link to={endpointJobPath(endpoint.hostname, id, { from: `/endpoints/${encodeURIComponent(endpoint.hostname)}/profiles` })} aria-label={`Open job for ${it.account ?? it.sid}`}>Open job</Link> : <span className="muted">no job</span>}
                </li>
              );
            })}
          </ul>
        </div>
      )}

      <div className="tablewrap ptable-wrap">
        <table className="ptable" aria-label="Profiles" style={grey ? { opacity: 0.6 } : undefined}>
          <thead>
            <tr>
              <th style={{ width: 32 }}>{isAdmin && <input type="checkbox" aria-label="Select all removable profiles" checked={allChecked} onChange={toggleAll} disabled={deletableRows.length === 0} />}</th>
              {th('account', 'Account')}<th>Profile folder</th>{th('size', 'Size', 'num')}{th('used', 'Last used')}<th>State</th>
            </tr>
          </thead>
          <tbody>
            {q.isLoading && <tr><td colSpan={6}><Loading label="Reading profiles" /></td></tr>}
            {q.data && items.length === 0 && <tr><td colSpan={6} className="empty">No user profiles to show on {endpoint.name}.</td></tr>}
            {sorted.map((r) => <Row key={r.sid} r={r} isAdmin={isAdmin} checked={selected.has(r.sid)} onToggle={() => toggle(r.sid)}
              busy={busySids.has(r.sid)} canAct={canAct} onCalc={() => void calculate(r.sid)} />)}
          </tbody>
        </table>
      </div>

      {confirm && (
        <DeleteDialog endpointId={endpoint.id} confirm={confirm} rows={items} onCancel={() => setConfirm(null)}
          onDone={(jobIds) => {
            setOutcome({ items: confirm.items, jobIds });
            setConfirm(null); setSelected(new Set()); setNote(null);
            void q.refetch();
            if (jobIds[0]) onStarted(jobIds[0]);
          }} />
      )}
    </div>
  );
}

function Row({ r, isAdmin, checked, onToggle, busy, canAct, onCalc }: {
  r: ProfileRowDto; isAdmin: boolean; checked: boolean; onToggle: () => void; busy: boolean; canAct: boolean; onCalc: () => void;
}) {
  const label = r.account ?? r.sid;
  const lockTip = r.blockReason ?? 'Cannot be deleted';
  return (
    <tr>
      <td>{isAdmin && <input type="checkbox" aria-label={`Select ${label}`} checked={checked && r.deletable} disabled={!r.deletable} onChange={onToggle} title={r.deletable ? undefined : lockTip} />}</td>
      <td title={`${r.account ?? 'unresolved account'}\n${r.sid}`}>
        <div className="ep-cell">
          {r.account ? <b className="ep-name">{r.account}</b> : <b className="ep-name muted">{r.sid}</b>}
          {r.account && <span className="ep-ip muted">{r.sid}</span>}
          {!r.deletable && <span className="tag warn" title={lockTip}>🔒 Locked{r.blockReason ? `: ${r.blockReason}` : ''}</span>}
        </div>
      </td>
      <td className="mono ellip" title={r.localPath}>{r.localPath}</td>
      <td className="num">
        {r.sizeBytes != null ? (
          <>
            {fmtBytes(r.sizeBytes)}
            {r.sizeMeasuredUtc && <div className="muted small">calculated {formatMinute(r.sizeMeasuredUtc)}</div>}
            {busy ? <div className="muted small">recalculating…</div>
              : <button className="small ghost" onClick={onCalc} disabled={!canAct} aria-label={`Recalculate ${label}`}>Recalculate</button>}
          </>
        ) : busy ? <span className="muted">calculating…</span> : (
          <><span className="muted">Not calculated</span>{' '}<button className="small ghost" onClick={onCalc} disabled={!canAct} aria-label={`Calculate ${label}`}>Calculate</button></>
        )}
      </td>
      <td>
        {r.lastUsedUtc ? (
          <span title={r.lastUsedSource ? sourceLabel(r.lastUsedSource) : undefined}>
            {formatMinute(r.lastUsedUtc)}{r.lastUsedSource && <div className="muted small">{sourceLabel(r.lastUsedSource)}</div>}
          </span>
        ) : <span className="muted">Unknown</span>}
      </td>
      <td><span className={`tag ${r.loaded ? 'ok' : ''}`}>{r.loaded ? 'Loaded' : 'Not loaded'}</span><div className="muted small">{r.state ?? 'Unavailable'}</div></td>
    </tr>
  );
}

function Reasons({ reasons, items }: { reasons: { sid: string; reason: string }[]; items: { sid: string; account: string | null; localPath: string }[] }) {
  if (reasons.length === 0) return null;
  return (
    <ul data-testid="delete-reasons">
      {reasons.map((x) => {
        const row = items.find((i) => i.sid === x.sid);
        return <li key={x.sid}>{row?.account ?? x.sid}{row ? <> <span className="mono muted">{row.localPath}</span></> : null}: {x.reason}</li>;
      })}
    </ul>
  );
}

function DeleteDialog({ endpointId, confirm, rows, onCancel, onDone }: { endpointId: string; confirm: ProfileDeleteConfirm; rows: ProfileRowDto[]; onCancel: () => void; onDone: (jobIds: string[]) => void }) {
  const [reason, setReason] = useState('');
  const [typed, setTyped] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [reasons, setReasons] = useState<{ sid: string; reason: string }[]>([]);
  const catalog = useCatalog();
  const risk = catalog.data?.find((c) => c.code.startsWith('profile.delete'))?.risk ?? 'Critical';
  const total = confirm.items.reduce((s, i) => s + (i.sizeBytes ?? 0), 0);
  const unknown = confirm.items.some((i) => i.sizeBytes == null);
  const locked = busy || reason.trim() === '' || typed !== confirm.expectedHostname;

  const run = async () => {
    if (locked) return;
    setBusy(true); setError(null); setReasons([]);
    try {
      const res = parseDeleteResult(await post<unknown>(`/api/endpoints/${endpointId}/profiles/delete-batch`, {
        sids: confirm.items.map((i) => i.sid), token: confirm.token, typed, reason: reason.trim(),
      }));
      onDone(res.jobIds);
    } catch (e) {
      setError(errText(e));
      setReasons(parseReasons(e));
      setBusy(false);
    }
  };

  return (
    <Dialog title="Delete user profiles" onClose={busy ? () => undefined : onCancel} wide>
      <p><RiskBadge risk={risk} /> This permanently deletes {confirm.count} profile{confirm.count === 1 ? '' : 's'} and all their files from <b>{confirm.expectedHostname}</b>. It cannot be undone.</p>
      <div className="tablewrap"><table aria-label="Profiles to delete">
        <thead><tr><th>Account</th><th>Path</th><th className="num">Size</th></tr></thead>
        <tbody>
          {confirm.items.map((i) => (
            <tr key={i.sid}><td>{i.account ?? i.sid}</td><td className="mono">{i.path}</td><td className="num">{i.sizeBytes != null ? fmtBytes(i.sizeBytes) : 'Not calculated'}</td></tr>
          ))}
        </tbody>
        <tfoot><tr><td colSpan={2}><b>Total</b></td><td className="num"><b data-testid="delete-total">{fmtBytes(total)}</b>{unknown ? ' + unknown' : ''}</td></tr></tfoot>
      </table></div>
      <div className="form-row" style={{ marginTop: 12 }}>
        <label htmlFor="pd-reason">Reason (required)</label>
        <input id="pd-reason" value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} autoComplete="off" />
      </div>
      <div className="form-row">
        <label htmlFor="pd-typed">Type the hostname <b>{confirm.expectedHostname}</b> to confirm</label>
        <input id="pd-typed" data-autofocus value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" spellCheck={false} />
      </div>
      {error && <div className="notice error" role="alert">{error}<Reasons reasons={reasons} items={rows} /></div>}
      <div className="actions">
        <button onClick={onCancel} disabled={busy}>Cancel</button>
        <button className="danger" onClick={() => void run()} disabled={locked}>{busy ? 'Working...' : 'Run'}</button>
      </div>
    </Dialog>
  );
}
