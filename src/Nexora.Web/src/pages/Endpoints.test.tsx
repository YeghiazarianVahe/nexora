// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
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
  diskHealthPercent: 95, manufacturer: null, model: null, organizationalUnit: null, isPinned: false, ssdHealthPercent: null, hddHealthPercent: null,
  ramTotalBytes: null, volumes: null, ...over,
} as EndpointSummary);

const fleet = [
  pc('ONLINE-OK'),
  pc('OFFLINE-BAD-DRIVE', { online: false, diskHealth: 'Critical', alerts: [] }),                // offline: no alert
  pc('ONLINE-BAD-DRIVE', { diskHealth: 'Critical', alerts: ['diskhealth'] }),
  pc('HOT-CPU', { cpuPercent: 99, alerts: ['cpu'] }),
];
const me = { account: 'CONTOSO\demo', displayName: 'Demo', role: 'Viewer', isSuperAdmin: false, status: 'Active' } as Me;

function show(url: string) {
  api.get.mockImplementation(async (path: string) => (path.startsWith('/api/endpoints') ? fleet : []));
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[url]}>
        <Routes>
          <Route path="/endpoints" element={<Endpoints me={me} />} />
          <Route path="/endpoints/:id" element={<p data-testid="detail-page">detail page</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}
const names = () => screen.queryAllByTestId('endpoint-row').map((r) => r.textContent ?? '');

beforeEach(() => { api.get.mockReset(); });
afterEach(cleanup);

describe('the list opened from a dashboard card shows exactly what the card counted (N9, N5)', () => {
  it('?health=attention lists only the ONLINE PCs with a bad drive: the dashboard drive-health number', async () => {
    show('/endpoints?health=attention');
    await waitFor(() => expect(names().length).toBe(1));
    expect(names()[0]).toContain('ONLINE-BAD-DRIVE');
  });

  it('?status=offline and ?alert=cpu', async () => {
    show('/endpoints?status=offline');
    await waitFor(() => expect(names().length).toBe(1));
    expect(names()[0]).toContain('OFFLINE-BAD-DRIVE');
    cleanup();
    show('/endpoints?alert=cpu');
    await waitFor(() => expect(names().length).toBe(1));
    expect(names()[0]).toContain('HOT-CPU');
  });

  it('without a filter everything is listed, and an unknown value in the address is ignored', async () => {
    show('/endpoints?status=bogus&alert=nonsense');
    await waitFor(() => expect(names().length).toBe(4));
  });

  it('Clear filters returns to the full list', async () => {
    const user = userEvent.setup();
    show('/endpoints?alert=cpu');
    await waitFor(() => expect(names().length).toBe(1));
    await user.click(screen.getByRole('button', { name: 'Clear filters' }));
    await waitFor(() => expect(names().length).toBe(4));
  });
});

describe('Enter on a row (M12)', () => {
  it('opens the PC when the row itself has the focus', async () => {
    const user = userEvent.setup();
    show('/endpoints?alert=cpu');
    const row = await screen.findByTestId('endpoint-row');
    row.focus();
    await user.keyboard('{Enter}');
    expect(await screen.findByTestId('detail-page')).toBeInTheDocument();
  });

  it('does NOT open the PC when Enter is pressed on a control inside the row', async () => {
    const user = userEvent.setup();
    show('/endpoints?alert=cpu');
    const row = await screen.findByTestId('endpoint-row');
    const inner = row.querySelector<HTMLElement>('input, a, button');
    expect(inner).not.toBeNull();
    inner!.focus();
    await user.keyboard('{Enter}');
    expect(screen.queryByTestId('detail-page')).toBeNull();
  });
});
