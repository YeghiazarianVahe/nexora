// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Me } from '../types';
import { RunActionDialog, runsImmediately, type TargetRef } from './RunActionDialog';
import type { CatalogEntry } from './jobTypes';
import { auditActionName } from '../lib/auditLabel';

const api = vi.hoisted(() => ({ post: vi.fn() }));
vi.mock('../api', () => ({ post: api.post, get: vi.fn(), ApiError: class extends Error {} }));

const entry = (over: Partial<CatalogEntry>): CatalogEntry => ({
  code: 'x.y', displayName: 'Do a thing', category: 'SystemRepair', risk: 'Medium', minRole: 'Operator', method: 'Wmi', parameters: [],
  timeoutSeconds: 300, description: 'Does a thing to the PC', command: null, supportsCancel: true, supportsBulk: true, requiresUserNotice: false,
  needsConfirmation: true, needsReason: false, needsTypedConfirmation: false, heavy: false, isPower: false, checksProtected: false, implemented: true, allowed: true,
  ...over,
});

const me = (role: Me['role']): Me => ({ account: 'CONTOSO\\demo', displayName: 'Demo', role, isSuperAdmin: false, status: 'Active' } as Me);
const one: TargetRef[] = [{ id: 'e1', name: 'Cashier 3', hostname: 'wks-pc-0103' }];
const many: TargetRef[] = [1, 2, 3].map((i) => ({ id: `e${i}`, name: `PC ${i}`, hostname: `wks-pc-${i}` }));

function show(e: CatalogEntry, targets: TargetRef[], role: Me['role'] = 'Operator') {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onCreated = vi.fn();
  const onClose = vi.fn();
  render(<QueryClientProvider client={qc}><RunActionDialog entry={e} targets={targets} me={me(role)} onClose={onClose} onCreated={onCreated} /></QueryClientProvider>);
  return { onCreated, onClose };
}

const created = { job: { id: 'job-1' }, targets: [] };

beforeEach(() => {
  api.post.mockReset();
  api.post.mockImplementation(async (path: string) => (path.endsWith('/confirm') ? { token: 'tok' } : path.endsWith('/preflight') ? preflight : created));
});
afterEach(cleanup);

let preflight = { total: 3, reachable: 3, offline: [] as string[], protected: [] as { name: string; reason: string }[], busy: [] as string[], overrideNeeded: false };

describe('confirmation by risk', () => {
  it('Low with nothing to fill in runs immediately, without a dialog or confirm token', async () => {
    const { onCreated } = show(entry({ risk: 'Low', needsConfirmation: false }), one);
    await waitFor(() => expect(onCreated).toHaveBeenCalledWith('job-1'));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(api.post).toHaveBeenCalledTimes(1);
    expect(api.post.mock.calls[0][0]).toBe('/api/jobs');
  });

  it('Low with a required parameter still asks for it first', () => {
    const e = entry({ risk: 'Low', needsConfirmation: false, parameters: [{ name: 'message', type: 'String', label: 'Message', required: true, min: null, max: null, maxLength: 255, allowed: null, default: null, help: null }] });
    expect(runsImmediately(e)).toBe(false);
  });

  it('Medium shows the friendly name, command, host, runs-as line and effect, and needs only a click on Run', async () => {
    const user = userEvent.setup();
    const { onCreated } = show(entry({ displayName: 'Repair system files', command: 'sfc /scannow', description: 'Scans and repairs Windows system files' }), one);

    const dialog = screen.getByRole('dialog', { name: 'Repair system files' });
    expect(dialog).toHaveTextContent('sfc /scannow');
    expect(dialog).toHaveTextContent('Scans and repairs Windows system files');
    expect(dialog).toHaveTextContent('wks-pc-0103');
    expect(dialog).toHaveTextContent('runs as CONTOSO\\demo');
    const run = screen.getByRole('button', { name: 'Run' });
    expect(run).toBeEnabled();

    await user.click(run);
    await waitFor(() => expect(onCreated).toHaveBeenCalledWith('job-1'));
    expect(api.post.mock.calls.map((c) => c[0])).toEqual(['/api/jobs/confirm', '/api/jobs']);
    expect(api.post.mock.calls[1][1]).toMatchObject({ confirmToken: 'tok' });
  });

  it('High needs a reason and the typed hostname, and Enter submits only when both are right', async () => {
    const user = userEvent.setup();
    const { onCreated } = show(entry({ risk: 'High', needsReason: true, needsTypedConfirmation: true, displayName: 'Restart computer', command: 'shutdown /r' }), one);

    const run = screen.getByRole('button', { name: 'Run' });
    expect(run).toBeDisabled();

    await user.type(screen.getByLabelText(/Reason/), 'user asked');
    expect(run).toBeDisabled();

    const typed = screen.getByLabelText(/Type the hostname/);
    await user.type(typed, 'cashier 3');
    expect(run).toBeDisabled();
    await user.keyboard('{Enter}');
    expect(api.post).not.toHaveBeenCalled();

    await user.clear(typed);
    await user.type(typed, 'WKS-PC-0103');
    expect(run).toBeEnabled();
    await user.keyboard('{Enter}');
    await waitFor(() => expect(onCreated).toHaveBeenCalled());
    expect(api.post.mock.calls[0][1]).toMatchObject({ reason: 'user asked', typed: 'wks-pc-0103' });
  });

  it('Critical adds the protected-host override, which needs a reason', async () => {
    const user = userEvent.setup();
    show(entry({ risk: 'Critical', needsReason: true, needsTypedConfirmation: true, checksProtected: true, minRole: 'Admin' }), one, 'Admin');

    await user.type(screen.getByLabelText(/Type the hostname/), 'wks-pc-0103');
    await user.type(screen.getByLabelText(/Reason/), 'ticket 42');
    expect(screen.getByRole('button', { name: 'Run' })).toBeEnabled();
    await user.click(screen.getByLabelText(/Override protected workstations/));
    expect(screen.getByRole('button', { name: 'Run' })).toBeEnabled();
    await user.clear(screen.getByLabelText(/Reason/));
    expect(screen.getByRole('button', { name: 'Run' })).toBeDisabled();
  });

  it('an Operator is never offered the protected override', () => {
    show(entry({ risk: 'High', needsReason: true, needsTypedConfirmation: true, checksProtected: true }), one, 'Operator');
    expect(screen.queryByLabelText(/Override protected workstations/)).toBeNull();
  });

  it('Cancel closes without calling the server', async () => {
    const user = userEvent.setup();
    const { onClose } = show(entry({}), one);
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalled();
    expect(api.post).not.toHaveBeenCalled();
  });
});

describe('bulk runs', () => {
  it('shows the pre-flight and asks for the target count', async () => {
    preflight = { total: 3, reachable: 2, offline: ['PC 3'], protected: [], busy: [], overrideNeeded: false };
    const user = userEvent.setup();
    show(entry({ risk: 'High', needsReason: true, needsTypedConfirmation: true }), many);

    expect(await screen.findByText('2 of 3 reachable')).toBeInTheDocument();
    expect(screen.getByText(/1 offline: PC 3/)).toBeInTheDocument();
    expect(api.post.mock.calls[0]).toEqual(['/api/jobs/preflight', { actionCode: 'x.y', endpointIds: ['e1', 'e2', 'e3'] }]);

    await user.type(screen.getByLabelText(/Reason/), 'maintenance');
    await user.type(screen.getByLabelText(/Type the number of workstations/), '3');
    expect(screen.getByRole('button', { name: 'Run' })).toBeEnabled();
  });

  it('Run stays disabled while protected workstations are in the set and no Admin override is given', async () => {
    preflight = { total: 3, reachable: 3, offline: [], protected: [{ name: 'PC 1', reason: 'Nexora server' }], busy: [], overrideNeeded: true };
    const user = userEvent.setup();
    show(entry({ risk: 'Medium', checksProtected: true }), many, 'Operator');

    expect(await screen.findByText(/1 protected: PC 1/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Run' })).toBeDisabled();
    void user;
  });

  it('Run waits for the pre-flight and stays off if it fails', async () => {
    api.post.mockImplementation(async (path: string) => { if (path.endsWith('/preflight')) throw new Error('boom'); return created; });
    show(entry({}), many);
    expect(await screen.findByRole('button', { name: 'Try again' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Run' })).toBeDisabled();
  });
});

describe('friendly names in the audit log', () => {
  const index = new Map([['repair.sfc', entry({ code: 'repair.sfc', displayName: 'Repair system files' })]]);
  it('maps job.create and job.target entries to the friendly name', () => {
    expect(auditActionName({ action: 'job.create', newValue: 'repair.sfc', details: null }, index)).toBe('Repair system files');
    expect(auditActionName({ action: 'job.target', newValue: 'Succeeded', details: JSON.stringify({ action: 'repair.sfc' }) }, index)).toBe('Repair system files');
    expect(auditActionName({ action: 'role.change', newValue: 'repair.sfc', details: null }, index)).toBeNull();
    expect(auditActionName({ action: 'job.create', newValue: 'unknown.code', details: null }, index)).toBeNull();
  });
});
