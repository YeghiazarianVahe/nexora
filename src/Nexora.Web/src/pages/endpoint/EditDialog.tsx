import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { put } from '../../api';
import type { EndpointSummary } from '../../types';
import { Dialog, ErrorNotice } from '../../ui';
import { GroupPicker, useGroups } from '../Endpoints';

export function EditDialog({ endpoint, onClose }: { endpoint: EndpointSummary; onClose: () => void }) {
  const qc = useQueryClient();
  const groups = useGroups();
  const [name, setName] = useState(endpoint.name === endpoint.hostname ? '' : endpoint.name);
  const [address, setAddress] = useState(endpoint.hostname);
  const [selected, setSelected] = useState<string[]>(endpoint.groupIds);
  const save = useMutation({
    mutationFn: () => put<EndpointSummary>(`/api/endpoints/${endpoint.id}`, { name, address, groupIds: selected }),
    onSuccess: () => { qc.invalidateQueries(); onClose(); },
  });
  return (
    <Dialog title="Edit workstation" onClose={onClose}>
      <div className="inline-form">
        <div className="form-row"><label htmlFor="en">Workstation name</label><input id="en" value={name} maxLength={100} onChange={(e) => setName(e.target.value)} /></div>
        <div className="form-row"><label htmlFor="ea">IP address or host name</label><input id="ea" value={address} onChange={(e) => setAddress(e.target.value)} /></div>
      </div>
      <div className="form-row"><span className="label">Groups</span><GroupPicker groups={groups.data ?? []} selected={selected} onChange={setSelected} /></div>
      <ErrorNotice error={save.error} />
      <div className="actions">
        <button onClick={onClose}>Cancel</button>
        <button className="primary" disabled={!address.trim() || save.isPending} onClick={() => save.mutate()}>Save</button>
      </div>
    </Dialog>
  );
}
