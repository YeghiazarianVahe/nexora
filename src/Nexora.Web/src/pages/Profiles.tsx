import { useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { get } from '../api';
import { EndpointPicker } from '../components/EndpointPicker';
import { ProfilesPanel } from '../profiles/ProfilesPanel';
import type { EndpointSummary, Me } from '../types';
import { ErrorNotice, Loading } from '../ui';

/** User profiles of any workstation, the Nexora server included. Defaults to the workstation you came from, else the server. */
export function Profiles({ me }: { me: Me }) {
  const [params, setParams] = useSearchParams();
  const q = useQuery({ queryKey: ['endpoints'], queryFn: () => get<EndpointSummary[]>('/api/endpoints') });
  const endpoints = q.data ?? [];

  const requested = params.get('endpoint');
  const selected = endpoints.find((e) => e.id === requested) ?? endpoints.find((e) => e.isPinned) ?? endpoints[0];

  return (
    <>
      <h1>User profiles</h1>
      <p className="sub">The profiles stored on a workstation (System Properties &gt; Advanced &gt; User Profiles), read over WMI. The Nexora server is always in the list.</p>

      {q.isLoading && <Loading />}
      <ErrorNotice error={q.error} />
      {q.isError && <button onClick={() => q.refetch()}>Try again</button>}

      {selected && (
        <>
          <EndpointPicker endpoints={endpoints} value={selected.id} onChange={(id) => setParams({ endpoint: id })} />
          <h2 style={{ marginTop: 18 }}>
            {selected.name} <span className="tag" style={{ marginLeft: 6 }}>{selected.online ? 'Online' : 'Offline'}</span>
            {selected.isPinned && <span className="tag ok" style={{ marginLeft: 6 }}>Nexora server</span>}
          </h2>
          <ProfilesPanel key={selected.id} endpoint={{ id: selected.id, name: selected.name, hostname: selected.hostname, online: selected.online }} me={me} />
        </>
      )}
    </>
  );
}
