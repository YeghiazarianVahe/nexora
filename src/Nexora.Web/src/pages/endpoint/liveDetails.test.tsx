// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { EndpointSummary, Me } from '../../types';
import { HardwareTab } from './HardwareTab';
import { LogsTab } from './LogsTab';
import { LiveProcesses } from './MonitoringTab';

const api = vi.hoisted(() => {
  class ApiError extends Error { constructor(public status: number, message: string) { super(message); } }
  return { get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn(), ApiError };
});
vi.mock('../../api', () => ({ ...api }));
vi.mock('../../realtime', () => ({ useRealtime: () => ({ connection: null, epoch: 0 }), useHubOn: () => undefined }));
vi.mock('../../toasts', () => ({ useToasts: () => ({ push: vi.fn() }) }));
class ResizeObserverStub { observe() {} unobserve() {} disconnect() {} }
(globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= ResizeObserverStub;

const me = (role: Me['role']) => ({ account: 'AB\\op', displayName: 'Op', role, isSuperAdmin: false, status: 'Active' } as Me);
const ep = { id: 'e1', hostname: 'WKS-111', name: 'WKS-111', online: true, lastSeenUtc: new Date().toISOString() } as EndpointSummary;
const detail = { services: [], processes: [], disks: [], software: [{ name: 'Seven Zip', version: '1', publisher: 'x' }], errors: [], warnings: [] };
const detailCalls = () => api.get.mock.calls.filter((c) => String(c[0]).endsWith('/detail'));

beforeEach(() => {
  vi.resetAllMocks();
  api.get.mockImplementation(async (path: string) => {
    if (path.endsWith('/detail')) return detail;
    if (path.startsWith('/api/audit') || path.startsWith('/api/jobs?')) return { items: [], total: 0, page: 1, pageSize: 50 };
    if (path.includes('/metrics')) return [];
    if (path === '/api/thresholds') return { cpuAlertPercent: 90, ramAlertPercent: 90, diskAlertPercent: 90 };
    if (path.includes('/hardware')) return { status: 'Unavailable', errors: [], cached: false, serialsHidden: false };
    return [];
  });
});
afterEach(cleanup);

function mount(ui: ReactNode) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<MemoryRouter><QueryClientProvider client={qc}>{ui}</QueryClientProvider></MemoryRouter>);
}

const cases: [string, (role: Me['role']) => ReactNode, string][] = [
  ['Processes (MonitoringTab)', (r) => <LiveProcesses e={ep} me={me(r)} />, 'Read from workstation'],
  ['Installed software (HardwareTab)', (r) => <HardwareTab e={ep} me={me(r)} />, 'Load list'],
  ['Event log (LogsTab)', (r) => <LogsTab e={ep} me={me(r)} />, 'Read from workstation'],
];

describe.each(cases)('%s', (_name, ui, openLabel) => {
  it('Viewer: friendly notice, no button, and /detail is never requested', async () => {
    mount(ui('Viewer'));
    expect(await screen.findByTestId('operator-required')).toHaveTextContent('Operator role required for live details');
    expect(screen.queryByRole('button', { name: openLabel })).toBeNull();
    expect(detailCalls()).toHaveLength(0);
  });

  it('Operator: no notice, the button loads /detail', async () => {
    mount(ui('Operator'));
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: openLabel }));
    await vi.waitFor(() => expect(detailCalls()).toHaveLength(1));
    expect(screen.queryByTestId('operator-required')).toBeNull();
  });

  it('Operator whose request still gets 403 sees the same notice, not a raw error', async () => {
    api.get.mockImplementation(async (path: string) => {
      if (path.endsWith('/detail')) throw new api.ApiError(403, 'Forbidden');
      if (path.startsWith('/api/audit') || path.startsWith('/api/jobs?')) return { items: [], total: 0, page: 1, pageSize: 50 };
      return path.includes('/hardware') ? { status: 'Unavailable', errors: [], cached: false, serialsHidden: false } : [];
    });
    mount(ui('Operator'));
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: openLabel }));
    expect(await screen.findByTestId('operator-required')).toBeInTheDocument();
    expect(screen.queryByText('Forbidden')).toBeNull();
  });
});
