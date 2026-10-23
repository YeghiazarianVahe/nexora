// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Me } from '../types';
import { RunActionDialog } from './RunActionDialog';
import type { CatalogEntry } from './jobTypes';

const api = vi.hoisted(() => {
  class ApiError extends Error { constructor(public status: number, message: string) { super(message); } }
  return { get: vi.fn(), post: vi.fn(), ApiError };
});
vi.mock('../api', () => ({ ...api }));
vi.mock('../toasts', () => ({ useToasts: () => ({ push: vi.fn() }) }));

const param = (name: string, type: 'String' | 'Bool', label: string) => ({ name, type, label, required: type === 'String', min: null, max: null, maxLength: 256, allowed: null, default: null, help: null });
const entry = (code: string): CatalogEntry => ({
  code, displayName: code, category: 'services', risk: 'Medium', minRole: 'Operator', method: 'Wmi',
  parameters: [param('serviceName', 'String', 'Service name'), param('stopDependents', 'Bool', 'Stop dependents')],
  timeoutSeconds: 60, description: 'desc', command: null, supportsCancel: false, supportsBulk: false, requiresUserNotice: false,
  needsConfirmation: true, needsReason: false, needsTypedConfirmation: false, heavy: false, isPower: false, checksProtected: false, implemented: true, allowed: true,
});
const me = { account: 'AB\\op', displayName: 'Op', role: 'Operator', isSuperAdmin: false, status: 'Active' } as Me;
const target = [{ id: 'e1', name: 'PC', hostname: 'wks-pc-1' }];

function show(code: string) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={qc}><RunActionDialog entry={entry(code)} me={me} targets={target} onClose={() => undefined} onCreated={() => undefined} /></QueryClientProvider>);
}
const depCalls = () => api.get.mock.calls.filter((c) => String(c[0]).includes('/dependents'));

beforeEach(() => {
  vi.resetAllMocks();
  api.get.mockResolvedValue({ running: [], all: [], anyProtected: false });
});
afterEach(() => { cleanup(); vi.useRealTimers(); });

describe('hand-typed service name', () => {
  it('looks dependents up once, 400 ms after typing stops, and never for an invalid name', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: (ms) => vi.advanceTimersByTime(ms) });
    show('svc.stop');
    const input = await screen.findByLabelText(/Service name/);
    await user.type(input, 'Spooler');
    expect(depCalls()).toHaveLength(0);                       // still inside the debounce window
    expect(screen.getByRole('button', { name: 'Run' })).toBeDisabled();
    await act(async () => { await vi.advanceTimersByTimeAsync(450); });
    expect(await screen.findByText(/No running services depend/)).toBeInTheDocument();
    expect(depCalls()).toHaveLength(1);
    expect(depCalls()[0][0]).toBe('/api/endpoints/e1/services/Spooler/dependents');

    await user.clear(input);
    await user.type(input, 'bad/name?');
    await act(async () => { await vi.advanceTimersByTimeAsync(600); });
    expect(depCalls()).toHaveLength(1);                       // invalid name: no request
    expect(screen.getByText(/Enter a valid service name/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Run' })).toBeDisabled();
  });
});

describe('refused start', () => {
  it('shows the server 400 denial from confirm as text in the dialog', async () => {
    const user = userEvent.setup();
    api.post.mockRejectedValue(new api.ApiError(400, 'Service RemoteRegistry cannot be started from Nexora. <b>denied</b>'));
    show('svc.start');
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText(/Service name/), 'RemoteRegistry');
    await user.click(within(dialog).getByRole('button', { name: 'Run' }));
    const alert = await within(dialog).findByRole('alert');
    expect(alert).toHaveTextContent('Service RemoteRegistry cannot be started from Nexora. <b>denied</b>');
    expect(alert.querySelector('b')).toBeNull();
    expect(dialog).toBeInTheDocument();
  });
});
