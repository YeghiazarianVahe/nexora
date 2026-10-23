import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { endpointPath } from '../nav/origin';
import { del, get, post, put } from '../api';
import { useNavigate } from 'react-router-dom';
import { useCatalog } from '../jobs/RemoteActions';
import { RunActionDialog } from '../jobs/RunActionDialog';
import { roleRank, type EndpointSummary, type Group, type Me } from '../types';
import { ConfirmDialog, Dialog, DiskHealthTag, ErrorNotice, Loading, Meter, OnlineTag, useSort } from '../ui';
import { useGroups } from './Endpoints';

export function Groups({ me }: { me: Me }) {
  const isAdmin = roleRank[me.role] >= roleRank.Admin;
  const canRun = roleRank[me.role] >= roleRank.Operator;
  const groups = useGroups();
  const endpoints = useQuery({ queryKey: ['endpoints'], queryFn: () => get<EndpointSummary[]>('/api/endpoints') });
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [editing, setEditing] = useState<Partial<Group> | null>(null);
  const [members, setMembers] = useState(false);
  const [running, setRunning] = useState(false);
  const nav = useNavigate();
  const restart = useCatalog().data?.find((c) => c.code === 'svc.restart' && c.allowed && c.implemented) ?? null;
  const [deleting, setDeleting] = useState(false);

  const selected = groups.data?.find((g) => g.id === selectedId) ?? null;
  const members_ = (endpoints.data ?? []).filter((e) => selected?.memberIds.includes(e.id));
  const { sorted: memberRows, th } = useSort(members_, {
    status: (e) => (e.online ? 1 : 0), name: (e) => e.name, address: (e) => e.hostname, user: (e) => e.lastUser,
    cpu: (e) => e.cpuPercent, ram: (e) => e.ramPercent, health: (e) => e.diskHealthPercent,
  }, { key: 'name', dir: 'asc' });

  return (
    <>
      <h1>Groups</h1>
      <p className="sub">Sets of workstations for a specific job: a branch, a role, a pilot ring. Run a task on a whole group at once.</p>
      <div className="toolbar">
        {isAdmin && <button className="primary" onClick={() => setEditing({ name: '', description: '' })}>New group</button>}
      </div>
      <ErrorNotice error={groups.error} />
      <ErrorNotice error={endpoints.error} />
      {endpoints.isError && <button onClick={() => void endpoints.refetch()}>Retry workstations</button>}
      {endpoints.isLoading && <Loading label="Loading workstations" />}
      {groups.isLoading && <Loading />}

      <div className="group-grid">
        {groups.data?.length === 0 && <div className="notice">No groups yet.{isAdmin ? ' Create the first one.' : ''}</div>}
        {groups.data?.map((g) => (
          <button key={g.id} className={`group-card ${g.id === selectedId ? 'on' : ''}`} onClick={() => setSelectedId(g.id)}>
            <b>{g.name}</b>
            <span className="muted">{g.description || 'No description'}</span>
            <div style={{ marginTop: 8 }}><span className="tag ok">{g.online} online</span> <span className="tag">{g.memberIds.length} total</span></div>
          </button>
        ))}
      </div>

      {selected && (
        <>
          <div className="toolbar">
            <h2 style={{ margin: 0 }}>{selected.name}</h2>
            <div className="spacer" />
            {canRun && restart && <button className="primary" onClick={() => setRunning(true)} disabled={!endpoints.isSuccess || selected.online === 0}>Restart a service on the group</button>}
            {isAdmin && <button onClick={() => setMembers(true)} disabled={!endpoints.isSuccess}>Edit members</button>}
            {isAdmin && <button onClick={() => setEditing(selected)}>Rename</button>}
            {isAdmin && <button className="danger" onClick={() => setDeleting(true)}>Delete group</button>}
          </div>
          <div className="tablewrap">
            <table>
              <thead><tr>{th('status', 'Status')}{th('name', 'Workstation')}{th('address', 'Address')}{th('user', 'User')}{th('cpu', 'CPU')}{th('ram', 'RAM')}{th('health', 'Drives')}</tr></thead>
              <tbody>
                {endpoints.isSuccess && memberRows.length === 0 && <tr><td colSpan={7} className="empty">No members yet.{isAdmin ? ' Use Edit members.' : ''}</td></tr>}
                {memberRows.map((e) => (
                  <tr key={e.id}>
                    <td><OnlineTag online={e.online} /></td>
                    <td><Link to={endpointPath(e.hostname)}>{e.name}</Link></td>
                    <td className="mono">{e.hostname}</td>
                    <td>{e.lastUser ?? '-'}</td>
                    <td><Meter value={e.cpuPercent} kind="cpu" /></td>
                    <td><Meter value={e.ramPercent} kind="ram" /></td>
                    <td><DiskHealthTag status={e.diskHealth} percent={e.diskHealthPercent} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}

      {editing && <GroupForm group={editing} onClose={() => setEditing(null)} onSaved={(g) => setSelectedId(g.id)} />}
      {members && selected && <MembersDialog group={selected} endpoints={endpoints.data ?? []} onClose={() => setMembers(false)} />}
      {running && selected && restart && (
        <RunActionDialog
          entry={restart} me={me} onClose={() => setRunning(false)}
          targets={memberRows.filter((e) => e.online).map((e) => ({ id: e.id, name: e.name, hostname: e.hostname }))}
          onCreated={(id) => { setRunning(false); nav(`/jobs/${id}?output=1`); }}
        />
      )}
      {deleting && selected && <DeleteGroup group={selected} onClose={() => { setDeleting(false); setSelectedId(null); }} onCancel={() => setDeleting(false)} />}
    </>
  );
}

function GroupForm({ group, onClose, onSaved }: { group: Partial<Group>; onClose: () => void; onSaved: (g: Group) => void }) {
  const qc = useQueryClient();
  const [name, setName] = useState(group.name ?? '');
  const [description, setDescription] = useState(group.description ?? '');
  const save = useMutation({
    mutationFn: () => group.id
      ? put<Group>(`/api/groups/${group.id}`, { name, description })
      : post<Group>('/api/groups', { name, description }),
    onSuccess: (g) => { qc.invalidateQueries({ queryKey: ['groups'] }); onSaved(g); onClose(); },
  });
  return (
    <Dialog title={group.id ? 'Rename group' : 'New group'} onClose={onClose}>
      <div className="form-row"><label htmlFor="gn">Name</label><input id="gn" value={name} maxLength={60} onChange={(e) => setName(e.target.value)} data-autofocus /></div>
      <div className="form-row"><label htmlFor="gd">Description (optional)</label><input id="gd" value={description} maxLength={300} onChange={(e) => setDescription(e.target.value)} /></div>
      <ErrorNotice error={save.error} />
      <div className="actions">
        <button onClick={onClose}>Cancel</button>
        <button className="primary" disabled={!name.trim() || save.isPending} onClick={() => save.mutate()}>Save</button>
      </div>
    </Dialog>
  );
}

function MembersDialog({ group, endpoints, onClose }: { group: Group; endpoints: EndpointSummary[]; onClose: () => void }) {
  const qc = useQueryClient();
  const [selected, setSelected] = useState<string[]>(group.memberIds);
  const [search, setSearch] = useState('');
  const t = search.trim().toLowerCase();
  const shown = endpoints.filter((e) => !t || e.name.toLowerCase().includes(t) || e.hostname.toLowerCase().includes(t));
  const save = useMutation({
    // Send only what this editor changed, so members another operator added meanwhile are not removed by a stale list.
    mutationFn: () => post<Group>(`/api/groups/${group.id}/members/changes`, {
      add: selected.filter((id) => !group.memberIds.includes(id)),
      remove: group.memberIds.filter((id) => !selected.includes(id)),
    }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['groups'] }); qc.invalidateQueries({ queryKey: ['endpoints'] }); onClose(); },
    onError: () => { qc.invalidateQueries({ queryKey: ['groups'] }); qc.invalidateQueries({ queryKey: ['endpoints'] }); },
  });
  return (
    <Dialog title={`Members of ${group.name}`} onClose={onClose}>
      <div className="toolbar">
        <input placeholder="Search" aria-label="Search workstations" value={search} onChange={(e) => setSearch(e.target.value)} data-autofocus />
        <button className="small" onClick={() => setSelected([...new Set([...selected, ...shown.map((e) => e.id)])])}>Select shown</button>
        <button className="small" onClick={() => setSelected(selected.filter((id) => !shown.some((e) => e.id === id)))}>Clear shown</button>
      </div>
      <div className="pick-list">
        {shown.map((e) => (
          <label key={e.id}>
            <input type="checkbox" checked={selected.includes(e.id)}
              onChange={(ev) => setSelected(ev.target.checked ? [...selected, e.id] : selected.filter((x) => x !== e.id))} />
            <span>{e.name} <span className="muted mono">{e.hostname}</span></span>
          </label>
        ))}
        {shown.length === 0 && <div className="empty">No workstations.</div>}
      </div>
      <p className="muted">{selected.length} selected</p>
      <ErrorNotice error={save.error} />
      <div className="actions">
        <button onClick={onClose}>Cancel</button>
        <button className="primary" disabled={save.isPending} onClick={() => save.mutate()}>Save members</button>
      </div>
    </Dialog>
  );
}

function DeleteGroup({ group, onClose, onCancel }: { group: Group; onClose: () => void; onCancel: () => void }) {
  const qc = useQueryClient();
  const remove = useMutation({
    mutationFn: () => del<void>(`/api/groups/${group.id}`),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['groups'] }); qc.invalidateQueries({ queryKey: ['endpoints'] }); onClose(); },
  });
  return (
    <ConfirmDialog
      title="Delete group" danger confirmLabel="Delete" busy={remove.isPending}
      message={<>Delete <b>{group.name}</b>? The {group.memberIds.length} workstation(s) in it are kept, only the grouping is removed.<ErrorNotice error={remove.error} /></>}
      onConfirm={() => remove.mutate()} onCancel={onCancel}
    />
  );
}
