// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { OuCell } from '../components/EndpointParts';
import { filterableOu, ouState } from '../lib/format';
import { Endpoints } from './Endpoints';
import type { EndpointSummary, Me } from '../types';

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }));
vi.mock('../api', () => ({ get: api.get, post: api.post, apiDownload: vi.fn(), ApiError: class extends Error {} }));
vi.mock('../realtime', () => ({ useRealtime: () => ({ connection: null, state: 'connected', epoch: 1 }), useHubOn: () => undefined }));
vi.mock('@tanstack/react-virtual', () => ({
  useVirtualizer: ({ count }: { count: number }) => ({
    getTotalSize: () => count * 44, scrollToIndex: () => undefined,
    getVirtualItems: () => Array.from({ length: count }, (_, i) => ({ index: i, start: i * 44, key: i })),
  }),
}));

const pc = (name: string, over: Partial<EndpointSummary> = {}): EndpointSummary => ({
  id: `id-${name}`, hostname: name, name, reportedHostname: null, ipAddress: null, lastUser: null, osCaption: null, osVersion: null, uptimeSeconds: null,
  lastSeenUtc: null, online: true, cpuPercent: 10, ramPercent: 10, diskPercent: 10, groupIds: [], diskHealth: 'OK', lastError: null, alerts: [],
  diskHealthPercent: 95, manufacturer: null, model: null, organizationalUnit: null, ouStatus: null, ouError: null, isPinned: false,
  ssdHealthPercent: null, hddHealthPercent: null, ramTotalBytes: null, volumes: null, ...over,
} as EndpointSummary);

const fleet = [
  pc('FOUND-PC', { organizationalUnit: 'Head Office/IT', ouStatus: 'found' }),
  pc('CONTAINER-PC', { organizationalUnit: 'Computers', ouStatus: 'found' }),
  pc('FAILED-PC', { organizationalUnit: null, ouStatus: 'failed', ouError: 'LDAP timeout' }),
  pc('STALE-FAILED-PC', { organizationalUnit: 'Old/Value', ouStatus: 'failed', ouError: 'LDAP down' }),
  pc('MISSING-PC', { ouStatus: 'notFound' }),
  pc('NEW-PC', { ouStatus: null }),
];
const me = { account: 'AB\\op', displayName: 'Op', role: 'Viewer', isSuperAdmin: false, status: 'Active' } as Me;

function show(url = '/endpoints') {
  api.get.mockImplementation(async (path: string) => (path.startsWith('/api/endpoints') ? fleet : []));
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[url]}><Routes><Route path="/endpoints" element={<Endpoints me={me} />} /></Routes></MemoryRouter>
    </QueryClientProvider>,
  );
}
const rows = () => screen.queryAllByTestId('endpoint-row').map((r) => r.textContent ?? '');

beforeEach(() => { api.get.mockReset(); });
afterEach(cleanup);

describe('OuCell: the four OU states', () => {
  it('found: the OU text with the full path as tooltip and a filter action; a container name is shown as-is', async () => {
    const onFilter = vi.fn();
    const { rerender } = render(<OuCell endpoint={{ organizationalUnit: 'AB/Head Office/IT', ouStatus: 'found' }} onFilter={onFilter} />);
    const btn = screen.getByRole('button', { name: /Filter by this OU/ });
    expect(btn).toHaveTextContent('Head Office › IT');
    expect(btn).toHaveAttribute('title', 'AB/Head Office/IT');
    await userEvent.click(btn);
    expect(onFilter).toHaveBeenCalledWith('AB/Head Office/IT');
    rerender(<OuCell endpoint={{ organizationalUnit: 'Computers', ouStatus: 'found' }} onFilter={onFilter} />);
    expect(screen.getByRole('button')).toHaveTextContent('Computers');
  });

  it('failed: "Unknown (lookup failed)" with the error as tooltip, not clickable', () => {
    render(<OuCell endpoint={{ organizationalUnit: 'Old/Value', ouStatus: 'failed', ouError: 'LDAP timeout' }} onFilter={vi.fn()} />);
    const el = screen.getByText('Unknown (lookup failed)');
    expect(el).toHaveAttribute('title', 'LDAP timeout');
    expect(el).toHaveClass('ou-failed');
    expect(screen.queryByRole('button')).toBeNull();
    expect(screen.queryByText(/Old/)).toBeNull();
  });

  it('notFound: a dash with the Active Directory tooltip', () => {
    render(<OuCell endpoint={{ organizationalUnit: null, ouStatus: 'notFound' }} onFilter={vi.fn()} />);
    expect(screen.getByText('—')).toHaveAttribute('title', 'Not found in Active Directory');
    expect(screen.queryByRole('button')).toBeNull();
  });

  it('null status without an OU: a dash with the "Not checked yet" tooltip', () => {
    render(<OuCell endpoint={{ organizationalUnit: null, ouStatus: null }} />);
    expect(screen.getByText('—')).toHaveAttribute('title', 'Not checked yet');
  });

  it('without a filter handler (detail page) the found OU is plain text', () => {
    render(<OuCell endpoint={{ organizationalUnit: 'Head Office/IT', ouStatus: 'found' }} />);
    expect(screen.getByText('Head Office › IT')).toBeInTheDocument();
    expect(screen.queryByRole('button')).toBeNull();
  });
});

describe('ouState / filterableOu', () => {
  it('only found values are filterable', () => {
    expect(filterableOu({ organizationalUnit: 'Computers', ouStatus: 'found' })).toBe('Computers');
    expect(filterableOu({ organizationalUnit: 'Old/Value', ouStatus: 'failed', ouError: 'x' })).toBeNull();
    expect(filterableOu({ organizationalUnit: null, ouStatus: 'notFound' })).toBeNull();
    expect(filterableOu({ organizationalUnit: null, ouStatus: null })).toBeNull();
    expect(ouState({ organizationalUnit: null, ouStatus: 'failed', ouError: null }).kind).toBe('failed');
  });
});

describe('Endpoints list OU column and filter', () => {
  it('shows each state in the column', async () => {
    show();
    await waitFor(() => expect(rows().length).toBe(6));
    expect(screen.getAllByText('Unknown (lookup failed)')).toHaveLength(2);
    expect(screen.getAllByTitle('Not found in Active Directory')).toHaveLength(1);
    expect(screen.getAllByTitle('Not checked yet')).toHaveLength(1);
    expect(screen.getByTitle('LDAP timeout')).toBeInTheDocument();
  });

  it('the OU dropdown offers only found OUs: no failed, unknown or placeholder option', async () => {
    show();
    await waitFor(() => expect(rows().length).toBe(6));
    const options = within(screen.getByLabelText('OU')).getAllByRole('option').map((o) => o.textContent);
    expect(options).toEqual(['All OUs', 'Computers', 'Head Office/IT']);
  });

  it('filtering by an OU leaves out failed and unknown rows', async () => {
    show('/endpoints?ou=Computers');
    await waitFor(() => expect(rows().length).toBe(1));
    expect(rows()[0]).toContain('CONTAINER-PC');
  });
});
