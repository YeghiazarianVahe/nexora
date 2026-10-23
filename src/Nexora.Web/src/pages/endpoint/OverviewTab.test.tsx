// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { applyLiveToOverview } from '../../lib/overview';
import type { EndpointOverview, EndpointSummary, HardwareInventory } from '../../types';
import { OverviewTab } from './OverviewTab';

const api = vi.hoisted(() => { class ApiError extends Error {} return { get: vi.fn(), post: vi.fn(), ApiError }; });
vi.mock('../../api', () => ({ ...api }));

const GB = 1024 ** 3;
const ID = '11111111-1111-4111-8111-111111111111';
const ep = {
  id: ID, hostname: 'WKS-111', name: 'WKS-111', reportedHostname: 'WKS-111', ipAddress: '10.0.0.11', lastUser: null, osCaption: null, osVersion: null,
  uptimeSeconds: null, lastSeenUtc: new Date().toISOString(), online: true, cpuPercent: 12, ramPercent: 50, diskPercent: null, groupIds: [], diskHealth: null,
  diskHealthPercent: null, ssdHealthPercent: null, hddHealthPercent: null, ramTotalBytes: 16 * GB, volumes: null, manufacturer: null, model: null,
  organizationalUnit: null, lastError: null, alerts: [], isPinned: false,
} as unknown as EndpointSummary;

const inv = (os: Partial<NonNullable<HardwareInventory['os']>> = {}): HardwareInventory => ({
  status: 'Complete', collectedUtc: '2026-03-01T08:30:00Z', cached: false, serialsHidden: false, errors: [],
  os: { productName: 'Windows 11 Pro', edition: 'Microsoft Windows 11 Pro', version: '23H2', build: '22631.4317', architecture: '64-bit', installedUtc: '2025-01-15T10:00:00Z',
    bootTimeUtc: new Date(Date.now() - 90 * 60_000).toISOString(), fastStartupEnabled: false, uptimeBasis: 'boot', ...os },
  cpu: { manufacturer: 'Intel', model: 'Core i7-1255U', physicalCores: 10, logicalProcessors: 12, architecture: 'x64', maxClockMhz: 1700 },
  memory: { installedBytes: 16 * GB, usableBytes: 15.8 * GB, technology: 'DDR4', speedMhz: 3200, slotCount: 4, occupiedSlots: 2, emptySlots: 2, maxCapacityBytes: null, modulesReported: true,
    modules: [
      { slot: 'A1', bank: null, capacityBytes: 8 * GB, manufacturer: 'K', speedMhz: 3200, configuredSpeedMhz: 3200, partNumber: null, technology: 'DDR4' },
      { slot: 'B1', bank: null, capacityBytes: 8 * GB, manufacturer: 'K', speedMhz: 3200, configuredSpeedMhz: 3200, partNumber: null, technology: 'DDR4' }] },
  board: null, bios: null, computer: null,
});

const overview = (over: Partial<EndpointOverview> = {}): EndpointOverview => ({
  online: true, lastSeenUtc: new Date().toISOString(), collectedUtc: '2026-03-01T08:30:00Z', stale: false, unavailableReason: null, inventory: inv(),
  live: { cpuLoadPercent: 37, ramTotalBytes: 16 * GB, ramUsedBytes: 8 * GB, ramFreeBytes: 8 * GB, ramPercentUsed: 50, sampledUtc: new Date().toISOString() }, ...over,
});

let payload: unknown;
let qc: QueryClient;
function show(e: EndpointSummary = ep) {
  qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={qc}><OverviewTab e={e} /></QueryClientProvider>);
}
beforeEach(() => {
  payload = overview();
  api.get.mockImplementation(async (p: string) => {
    if (p === `/api/endpoints/${ID}/overview`) { if (payload instanceof Error) throw payload; return payload; }
    if (p === '/api/thresholds') return { cpuAlertPercent: 90, ramAlertPercent: 90, diskAlertPercent: 90, healthOkFrom: 85, healthCriticalBelow: 50 };
    return null;
  });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });

const card = (name: string) => within(screen.getByRole('region', { name }));

describe('Overview tab', () => {
  it('renders CPU, RAM and OS for an online workstation, including version, build and UBR', async () => {
    show();
    expect(await screen.findByText('Core i7-1255U')).toBeInTheDocument();
    expect(card('Processor').getByText('10')).toBeInTheDocument();
    expect(card('Processor').getByText('12')).toBeInTheDocument();
    expect(card('Processor').getByText('1.7 GHz')).toBeInTheDocument();
    expect(card('Processor').getByText('37%')).toBeInTheDocument();
    expect(card('Memory').getAllByText('16.0 GB').length).toBe(2); // total and installed
    expect(card('Memory').getByText('50%')).toBeInTheDocument();
    expect(card('Memory').getByText('4 total · 2 occupied · 2 empty')).toBeInTheDocument();
    expect(card('Operating system').getByText('23H2 / 22631.4317')).toBeInTheDocument();
    expect(card('Operating system').getByText('64-bit')).toBeInTheDocument();
    expect(card('Operating system').getByText('15.01.2025 14:00')).toBeInTheDocument(); // Asia/Yerevan is UTC+4
    expect(card('Operating system').getByText('1h 30m')).toBeInTheDocument();
    expect(screen.queryByTestId('overview-stale')).toBeNull();
    expect(screen.queryByText(/Fast Startup/)).toBeNull();
  });

  it('changes the load and RAM numbers when a live update arrives', async () => {
    show();
    expect(await screen.findByText('37%')).toBeInTheDocument();
    act(() => {
      qc.setQueryData<EndpointOverview>(['overview', ID], (old) => applyLiveToOverview(old!, { ...ep, cpuPercent: 81, ramPercent: 75 }));
    });
    expect(await screen.findByText('81%')).toBeInTheDocument();
    expect(card('Memory').getByText('75%')).toBeInTheDocument();
    expect(card('Memory').getByText('12.0 GB')).toBeInTheDocument(); // used
    expect(card('Memory').getByText('4.0 GB')).toBeInTheDocument(); // free
  });

  it('shows the offline banner and last known values when stale', async () => {
    payload = overview({ online: false, stale: true, lastSeenUtc: '2026-03-02T10:00:00Z' });
    show({ ...ep, online: false });
    const banner = await screen.findByTestId('overview-stale');
    expect(banner).toHaveTextContent('Offline - showing last known values from 01.03.2026 12:30.');
    expect(banner).toHaveTextContent('Last seen 02.03.2026 14:00.');
    expect(screen.getByText('Core i7-1255U')).toBeInTheDocument();
    expect(card('Processor').getByText('37% (last known)')).toBeInTheDocument();
    expect(card('Processor').queryByText('37%')).toBeNull();
  });

  it('says no data was collected, with the reason as plain text, when offline without inventory', async () => {
    payload = overview({ online: false, stale: true, inventory: null, live: null, collectedUtc: null, unavailableReason: '<img src=x onerror=alert(1)> WMI access denied' });
    show({ ...ep, online: false });
    expect(await screen.findByText('No data collected yet')).toBeInTheDocument();
    const reason = screen.getByTestId('overview-reason');
    expect(reason).toHaveTextContent('<img src=x onerror=alert(1)> WMI access denied');
    expect(reason.querySelector('img')).toBeNull();
    expect(screen.getByTestId('overview-stale')).toHaveTextContent('no data collected yet');
  });

  it('shows the Fast Startup caveat only when the uptime basis is the last full boot', async () => {
    payload = overview({ inventory: inv({ fastStartupEnabled: true, uptimeBasis: 'fullBoot' }) });
    show();
    await screen.findByText('Core i7-1255U');
    expect(card('Operating system').getAllByText(/Fast Startup is on - last full boot/).length).toBeGreaterThan(0);
  });

  it('keeps the page up and shows an error when the overview request fails', async () => {
    payload = new Error('overview exploded');
    show();
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('overview exploded'));
    expect(screen.getByRole('region', { name: 'Processor' })).toBeInTheDocument();
  });

  it('formats numbers with the fixed locale helpers (JSON numbers, never parsed strings)', async () => {
    payload = overview({ inventory: inv({ build: '26100.1234' }), live: { cpuLoadPercent: 12.5, ramTotalBytes: 8 * GB, ramUsedBytes: 4.5 * GB, ramFreeBytes: 3.5 * GB, ramPercentUsed: 56.25, sampledUtc: new Date().toISOString() } });
    show();
    expect(await screen.findByText('4.5 GB')).toBeInTheDocument();
    expect(screen.queryByText(/,\d/)).toBeNull();
  });
});

describe('applyLiveToOverview', () => {
  it('leaves the inventory untouched and derives used and free from the percentage', () => {
    const o = overview();
    const n = applyLiveToOverview(o, { ...ep, cpuPercent: 5, ramPercent: 25, online: true });
    expect(n.inventory).toBe(o.inventory);
    expect(n.live).toMatchObject({ cpuLoadPercent: 5, ramUsedBytes: 4 * GB, ramFreeBytes: 12 * GB });
  });
});
