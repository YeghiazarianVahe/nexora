import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { get, post, put } from '../api';
import type { AccessRequest, DirectoryUser, UserRow } from '../types';
import { ConfirmDialog, Dialog, ErrorNotice, Loading, fmtDate } from '../ui';

const ASSIGNABLE = ['Viewer', 'Operator', 'Admin'] as const;
type Tab = 'requests' | 'users' | 'add';

export function RoleManagement() {
  const [tab, setTab] = useState<Tab>('requests');
  const pending = useQuery({
    queryKey: ['admin', 'requests', 'Pending'],
    queryFn: () => get<AccessRequest[]>('/api/admin/access-requests?status=Pending'),
  });

  return (
    <>
      <h1>Role Management</h1>
      <p className="sub">Only you can see this. Every change is written to the audit log with your identity, the target, old and new role.</p>
      <div className="tabs" role="tablist">
        <button role="tab" className={tab === 'requests' ? 'on' : ''} onClick={() => setTab('requests')}>
          Pending requests {!!pending.data?.length && <span className="badge">{pending.data.length}</span>}
        </button>
        <button role="tab" className={tab === 'users' ? 'on' : ''} onClick={() => setTab('users')}>Users</button>
        <button role="tab" className={tab === 'add' ? 'on' : ''} onClick={() => setTab('add')}>Add user</button>
      </div>
      {tab === 'requests' && <Requests requests={pending.data} loading={pending.isLoading} error={pending.error} />}
      {tab === 'users' && <Users />}
      {tab === 'add' && <AddUser />}
    </>
  );
}

function Requests({ requests, loading, error }: { requests?: AccessRequest[]; loading: boolean; error: unknown }) {
  const qc = useQueryClient();
  const [roles, setRoles] = useState<Record<string, string>>({});
  const [rejecting, setRejecting] = useState<AccessRequest | null>(null);
  const [reason, setReason] = useState('');
  const refresh = () => qc.invalidateQueries({ queryKey: ['admin'] });

  const approve = useMutation({
    mutationFn: (r: AccessRequest) => post(`/api/admin/access-requests/${r.id}/approve`, { role: roles[r.id] ?? 'Viewer' }),
    onSuccess: refresh,
  });
  const reject = useMutation({
    mutationFn: (r: AccessRequest) => post(`/api/admin/access-requests/${r.id}/reject`, { reason }),
    onSuccess: () => { refresh(); setRejecting(null); setReason(''); },
  });

  if (loading) return <Loading />;
  return (
    <>
      <ErrorNotice error={error ?? approve.error} />
      <div className="tablewrap">
        <table>
          <thead><tr><th>User</th><th>Account</th><th>Requested</th><th>Grant role</th><th /></tr></thead>
          <tbody>
            {requests?.length === 0 && <tr><td colSpan={5} className="empty">No pending requests.</td></tr>}
            {requests?.map((r) => (
              <tr key={r.id}>
                <td><b>{r.displayName}</b></td><td className="mono">{r.account}</td><td>{fmtDate(r.requestedUtc)}</td>
                <td>
                  <select aria-label={`Role for ${r.displayName}`} value={roles[r.id] ?? 'Viewer'} onChange={(e) => setRoles({ ...roles, [r.id]: e.target.value })}>
                    {ASSIGNABLE.map((x) => <option key={x}>{x}</option>)}
                  </select>
                </td>
                <td>
                  <button className="small primary" disabled={approve.isPending} onClick={() => approve.mutate(r)}>Approve</button>{' '}
                  <button className="small danger" onClick={() => setRejecting(r)}>Reject</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {rejecting && (
        <Dialog title={`Reject ${rejecting.displayName}`} onClose={() => setRejecting(null)}>
          <div className="form-row">
            <label htmlFor="why">Reason (shown to the user)</label>
            <input id="why" value={reason} maxLength={500} onChange={(e) => setReason(e.target.value)} data-autofocus />
          </div>
          <ErrorNotice error={reject.error} />
          <div className="actions">
            <button onClick={() => setRejecting(null)}>Cancel</button>
            <button className="danger" disabled={!reason.trim() || reject.isPending} onClick={() => reject.mutate(rejecting)}>Reject</button>
          </div>
        </Dialog>
      )}
    </>
  );
}

function Users() {
  const qc = useQueryClient();
  const [search, setSearch] = useState('');
  const [role, setRole] = useState('');
  const [revoking, setRevoking] = useState<UserRow | null>(null);
  const params = new URLSearchParams();
  if (search.trim()) params.set('search', search.trim());
  if (role) params.set('role', role);

  const q = useQuery({ queryKey: ['admin', 'users', params.toString()], queryFn: () => get<UserRow[]>(`/api/admin/users?${params}`) });
  const refresh = () => qc.invalidateQueries({ queryKey: ['admin'] });
  const change = useMutation({
    mutationFn: (v: { sid: string; role: string }) => put(`/api/admin/users/${v.sid}/role`, { role: v.role }),
    onSuccess: refresh,
  });
  const revoke = useMutation({
    mutationFn: (sid: string) => post(`/api/admin/users/${sid}/revoke`),
    onSuccess: () => { refresh(); setRevoking(null); },
  });

  return (
    <>
      <div className="toolbar">
        <input placeholder="Search name or account" aria-label="Search users" value={search} onChange={(e) => setSearch(e.target.value)} />
        <select aria-label="Role filter" value={role} onChange={(e) => setRole(e.target.value)}>
          <option value="">All roles</option>
          {ASSIGNABLE.map((x) => <option key={x}>{x}</option>)}
        </select>
      </div>
      <ErrorNotice error={q.error ?? change.error} />
      {q.isLoading && <Loading />}
      <div className="tablewrap">
        <table>
          <thead><tr><th>User</th><th>Account</th><th>Role</th><th>Status</th><th>Last login</th><th>Auth</th><th /></tr></thead>
          <tbody>
            {q.data?.length === 0 && <tr><td colSpan={7} className="empty">No users.</td></tr>}
            {q.data?.map((u) => (
              <tr key={u.sid}>
                <td><b>{u.displayName}</b></td><td className="mono">{u.account}</td>
                <td>
                  {u.status === 'Active' ? (
                    <select aria-label={`Role of ${u.displayName}`} value={u.role} disabled={change.isPending}
                      onChange={(e) => change.mutate({ sid: u.sid, role: e.target.value })}>
                      {ASSIGNABLE.map((x) => <option key={x}>{x}</option>)}
                    </select>
                  ) : u.role}
                </td>
                <td><span className={`tag ${u.status === 'Active' ? 'ok' : 'bad'}`}>{u.status}</span></td>
                <td>{fmtDate(u.lastSeenUtc)}</td>
                <td>{u.lastAuthScheme ?? '-'}</td>
                <td>{u.status === 'Active' && <button className="small danger" onClick={() => setRevoking(u)}>Revoke</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {revoking && (
        <ConfirmDialog title="Revoke access" danger confirmLabel="Revoke" busy={revoke.isPending}
          message={<>Revoke Nexora access for <b>{revoking.displayName}</b> ({revoking.role})? They are signed out of live updates immediately.</>}
          onConfirm={() => revoke.mutate(revoking.sid)} onCancel={() => setRevoking(null)} />
      )}
    </>
  );
}

function AddUser() {
  const qc = useQueryClient();
  const [text, setText] = useState('');
  const [role, setRole] = useState('Viewer');
  const [added, setAdded] = useState<string | null>(null);
  const q = useQuery({
    queryKey: ['admin', 'directory', text.trim()],
    queryFn: () => get<DirectoryUser[]>(`/api/admin/directory?q=${encodeURIComponent(text.trim())}`),
    enabled: text.trim().length >= 2,
  });
  const add = useMutation({
    mutationFn: (u: DirectoryUser) => post('/api/admin/users', { sid: u.sid, role }),
    onSuccess: (_d, u) => { setAdded(u.displayName); qc.invalidateQueries({ queryKey: ['admin'] }); },
  });

  return (
    <>
      <p className="sub">Search Active Directory (read-only) and grant access before the person ever signs in.</p>
      <div className="toolbar">
        <input placeholder="Name or account (min 2 characters)" aria-label="Directory search" value={text} onChange={(e) => { setText(e.target.value); setAdded(null); }} style={{ minWidth: 280 }} />
        <label>Role&nbsp;
          <select value={role} onChange={(e) => setRole(e.target.value)}>
            {ASSIGNABLE.map((x) => <option key={x}>{x}</option>)}
          </select>
        </label>
      </div>
      {added && <div className="notice">Added {added} as {role}.</div>}
      <ErrorNotice error={q.error ?? add.error} />
      {q.isFetching && <Loading label="Searching the directory" />}
      <div className="tablewrap">
        <table>
          <thead><tr><th>Name</th><th>Account</th><th /></tr></thead>
          <tbody>
            {q.data?.length === 0 && <tr><td colSpan={3} className="empty">No matches.</td></tr>}
            {q.data?.map((u) => (
              <tr key={u.sid}>
                <td><b>{u.displayName}</b></td><td className="mono">{u.account}</td>
                <td><button className="small primary" disabled={add.isPending} onClick={() => add.mutate(u)}>Add as {role}</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
