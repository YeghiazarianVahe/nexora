// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { DiskHealth, EndpointSummary } from '../../types';
import { OverviewSignals } from './OverviewSignals';

const api = vi.hoisted(() => ({ get: vi.fn() }));
vi.mock('../../api', () => ({ ...api, ApiError: class extends Error {} }));
const endpoint = { id: 'fixture', online: true, diskHealth: 'Warning', diskHealthPercent: 80,
  volumes: [{ letter: 'C:', totalBytes: 100 * 1024 ** 3, freeBytes: 25 * 1024 ** 3 }] } as EndpointSummary;
const disk = { diskKey: 'one', type: 'SSD', temperatureC: 42, updatedUtc: new Date().toISOString() } as DiskHealth;
function mount(disks: DiskHealth[], e = endpoint) {
  api.get.mockImplementation(async (url: string) => url.includes('/metrics') ? [{ timeUtc: new Date().toISOString(), cpu: 30, ram: 50 }] : disks);
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><OverviewSignals e={e} /></QueryClientProvider>);
}
beforeEach(() => vi.resetAllMocks());
afterEach(cleanup);
it('shows CPU/RAM history and separates volume capacity from estimated physical health', async () => {
  mount([disk]);
  expect(await screen.findByText('42 °C measured')).toBeInTheDocument();
  expect(screen.getByText('CPU usage')).toBeInTheDocument();
  expect(screen.getByText('RAM usage')).toBeInTheDocument();
  expect(screen.getByText('Warning · 80% estimated')).toBeInTheDocument();
  expect(screen.getByText('75.0 GB used of 100.0 GB')).toBeInTheDocument();
  expect(api.get.mock.calls.every(([url]) => !String(url).includes('/refresh'))).toBe(true);
});
it('does not guess which of multiple SSDs is primary', async () => {
  mount([disk, { ...disk, diskKey: 'two' }]);
  expect(await screen.findByText(/Multiple SSDs reported/)).toBeInTheDocument();
  expect(screen.queryByText('42 °C measured')).toBeNull();
});
it.each([true, false])('does not show an old or offline temperature as current (online=%s)', async online => {
  mount([{ ...disk, updatedUtc: new Date(Date.now() - 60 * 60_000).toISOString() }], { ...endpoint, online });
  await waitFor(() => expect(api.get).toHaveBeenCalledTimes(2));
  expect(screen.queryByText('42 °C measured')).toBeNull();
});
it('keeps cards usable when stored history fails to load', async () => {
  api.get.mockRejectedValue(new Error('Stored metrics unavailable'));
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><OverviewSignals e={endpoint} /></QueryClientProvider>);
  expect((await screen.findAllByRole('alert'))[0]).toHaveTextContent('Stored metrics unavailable');
  expect(screen.getByText('Storage summary')).toBeInTheDocument();
});
