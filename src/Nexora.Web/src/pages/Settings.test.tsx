// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { Me } from '../types';
import { Settings } from './Settings';

const api = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn(), post: vi.fn(), del: vi.fn() }));
vi.mock('../api', () => ({ ...api, ApiError: class extends Error {} }));
const me = (role: Me['role']) => ({ account: 'AB\\a', displayName: 'A', role, isSuperAdmin: false, status: 'Active' } as Me);
const settings = {
  pollIntervalSeconds: 60, maxConcurrency: 32, cpuAlertPercent: 90, ramAlertPercent: 90, diskAlertPercent: 90, offlineAfterFailures: 3,
  metricRetentionDays: 30, diskPollMinutes: 60, protectedServices: ['Custom*'], protectedServicesFloor: ['RpcSs', 'WinDefend'],
};
beforeEach(() => {
  vi.resetAllMocks();
  api.get.mockImplementation(async (p: string) => (p === '/api/settings' ? { settings, features: { credentialOps: false } } : []));
  api.put.mockResolvedValue(settings);
});
afterEach(cleanup);
const mount = (role: Me['role']) => {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={qc}><Settings me={me(role)} /></QueryClientProvider>);
};

it('shows a failed first load instead of an endless spinner', async () => {
  api.get.mockRejectedValue(new Error('Settings unavailable'));
  mount('Admin');
  expect(await screen.findByRole('alert')).toHaveTextContent('Settings unavailable');
  expect(screen.queryByText('Loading...')).not.toBeInTheDocument();
});

it('Admin sees the read-only floor and the editable list; Operator and Viewer see neither', async () => {
  mount('Admin');
  expect(await screen.findByRole('heading', { name: 'Protected services' })).toBeInTheDocument();
  expect(within(screen.getByLabelText('Built-in protected services')).getByText('WinDefend')).toBeInTheDocument();
  expect(within(screen.getByLabelText('Built-in protected services')).queryByRole('button')).toBeNull();
  expect(within(screen.getByLabelText('Added protected services')).getByText('Custom*')).toBeInTheDocument();
  for (const role of ['Operator', 'Viewer'] as const) {
    cleanup();
    mount(role);
    await screen.findByRole('heading', { name: 'Settings' });
    expect(screen.queryByRole('heading', { name: 'Protected services' })).toBeNull();
  }
});

const thresholds = {
  diskTempHddWarnC: 45, diskTempHddCritC: 55, diskTempSsdWarnC: 60, diskTempSsdCritC: 70, diskTempNvmeWarnC: 65, diskTempNvmeCritC: 75,
};
const withThresholds = () => api.get.mockImplementation(async (p: string) => (p === '/api/settings' ? { settings, features: { credentialOps: false } } : p === '/api/thresholds' ? thresholds : []));

it('disk temperature limits: Admin only, prefilled, and the PUT always carries the six fields', async () => {
  withThresholds();
  const user = userEvent.setup();
  mount('Admin');
  expect(await screen.findByRole('heading', { name: 'Disk temperature limits' })).toBeInTheDocument();
  expect(screen.getByText(/NVMe drives that report their own limits use those/)).toBeInTheDocument();
  await vi.waitFor(() => expect(screen.getByLabelText('HDD warning (°C)')).toHaveValue('45'));
  await user.click(screen.getByRole('button', { name: 'Save settings' }));
  expect(api.put).toHaveBeenCalledWith('/api/settings', expect.objectContaining(thresholds));
  for (const role of ['Operator', 'Viewer'] as const) {
    cleanup();
    mount(role);
    await screen.findByRole('heading', { name: 'Settings' });
    expect(screen.queryByRole('heading', { name: 'Disk temperature limits' })).toBeNull();
  }
});

it('disk temperature limits: empty box is editable, range and warn < crit errors block saving', async () => {
  withThresholds();
  const user = userEvent.setup();
  mount('Admin');
  const warn = await screen.findByLabelText('SSD warning (°C)');
  await vi.waitFor(() => expect(warn).toHaveValue('60'));
  await user.clear(warn);
  expect(warn).toHaveValue('');
  expect(screen.getByText('Required (20-100)')).toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Save settings' })).toBeDisabled();
  await user.type(warn, '10');
  expect(screen.getByText('Whole number from 20 to 100')).toBeInTheDocument();
  await user.clear(warn);
  await user.type(warn, '75');
  expect(screen.getByText('Critical must be higher than warning')).toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Save settings' })).toBeDisabled();
  await user.clear(warn);
  await user.type(warn, '65');
  expect(screen.getByRole('button', { name: 'Save settings' })).toBeEnabled();
});

it('validates names, rejects duplicates of the floor, adds and removes, and saves through the settings call', async () => {
  const user = userEvent.setup();
  mount('Admin');
  const input = await screen.findByLabelText('Service name');
  const add = screen.getByRole('button', { name: 'Add' });
  await user.click(add);
  expect(screen.getByText('Enter a service name')).toBeInTheDocument();
  await user.type(input, 'bad name!');
  await user.click(add);
  expect(screen.getByText(/Use letters, digits/)).toBeInTheDocument();
  await user.clear(input);
  await user.type(input, 'rpcss');
  await user.click(add);
  expect(screen.getByText('Already in the list')).toBeInTheDocument();
  await user.clear(input);
  await user.type(input, 'Cust*');
  await user.click(add);
  expect(screen.getAllByText(/Use letters, digits, spaces/).length).toBe(1); // no wildcard
  await user.clear(input); // the box can be emptied and retyped (text state)
  await user.type(input, 'My Agent');
  await user.click(add);
  expect(within(screen.getByLabelText('Added protected services')).getByText('My Agent')).toBeInTheDocument();
  await user.click(screen.getByRole('button', { name: 'Remove Custom*' }));
  await user.click(screen.getByRole('button', { name: 'Save settings' }));
  expect(api.put).toHaveBeenCalledWith('/api/settings', expect.objectContaining({ protectedServices: ['My Agent'], pollIntervalSeconds: 60 }));
});
