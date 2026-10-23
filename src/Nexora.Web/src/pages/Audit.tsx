import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { get } from '../api';
import { roleRank, type AuditEntry, type Me, type Paged } from '../types';
import { useCatalogIndex } from '../jobs/catalog';
import { auditActionName } from '../lib/auditLabel';
import { Dialog, ErrorNotice, Loading, fmtDate } from '../ui';

export function Audit({ me }: { me: Me }) {
  const isAdmin = roleRank[me.role] >= roleRank.Admin;
  const [text, setText] = useState('');
  const [action, setAction] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState<AuditEntry | null>(null);
  const index = useCatalogIndex();

  const params = new URLSearchParams({ page: String(page), pageSize: '50' });
  if (text.trim()) params.set('text', text.trim());
  if (action) params.set('action', action);
  if (from) params.set('from', new Date(from).toISOString());
  if (to) params.set('to', new Date(to + 'T23:59:59').toISOString());

  const q = useQuery({ queryKey: ['audit', params.toString()], queryFn: () => get<Paged<AuditEntry>>(`/api/audit?${params}`) });
  const pages = q.data ? Math.max(1, Math.ceil(q.data.total / q.data.pageSize)) : 1;
  const reset = <T,>(set: (v: T) => void) => (v: T) => { set(v); setPage(1); };

  return (
    <>
      <h1>Audit log</h1>
      <p className="sub">{isAdmin ? 'All entries. The log is append-only.' : 'Your own entries. Admins can see everyone\'s.'}</p>

      <div className="toolbar">
        <input placeholder="Search" aria-label="Search audit" value={text} onChange={(e) => reset(setText)(e.target.value)} />
        <select aria-label="Action type" value={action} onChange={(e) => reset(setAction)(e.target.value)}>
          <option value="">All actions</option>
          <option value="job.">Remote actions</option>
          <option value="role.">Role changes</option>
          <option value="access.">Access requests</option>
          <option value="settings.">Settings</option>
          <option value="script.">Scripts</option>
          <option value="endpoint.">Endpoints</option>
        </select>
        <label>From <input type="date" value={from} onChange={(e) => reset(setFrom)(e.target.value)} /></label>
        <label>To <input type="date" value={to} onChange={(e) => reset(setTo)(e.target.value)} /></label>
      </div>

      {q.isLoading && <Loading />}
      <ErrorNotice error={q.error} />
      {q.data && (
        <>
          <div className="tablewrap">
            <table>
              <thead><tr><th>When</th><th>Actor</th><th>Action</th><th>Target</th><th>Change</th><th>IP</th><th>Result</th><th /></tr></thead>
              <tbody>
                {q.data.items.length === 0 && <tr><td colSpan={8} className="empty">No entries.</td></tr>}
                {q.data.items.map((a) => (
                  <tr key={a.id}>
                    <td>{fmtDate(a.timestampUtc)}</td><td>{a.actorAccount}</td><td><span className="mono">{a.action}</span>{auditActionName(a, index) && <div><b>{auditActionName(a, index)}</b></div>}</td>
                    <td>{a.targetId ?? '-'}</td>
                    <td>{a.oldValue || a.newValue ? `${a.oldValue ?? '-'} → ${a.newValue ?? '-'}` : '-'}</td>
                    <td className="mono">{a.clientIp ?? '-'}</td>
                    <td><span className={`tag ${a.success ? 'ok' : 'bad'}`}>{a.success ? 'OK' : 'Failed'}</span></td>
                    <td>{a.details && <button className="small" onClick={() => setOpen(a)}>Details</button>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="toolbar" style={{ marginTop: 12 }}>
            <button disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button>
            <span className="muted">Page {page} of {pages} &middot; {q.data.total} entries</span>
            <button disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button>
          </div>
        </>
      )}

      {open && (
        <Dialog title={open.action} onClose={() => setOpen(null)}>
          <pre className="out">{JSON.stringify(JSON.parse(open.details ?? '{}'), null, 2)}</pre>
          <div className="actions"><button onClick={() => setOpen(null)}>Close</button></div>
        </Dialog>
      )}
    </>
  );
}
