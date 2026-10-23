import { useId, useMemo, useState } from 'react';
import type { EndpointSummary } from '../types';

const MAX_SHOWN = 200;

/**
 * Pick any workstation: a search box over a native list (fully keyboard accessible).
 * Pinned endpoints (the Nexora server) are always offered at the top, whatever the search says, so they can never be filtered out.
 */
export function EndpointPicker({ endpoints, value, onChange, label = 'Workstation' }: {
  endpoints: EndpointSummary[]; value: string; onChange: (id: string) => void; label?: string;
}) {
  const [search, setSearch] = useState('');
  const id = useId();

  const { pinned, matches, more } = useMemo(() => {
    const t = search.trim().toLowerCase();
    const pinned = endpoints.filter((e) => e.isPinned);
    const rest = endpoints.filter((e) => !e.isPinned && (!t || [e.name, e.hostname, e.ipAddress, e.lastUser, e.organizationalUnit].some((v) => v?.toLowerCase().includes(t))));
    // Whatever is selected stays in the list, even if the search no longer matches it.
    const selected = endpoints.find((e) => e.id === value && !e.isPinned);
    const shown = rest.slice(0, MAX_SHOWN);
    if (selected && !shown.some((e) => e.id === selected.id)) shown.unshift(selected);
    return { pinned, matches: shown, more: Math.max(0, rest.length - MAX_SHOWN) };
  }, [endpoints, search, value]);

  return (
    <div className="picker">
      <label htmlFor={`${id}-q`}>{label}</label>
      <div className="picker-row">
        <input id={`${id}-q`} type="search" placeholder="Search name, address, user..." value={search} onChange={(e) => setSearch(e.target.value)} autoComplete="off" />
        <select aria-label={`${label} list`} value={value} onChange={(e) => onChange(e.target.value)}>
          {pinned.length > 0 && (
            <optgroup label="Nexora server">
              {pinned.map((e) => <option key={e.id} value={e.id}>{e.name} {e.online ? '' : '(offline)'}</option>)}
            </optgroup>
          )}
          <optgroup label={search.trim() ? `Matching workstations (${matches.length}${more ? '+' : ''})` : 'Workstations'}>
            {matches.map((e) => <option key={e.id} value={e.id}>{e.name}{e.name !== e.hostname ? ` · ${e.hostname}` : ''}{e.online ? '' : ' (offline)'}</option>)}
          </optgroup>
        </select>
      </div>
      {more > 0 && <small className="muted">{more} more match. Refine the search to narrow the list.</small>}
    </div>
  );
}
