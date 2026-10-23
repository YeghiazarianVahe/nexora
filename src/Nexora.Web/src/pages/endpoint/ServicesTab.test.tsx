// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { CatalogEntry } from '../../jobs/jobTypes';
import type { EndpointSummary, Me, ServiceAction } from '../../types';
import { RunActionDialog } from '../../jobs/RunActionDialog';
import { LiveProcesses } from './MonitoringTab';
import { ServicesTab } from './ServicesTab';
import { StartedJobContext } from './startedJob';

const api = vi.hoisted(() => {
  class ApiError extends Error { constructor(public status: number, message: string) { super(message); } }
  return { get: vi.fn(), post: vi.fn(), ApiError };
});
vi.mock('../../api', () => ({ ...api }));
vi.mock('../../toasts', () => ({ useToasts: () => ({ push: vi.fn() }) }));
class ResizeObserverStub { observe() {} unobserve() {} disconnect() {} }
(globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= ResizeObserverStub;

const E1 = 'e1';
const me = (role: Me['role']) => ({ account: 'AB\\op', displayName: 'Op', role, isSuperAdmin: false, status: 'Active' } as Me);
const ep = (over: Partial<EndpointSummary> = {}) => ({ id: E1, hostname: 'WKS-111', name: 'WKS-111', online: true, lastSeenUtc: new Date().toISOString(), ...over } as EndpointSummary);

const param = (name: string, type: 'String' | 'Bool', label: string) => ({ name, type, label, required: type === 'String', min: null, max: null, maxLength: 256, allowed: null, default: null, help: null });
const entry = (code: string, displayName: string, risk: CatalogEntry['risk'], over: Partial<CatalogEntry> = {}): CatalogEntry => ({
  code, displayName, category: 'services', risk, minRole: 'Operator', method: 'Wmi', parameters: [param('serviceName', 'String', 'Service name'), param('stopDependents', 'Bool', 'Stop dependents')],
  timeoutSeconds: 60, description: 'desc', command: null, supportsCancel: false, supportsBulk: false, requiresUserNotice: false,
  needsConfirmation: true, needsReason: false, needsTypedConfirmation: false, heavy: false, isPower: false, checksProtected: false, implemented: true, allowed: true, ...over,
});
const catalog = [
  entry('svc.start', 'Start service', 'Medium'), entry('svc.stop', 'Stop service', 'Medium'), entry('svc.restart', 'Restart service', 'Medium'),
  entry('proc.kill', 'Stop process', 'High', { category: 'processes', needsTypedConfirmation: true, parameters: [] }),
];

const row = (name: string, state: string, startMode: string, allowed: ServiceAction[], prot = false) =>
  ({ name, displayName: `${name} display`, state, startMode, protected: prot, allowed });
let services: unknown;
let servicesError: Error | null;
let dependents: unknown;
const onStarted = vi.fn();

beforeEach(() => {
  vi.resetAllMocks();
  servicesError = null;
  services = { items: [] };
  dependents = { running: [], all: [], anyProtected: false };
  api.get.mockImplementation(async (path: string) => {
    if (path === '/api/jobs/catalog') return catalog;
    if (path === `/api/endpoints/${E1}/services`) { if (servicesError) throw servicesError; return services; }
    if (path.includes('/dependents')) return dependents;
    if (path.includes('/metrics')) return [];
    if (path === '/api/thresholds') return { cpuAlertPercent: 90, ramAlertPercent: 90, diskAlertPercent: 90 };
    if (path.endsWith('/detail')) return { services: [], processes: [{ pid: 1234, name: 'notepad.exe', workingSetBytes: 1000 }], disks: [], software: [], errors: [], warnings: [] };
    return [];
  });
  api.post.mockImplementation(async (path: string) => (path === '/api/jobs/confirm' ? { token: 't' } : { job: { id: 'j1' }, targets: [] }));
});
afterEach(cleanup);

function mount(ui: ReactNode) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={qc}><StartedJobContext.Provider value={onStarted}>{ui}</StartedJobContext.Provider></QueryClientProvider>);
}
const btn = (verb: string, name: string) => screen.getByRole('button', { name: `${verb} ${name}` });

describe('Services tab', () => {
  it('enables Start / Stop / Restart from the server allowed[] only', async () => {
    services = { items: [
      row('Spooler', 'Running', 'Auto', ['stop', 'restart']),
      row('Fax', 'Stopped', 'Manual', ['start']),
      row('Odd', 'Running', 'Auto', ['start']), // contradicts the state on purpose: the UI follows allowed[]
      row('Off', 'Stopped', 'Disabled', []),
    ] };
    mount(<ServicesTab e={ep()} me={me('Operator')} />);
    await screen.findByRole('table', { name: 'Services' });
    const table: [string, [boolean, boolean, boolean]][] = [['Spooler', [false, true, true]], ['Fax', [true, false, false]], ['Odd', [true, false, false]], ['Off', [false, false, false]]];
    for (const [n, [s, st, r]] of table) {
      expect(btn('Start', n).hasAttribute('disabled')).toBe(!s);
      expect(btn('Stop', n).hasAttribute('disabled')).toBe(!st);
      expect(btn('Restart', n).hasAttribute('disabled')).toBe(!r);
    }
    expect(btn('Start', 'Spooler')).toHaveAttribute('title', 'Already running');
    expect(btn('Start', 'Off')).toHaveAttribute('title', 'Start type is Disabled');
    expect(btn('Stop', 'Fax')).toHaveAttribute('title', 'Not running');
  });

  it('a protected row shows the badge and has every button disabled with an explanation', async () => {
    services = { items: [row('WinDefend', 'Running', 'Auto', [], true)] };
    mount(<ServicesTab e={ep()} me={me('Admin')} />);
    await screen.findByText('Protected');
    for (const v of ['Start', 'Stop', 'Restart']) expect(btn(v, 'WinDefend')).toBeDisabled();
    for (const v of ['Stop', 'Restart']) {
      expect(btn(v, 'WinDefend').getAttribute('title')).toMatch(/Protected: cannot be stopped or restarted/);
    }
  });

  it('search and status filters narrow the list; server text is shown as text', async () => {
    services = { items: [row('Spooler', 'Running', 'Auto', []), { ...row('Evil', 'Stopped', 'Manual', []), displayName: '<img src=x onerror=alert(1)>' }] };
    mount(<ServicesTab e={ep()} me={me('Operator')} />);
    expect(await screen.findByText('<img src=x onerror=alert(1)>')).toBeInTheDocument();
    const user = userEvent.setup();
    await user.selectOptions(screen.getByLabelText('Status filter'), 'running');
    expect(screen.queryByText('Evil')).toBeNull();
    await user.selectOptions(screen.getByLabelText('Status filter'), 'all');
    await user.type(screen.getByRole('searchbox', { name: 'Search services' }), 'spool');
    expect(screen.queryByText('Evil')).toBeNull();
    expect(screen.getByText('Spooler')).toBeInTheDocument();
  });

  it('a Viewer sees the list without action buttons; a 403 shows the Operator-role notice', async () => {
    services = { items: [row('Spooler', 'Running', 'Auto', [])] };
    mount(<ServicesTab e={ep()} me={me('Viewer')} />);
    await screen.findByText('Spooler');
    expect(screen.queryByRole('button', { name: /^(Start|Stop|Restart) / })).toBeNull();
    cleanup();
    servicesError = new api.ApiError(403, 'Forbidden');
    mount(<ServicesTab e={ep()} me={me('Viewer')} />);
    expect(await screen.findByText(/Operator role required/)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('an offline host shows the banner (last known state when there is one) and disables every button', async () => {
    services = { items: [row('Spooler', 'Running', 'Auto', ['stop', 'restart'])] };
    mount(<ServicesTab e={ep({ online: false })} me={me('Operator')} />);
    await vi.waitFor(() => expect(screen.getByTestId('services-offline')).toHaveTextContent('last known state'));
    await screen.findByText('Spooler');
    expect(btn('Stop', 'Spooler')).toBeDisabled();
    expect(btn('Restart', 'Spooler')).toBeDisabled();
    cleanup();
    services = { unavailable: true, reason: 'offline', items: [] };
    mount(<ServicesTab e={ep({ online: false })} me={me('Operator')} />);
    await vi.waitFor(() => expect(screen.getByTestId('services-offline')).toHaveTextContent('Host offline. Its services cannot be read'));
  });
});

describe('dependents flow', () => {
  const open = async (verb: 'Stop' | 'Restart') => {
    services = { items: [row('LanmanServer', 'Running', 'Auto', ['stop', 'restart'])] };
    mount(<ServicesTab e={ep()} me={me('Operator')} />);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: `${verb} LanmanServer` }));
    return { user, dialog: await screen.findByRole('dialog') };
  };
  const body = () => api.post.mock.calls.find((c) => c[0] === '/api/jobs')?.[1] as { parameters: Record<string, unknown>; actionCode: string };

  it('without running dependents Stop just needs Run, and sends stopDependents=false', async () => {
    const { user, dialog } = await open('Stop');
    await within(dialog).findByText(/No running services depend/);
    await user.click(within(dialog).getByRole('button', { name: 'Run' }));
    await vi.waitFor(() => expect(body()).toBeDefined());
    expect(body().actionCode).toBe('svc.stop');
    expect(body().parameters).toMatchObject({ serviceName: 'LanmanServer', stopDependents: false });
    expect(body().parameters).not.toHaveProperty('name');
  });

  it('running dependents block Stop until the checkbox is ticked, then stopDependents=true is sent with High risk and typed hostname', async () => {
    dependents = { running: ['Browser', 'Netlogon'], all: ['Browser', 'Netlogon', 'Idle'], anyProtected: false };
    const { user, dialog } = await open('Stop');
    expect(await within(dialog).findByText('Browser')).toBeInTheDocument();
    expect(within(dialog).getByText('Netlogon')).toBeInTheDocument();
    expect(within(dialog).queryByText('Idle')).toBeNull();
    expect(dialog).toHaveTextContent('High');
    const run = within(dialog).getByRole('button', { name: 'Run' });
    expect(run).toBeDisabled();
    expect(dialog).toHaveTextContent('Without this, the action is blocked');
    await user.click(within(dialog).getByRole('checkbox', { name: 'Also stop dependent services (2)' }));
    expect(run).toBeDisabled(); // High: the hostname must be typed
    await user.type(within(dialog).getByLabelText(/Reason/), 'Planned maintenance');
    expect(run).toBeDisabled();
    await user.type(within(dialog).getByLabelText(/Type the hostname/), 'wks-111');
    expect(run).toBeEnabled();
    await user.click(run);
    await vi.waitFor(() => expect(body()).toBeDefined());
    expect(body().parameters).toMatchObject({ serviceName: 'LanmanServer', stopDependents: true });
    expect(body().parameters).not.toHaveProperty('name');
  });

  it('Restart uses the same check; a protected dependent blocks the action completely', async () => {
    dependents = { running: ['Browser'], all: ['Browser'], anyProtected: true };
    const { dialog } = await open('Restart');
    expect(await within(dialog).findByText(/protected service depends on this one/)).toBeInTheDocument();
    expect(within(dialog).queryByRole('checkbox')).toBeNull();
    expect(within(dialog).getByRole('button', { name: 'Run' })).toBeDisabled();
  });

  it('the service name field is read-only when preset, and the server effectiveRisk drives badge, typed hostname and the required reason', async () => {
    dependents = { running: ['Browser'], all: ['Browser'], anyProtected: false, effectiveRisk: 'Critical' };
    const { dialog } = await open('Stop');
    expect(within(dialog).getByLabelText(/Service name/)).toHaveAttribute('readonly');
    expect(await within(dialog).findByText('Browser')).toBeInTheDocument();
    expect(dialog).toHaveTextContent('Critical');
    expect(within(dialog).getByLabelText(/Type the hostname/)).toBeInTheDocument();
    expect(dialog).toHaveTextContent('Reason *');
  });

  it('an unavailable dependents answer blocks like an error', async () => {
    dependents = { unavailable: true, reason: 'host did not answer', running: [], all: [], anyProtected: false };
    const { dialog } = await open('Stop');
    expect(await within(dialog).findByText(/Could not check dependent services/)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Run' })).toBeDisabled();
  });

  it('the dependents panel follows the service name that is typed', async () => {
    api.get.mockImplementation(async (path: string) => {
      if (path.endsWith('/services/Alpha/dependents')) return { running: ['AlphaChild'], all: ['AlphaChild'], anyProtected: false };
      if (path.endsWith('/services/Beta/dependents')) return { running: [], all: [], anyProtected: false };
      return [];
    });
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={qc}><RunActionDialog entry={catalog[1]} me={me('Operator')} targets={[{ id: E1, name: 'WKS-111', hostname: 'WKS-111' }]} onClose={() => undefined} onCreated={() => undefined} /></QueryClientProvider>);
    const user = userEvent.setup();
    const dialog = await screen.findByRole('dialog');
    const input = within(dialog).getByLabelText(/Service name/);
    expect(within(dialog).getByRole('button', { name: 'Run' })).toBeDisabled();
    await user.type(input, 'Alpha');
    expect(await within(dialog).findByText('AlphaChild')).toBeInTheDocument();
    await user.click(within(dialog).getByRole('checkbox', { name: /Also stop dependent services/ }));
    await user.clear(input);
    await user.type(input, 'Beta');
    expect(await within(dialog).findByText(/No running services depend/)).toBeInTheDocument();
    expect(within(dialog).queryByText('AlphaChild')).toBeNull();
    await user.clear(input);
    await user.type(input, 'Alpha');
    expect(await within(dialog).findByText('AlphaChild')).toBeInTheDocument();
    expect(within(dialog).getByRole('checkbox', { name: /Also stop dependent services/ })).not.toBeChecked(); // the choice does not carry over
  });

  it('is blocked when the dependents cannot be read', async () => {
    api.get.mockImplementation(async (path: string) => {
      if (path === '/api/jobs/catalog') return catalog;
      if (path.includes('/dependents')) throw new Error('boom');
      return { items: [row('LanmanServer', 'Running', 'Auto', ['stop'])] };
    });
    mount(<ServicesTab e={ep()} me={me('Operator')} />);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Stop LanmanServer' }));
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(/Could not check dependent services/)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Run' })).toBeDisabled();
  });
});

describe('Processes tab', () => {
  it('keeps cached process actions disabled after going offline', async () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    qc.setQueryData(['endpoint-detail', E1], { processes: [{ pid: 1234, name: 'notepad.exe', workingSetBytes: 1000, owner: 'DOMAIN\\op', cpuPercent: 2.5 }], warnings: [] });
    render(<QueryClientProvider client={qc}><LiveProcesses e={ep({ online: false })} me={me('Operator')} /></QueryClientProvider>);
    expect(await screen.findByRole('button', { name: 'Stop process notepad.exe (1234)' })).toBeDisabled();
    expect(screen.getByText('DOMAIN\\op')).toBeInTheDocument();
    expect(screen.getByText('2.5')).toBeInTheDocument();
    expect(api.get).not.toHaveBeenCalledWith(`/api/endpoints/${E1}/detail`);
  });

  it('offers Stop (never Terminate or Restart) and the dialog is titled Stop process', async () => {
    mount(<LiveProcesses e={ep()} me={me('Operator')} />);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Read from workstation' }));
    const stop = await screen.findByRole('button', { name: 'Stop process notepad.exe (1234)' });
    expect(stop).toHaveTextContent('Stop');
    expect(screen.queryByRole('button', { name: /terminate|end process|restart/i })).toBeNull();
    await user.click(stop);
    expect(await screen.findByRole('dialog', { name: 'Stop process' })).toBeInTheDocument();
  });
});
