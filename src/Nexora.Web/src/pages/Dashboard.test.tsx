// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Dashboard } from './Dashboard';
import type { Dashboard as DashboardData } from '../types';

const api = vi.hoisted(() => ({ get: vi.fn() }));
vi.mock('../api', () => ({ get: api.get, post: vi.fn(), ApiError: class extends Error {} }));
vi.mock('../charts', () => ({ RangeTabs: () => null, StatLine: () => null, TimeChart: () => null }));
vi.mock('../jobs/catalog', () => ({ useCatalogIndex: () => new Map() }));

const data = (over: Partial<DashboardData> = {}): DashboardData => ({
  total: 258, online: 240, offline: 18, cpuAlerts: 3, ramAlerts: 5, diskAlerts: 7, diskHealthAlerts: 101, alertEndpoints: [], recentActions: [], alertEndpointTotal: 0, ...over,
});

const thresholds = { cpuAlertPercent: 85, ramAlertPercent: 80, diskAlertPercent: 95, healthOkFrom: 85, healthCriticalBelow: 50 };

/** The answers of the server: thresholds, fleet history, and the dashboard itself. */
const route = (d: DashboardData | (() => DashboardData)) => async (path: string) => {
  if (path === undefined) throw new Error('get() was called without a path: ' + new Error().stack);
  if (path.startsWith('/api/thresholds')) return thresholds;
  if (path.startsWith('/api/dashboard/history')) return [];
  return typeof d === 'function' ? d() : d;
};

function show(d: DashboardData) {
  api.get.mockImplementation(route(d));
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={qc}><MemoryRouter><Dashboard /></MemoryRouter></QueryClientProvider>);
}

beforeEach(() => { api.get.mockReset(); });
afterEach(cleanup);

describe('interactive dashboard cards (N9)', () => {
  it('every card links to the Workstations list with the filter that gives its number', async () => {
    show(data());
    const link = async (label: RegExp) => (await screen.findByRole('link', { name: label })).getAttribute('href');

    expect(await link(/Offline/)).toBe('/endpoints?status=offline');
    expect(await link(/Online/)).toBe('/endpoints?status=online');
    expect(await link(/CPU alerts/)).toBe('/endpoints?alert=cpu');
    expect(await link(/RAM alerts/)).toBe('/endpoints?alert=ram');
    expect(await link(/Low disk space/)).toBe('/endpoints?alert=disk');
    expect(await link(/Drive health alerts/)).toBe('/endpoints?health=attention');
    expect(await screen.findByText('101')).toBeInTheDocument();
  });

  it('says which Settings threshold a card counts against', async () => {
    show(data());
    await waitFor(() => expect(screen.getByRole('link', { name: /CPU alerts/ })).toHaveAttribute('title', expect.stringContaining('85%')));
    expect(screen.getByRole('link', { name: /Low disk space/ })).toHaveAttribute('title', expect.stringContaining('95%'));
  });

  it('shows when it was updated and refreshes on request', async () => {
    const user = userEvent.setup();
    show(data());
    expect(await screen.findByTestId('dashboard-updated')).toHaveTextContent(/Updated \d\d\.\d\d\.\d{4} \d\d:\d\d/);
    const before = api.get.mock.calls.filter((c) => c[0] === '/api/dashboard').length;
    await user.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => expect(api.get.mock.calls.filter((c) => c[0] === '/api/dashboard').length).toBe(before + 1));
  });

  it('says when the needs-attention table is a cut: showing 50 of N, with a link to the full list', async () => {
    show(data({ alertEndpointTotal: 130 }));
    expect(await screen.findByTestId('alert-cap')).toHaveTextContent('of 130');
    expect(screen.getByRole('link', { name: /See them all/ })).toHaveAttribute('href', '/endpoints?health=attention');
  });

  it('offers a retry when the dashboard cannot be read', async () => {
    const user = userEvent.setup();
    let fail = true;
    api.get.mockImplementation(async (path: string) => {
      if (path === '/api/dashboard' && fail) throw new Error('server down');
      return route(data())(path);
    });
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={qc}><MemoryRouter><Dashboard /></MemoryRouter></QueryClientProvider>);

    const retry = await screen.findByRole('button', { name: 'Try again' });
    fail = false;
    await user.click(retry);

    expect(await screen.findByRole('link', { name: /Offline/ })).toBeInTheDocument();
  });
});
