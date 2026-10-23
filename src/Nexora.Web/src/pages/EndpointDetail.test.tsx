// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { RouterProvider, createMemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Jobs } from '../jobs/Jobs';
import { JobDetailPage } from '../jobs/JobDetail';
import type { CatalogEntry } from '../jobs/jobTypes';
import type { HardwareInventory, Me } from '../types';
import { endpointRoutes } from './EndpointDetail';

const api = vi.hoisted(() => {
  class ApiError extends Error { constructor(public status: number, message: string) { super(message); } }
  return { get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn(), ApiError };
});
vi.mock('../api', () => ({ ...api }));
vi.mock('../realtime', () => ({ useRealtime: () => ({ connection: null, epoch: 0 }), useHubOn: () => undefined }));
vi.mock('../toasts', () => ({ useToasts: () => ({ push: vi.fn() }) }));
vi.mock('../jobs/JobOutputDialog', async () => {
  const { Link } = await import('react-router-dom');
  return { JobOutputDialog: ({ openJobTo }: { openJobTo?: string }) => (openJobTo ? <div role="dialog" aria-label="Output"><Link to={openJobTo}>Open job</Link></div> : null) };
});

class ResizeObserverStub { observe() {} unobserve() {} disconnect() {} }
(globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= ResizeObserverStub;

const E1 = '11111111-1111-4111-8111-111111111111';
const JOB = '22222222-2222-4222-8222-222222222222';
const H = 'WKS-111';
const OTHER_JOB = '33333333-3333-4333-8333-333333333333';
const me = { account: 'AB\\op', displayName: 'Op', role: 'Operator', isSuperAdmin: false, status: 'Active' } as Me;

const endpoint = {
  id: E1, hostname: 'WKS-111', name: 'WKS-111', reportedHostname: 'WKS-111', ipAddress: '10.0.0.11', lastUser: 'AB\\anna', osCaption: 'Microsoft Windows 11 Pro',
  osVersion: '10.0.26100', uptimeSeconds: 3600, lastSeenUtc: new Date().toISOString(), online: true, cpuPercent: 12, ramPercent: 40, diskPercent: 50, groupIds: [],
  diskHealth: 'OK', diskHealthPercent: 90, ssdHealthPercent: 90, hddHealthPercent: null, ramTotalBytes: 16 * 1024 ** 3, volumes: [{ letter: 'C:', fileSystem: 'NTFS', totalBytes: 500 * 1024 ** 3, freeBytes: 200 * 1024 ** 3 }],
  manufacturer: 'Dell', model: 'OptiPlex', organizationalUnit: null, lastError: null, alerts: [], isPinned: false,
};

const inventory = (over: Partial<HardwareInventory> = {}): HardwareInventory => ({
  status: 'Complete', collectedUtc: new Date().toISOString(), cached: false, serialsHidden: false, errors: [],
  os: { productName: 'Microsoft Windows 11 Pro', edition: 'Professional', version: '24H2', build: '26100.4061', architecture: 'x64', installedUtc: null, bootTimeUtc: null },
  cpu: { manufacturer: 'Intel', model: 'Core i5-12500', physicalCores: 6, logicalProcessors: 12, architecture: 'x64', maxClockMhz: 3000 },
  memory: { installedBytes: 16 * 1024 ** 3, usableBytes: 15.8 * 1024 ** 3, technology: 'DDR4', speedMhz: 3200, slotCount: 4, occupiedSlots: 2, emptySlots: 2, maxCapacityBytes: null,
    modulesReported: true, modules: [{ slot: 'DIMM A1', bank: 'BANK 0', capacityBytes: 8 * 1024 ** 3, manufacturer: 'Kingston', speedMhz: 3200, configuredSpeedMhz: 3200, partNumber: 'KF3200', technology: 'DDR4' }] },
  board: { manufacturer: 'ASUS', model: 'PRIME', serial: 'SN1' }, bios: { manufacturer: 'AMI', version: '1803', releaseDateUtc: null },
  computer: { manufacturer: 'Dell', model: 'OptiPlex', hostname: 'WKS-111', domain: 'corp.local', domainJoined: true },
  ...over,
});

const entry = (over: Partial<CatalogEntry>): CatalogEntry => ({
  code: 'net.flushdns', displayName: 'Flush DNS cache', category: 'network-policy', risk: 'Low', minRole: 'Operator', method: 'SystemTask', parameters: [],
  timeoutSeconds: 60, description: 'Clears the DNS resolver cache', command: 'ipconfig /flushdns', supportsCancel: true, supportsBulk: true, requiresUserNotice: false,
  needsConfirmation: false, needsReason: false, needsTypedConfirmation: false, heavy: false, isPower: false, checksProtected: false, implemented: true, allowed: true, ...over,
});
const baseCatalog = [
  entry({}),
  entry({ code: 'power.restart', displayName: 'Restart computer', category: 'power-session', description: 'Restarts the PC', risk: 'High', needsConfirmation: true, needsReason: true, needsTypedConfirmation: true, isPower: true, command: 'shutdown /r' }),
  entry({ code: 'script.run', displayName: 'Run catalog script', category: 'admin-scripts', description: 'Runs an approved script', risk: 'Critical', command: null, minRole: 'Admin', allowed: false, needsConfirmation: true }),
];

const jobSummary = {
  id: JOB, actionCode: 'net.flushdns', actionName: 'Flush DNS cache', initiatorSid: 'S-1', initiatorAccount: 'AB\\op', status: 'Succeeded',
  createdUtc: new Date().toISOString(), finishedUtc: new Date().toISOString(), reason: null, targetCount: 1, queued: 0, running: 0, succeeded: 1, warning: 0, failed: 0,
  cancelled: 0, timedOut: 0, skipped: 0, protectedOverride: false, risk: 'Low',
};
const jobDetail = {
  job: jobSummary, parametersJson: '{}',
  targets: [{ id: 't1', jobId: JOB, endpointId: E1, endpointName: 'WKS-111', host: 'WKS-111', status: 'Succeeded', startedUtc: null, finishedUtc: null, durationSeconds: 3, exitCode: 0, resultSummary: 'ok', skipReason: null, progress: 100 }],
};

let hardware: HardwareInventory = inventory();
let catalog: CatalogEntry[] = baseCatalog;

function route(path: string): unknown {
  const p = path.split('?')[0];
  if (p === `/api/endpoints/${E1}` || p === '/api/endpoints/by-host/WKS-111' || p === '/api/endpoints/by-host/wks-111') return endpoint;
  if (p === '/api/endpoints/by-host/DUP') throw new api.ApiError(409, 'More than one workstation matches that name.');
  if (p.startsWith('/api/endpoints/by-host/')) throw new api.ApiError(404, 'Endpoint not found.');
  if (p === `/api/endpoints/${E1}/hardware`) return hardware;
  if (p === '/api/jobs/catalog') return catalog;
  if (p === `/api/jobs/${JOB}`) return jobDetail;
  if (p === `/api/jobs/${OTHER_JOB}`) return { ...jobDetail, job: { ...jobSummary, id: OTHER_JOB }, targets: [{ ...jobDetail.targets[0], endpointId: 'someone-else' }] };
  if (p.startsWith('/api/jobs/') && p.split('/').length === 4 && p !== '/api/jobs/catalog') throw new api.ApiError(404, 'Job not found.');
  if (p === '/api/jobs') return { items: [jobSummary], total: 1, page: 1, pageSize: 25 };
  if (p === '/api/thresholds') return { cpuAlertPercent: 90, ramAlertPercent: 90, diskAlertPercent: 90, healthOkFrom: 85, healthCriticalBelow: 50 };
  if (p.includes('/helper')) return { connected: true, everConnected: true, serverUrl: 'http://x' };
  if (p === '/api/groups') return [];
  return [];
}

function mount(initial: string) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const router = createMemoryRouter([
    ...endpointRoutes(me),
    { path: '/endpoints', element: <p data-testid="list-page">list</p> },
    { path: '/jobs', element: <Jobs me={me} /> },
    { path: '/jobs/:id', element: <JobDetailPage me={me} /> },
  ], { initialEntries: [initial] });
  render(<QueryClientProvider client={qc}><RouterProvider router={router} /></QueryClientProvider>);
  return router;
}

beforeEach(() => {
  vi.resetAllMocks();
  hardware = inventory();
  catalog = baseCatalog;
  api.get.mockImplementation(async (path: string) => route(path));
  api.post.mockImplementation(async (path: string) => (path === '/api/jobs' ? jobDetail : {}));
});
afterEach(cleanup);

describe('workstation tabs', () => {
  it('opens the tab named in the URL and keeps the persistent header', async () => {
    mount(`/endpoints/${H}/overview`);
    expect(await screen.findByRole('region', { name: 'Processor' })).toBeInTheDocument();
    expect(screen.queryByRole('table', { name: 'Memory modules' })).toBeNull();
    expect(screen.getByRole('heading', { level: 1, name: 'WKS-111' })).toBeInTheDocument();
    expect(screen.getByLabelText('Workstation summary')).toHaveTextContent('10.0.0.11');
    expect(screen.getByLabelText('Workstation summary')).toHaveTextContent('AB\\anna');
    expect(screen.getByRole('link', { name: 'Back to workstation list' })).toHaveAttribute('href', '/endpoints');
    expect(screen.getByRole('link', { name: 'Overview' })).toHaveAttribute('aria-current', 'page');
  });

  it('redirects the bare and unknown tab URLs to Overview', async () => {
    const r1 = mount(`/endpoints/${H}`);
    await screen.findByText('Operating system');
    expect(r1.state.location.pathname).toBe(`/endpoints/${H}`);
    cleanup();
    const r2 = mount(`/endpoints/${H}/nonsense`);
    await screen.findByText('Operating system');
    expect(r2.state.location.pathname).toBe(`/endpoints/${H}/overview`);
  });

  it('switching tabs pushes history entries so back and forward walk through them', async () => {
    const user = userEvent.setup();
    const router = mount(`/endpoints/${H}/overview`);
    await screen.findByText('Operating system');
    await user.click(screen.getByRole('link', { name: 'Storage' }));
    await screen.findByRole('table', { name: 'Logical volumes' });
    await user.click(screen.getByRole('link', { name: 'Monitoring' }));
    await screen.findByTestId('freshness');
    await router.navigate(-1);
    await screen.findByRole('table', { name: 'Logical volumes' });
    expect(router.state.location.pathname).toBe(`/endpoints/${H}/storage`);
    await router.navigate(1);
    await screen.findByTestId('freshness');
  });

  it('shows Unavailable and Unknown for missing data instead of zeros or invented values', async () => {
    hardware = inventory({
      status: 'Partial', errors: ['Memory modules: not supported'],
      cpu: { manufacturer: null, model: null, physicalCores: null, logicalProcessors: null, architecture: null, maxClockMhz: null },
      memory: { installedBytes: null, usableBytes: null, technology: null, speedMhz: null, slotCount: null, occupiedSlots: 0, emptySlots: null, maxCapacityBytes: null, modules: [], modulesReported: false },
      board: null, bios: null,
    });
    mount(`/endpoints/${H}/overview`);
    await screen.findByTestId('inventory-status');   // the inventory has been loaded
    const memory = within(screen.getByRole('region', { name: 'Overview' })).getAllByRole('heading', { name: 'Memory' })[0].closest('section')!;
    expect(within(memory).getByText('Unknown')).toBeInTheDocument();          // slot count
    expect(within(memory).queryByText('0')).toBeNull();
    expect(within(memory).queryByText(/^0 /)).toBeNull();
    expect(within(memory).getAllByText('Unavailable').length).toBeGreaterThan(2);
    expect(screen.getByTestId('inventory-status')).toHaveTextContent('Memory modules: not supported');
  });

  it('hardware tab: unknown slot count is not shown as zero empty slots', async () => {
    hardware = inventory({ memory: { ...inventory().memory!, slotCount: null, emptySlots: null, occupiedSlots: 1 } });
    mount(`/endpoints/${H}/hardware`);
    await screen.findByText(/Inventory collected/);
    const memory = within(screen.getByRole('region', { name: 'Hardware' })).getByRole('heading', { name: 'Memory' }).closest('section')!;
    const empty = within(memory).getByText('Empty').closest('.frow')!;
    expect(empty).toHaveTextContent('Unknown');
  });

  it('manual inventory refresh calls only the inventory endpoint', async () => {
    const user = userEvent.setup();
    api.post.mockResolvedValueOnce(inventory());
    mount(`/endpoints/${H}/hardware`);
    await user.click(await screen.findByRole('button', { name: 'Refresh inventory' }));
    await waitFor(() => expect(api.post).toHaveBeenCalledWith(`/api/endpoints/${E1}/hardware/refresh`));
    expect(api.post).not.toHaveBeenCalledWith('/api/jobs', expect.anything());
  });
});

describe('Action Center', () => {
  const names = () => screen.queryAllByTestId('action-card').map((c) => c.getAttribute('aria-label'));
  const tab = (label: RegExp | string) => screen.getByRole('tab', { name: label });

  it('shows risk, permission and requirements on the cards of the default tab', async () => {
    mount(`/endpoints/${H}/actions/power-session`);
    const restart = await screen.findByRole('button', { name: 'Run Restart computer' });
    expect(restart).toHaveTextContent('High');
    expect(restart).toHaveTextContent('Type the workstation name');
    expect(restart).toHaveTextContent('Reason required');
    expect(screen.getAllByTestId('action-card')).toHaveLength(1);
  });

  it('the bare actions URL redirects to the default category; the tab bar has aria roles and a count per tab', async () => {
    const router = mount(`/endpoints/${H}/actions`);
    await screen.findAllByTestId('action-card');
    expect(router.state.location.pathname).toBe(`/endpoints/${H}/actions/power-session`);
    expect(screen.getByRole('tablist', { name: 'Action categories' })).toBeInTheDocument();
    expect(tab(/Power & Session/)).toHaveAttribute('aria-selected', 'true');
    expect(tab(/Health & Repair/)).toHaveAttribute('aria-selected', 'false');
    expect(tab(/Power & Session/)).toHaveTextContent('1');
    expect(tab(/Network & Policy/)).toHaveTextContent('1');
    expect(tab(/Admin Scripts/)).toHaveTextContent('1');
    expect(tab(/Health & Repair/)).toHaveTextContent('0');
    expect(screen.queryByRole('tab', { name: /Other/ })).toBeNull();
  });

  it('a deep link opens that category; clicking and arrow keys change the category in the URL', async () => {
    const user = userEvent.setup();
    const router = mount(`/endpoints/${H}/actions/network-policy`);
    await screen.findByRole('button', { name: 'Run Flush DNS cache' });
    expect(names()).toEqual(['Run Flush DNS cache']);
    expect(tab(/Network & Policy/)).toHaveAttribute('aria-selected', 'true');
    expect(tab(/Network & Policy/)).toHaveAttribute('tabindex', '0');
    expect(tab(/Power & Session/)).toHaveAttribute('tabindex', '-1');
    await user.click(tab(/Power & Session/));
    expect(router.state.location.pathname).toBe(`/endpoints/${H}/actions/power-session`);
    expect(names()).toEqual(['Run Restart computer']);
    tab(/Power & Session/).focus();
    await user.keyboard('{ArrowRight}');
    expect(router.state.location.pathname).toBe(`/endpoints/${H}/actions/health-repair`);
    expect(tab(/Health & Repair/)).toHaveFocus();
    await user.keyboard('{ArrowLeft}{ArrowLeft}');
    expect(router.state.location.pathname).toBe(`/endpoints/${H}/actions/admin-scripts`);   // wraps around
    await user.keyboard('{Home}');
    expect(router.state.location.pathname).toBe(`/endpoints/${H}/actions/power-session`);
  });

  it('an unknown category in the URL redirects to the default', async () => {
    const router = mount(`/endpoints/${H}/actions/nonsense`);
    await screen.findByRole('button', { name: 'Run Restart computer' });
    expect(router.state.location.pathname).toBe(`/endpoints/${H}/actions/power-session`);
  });

  it('search spans every category, groups results with a tab hint, and the tab counts show the matches', async () => {
    const user = userEvent.setup();
    const router = mount(`/endpoints/${H}/actions/power-session`);
    await screen.findByRole('button', { name: 'Run Restart computer' });
    await user.type(screen.getByRole('searchbox', { name: 'Search actions' }), 'dns');
    expect(names()).toEqual(['Run Flush DNS cache']);                          // a match from another tab is listed
    expect(screen.getByText('in the Network & Policy tab')).toBeInTheDocument();
    expect(tab(/Network & Policy/)).toHaveTextContent('1');
    expect(tab(/Power & Session/)).toHaveTextContent('0');
    expect(router.state.location.search).toBe('?q=dns');
    await user.clear(screen.getByRole('searchbox', { name: 'Search actions' }));
    expect(names()).toEqual(['Run Restart computer']);
  });

  it('the risk filter narrows the list and the counts, and is kept in the query string', async () => {
    const user = userEvent.setup();
    const router = mount(`/endpoints/${H}/actions/power-session`);
    await screen.findByRole('button', { name: 'Run Restart computer' });
    await user.click(screen.getByRole('button', { name: 'Low' }));
    expect(names()).toEqual([]);                                                 // Restart is High
    expect(screen.getByText('No action matches your search or filter.')).toBeInTheDocument();
    expect(tab(/Network & Policy/)).toHaveTextContent('1');
    expect(tab(/Admin Scripts/)).toHaveTextContent('0');
    await user.click(screen.getByRole('button', { name: 'High' }));
    expect(router.state.location.search).toBe('?risk=Low%2CHigh');
    expect(names()).toEqual(['Run Restart computer']);
    expect(screen.getByRole('button', { name: 'High' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('actions of the services / processes / profiles categories are never in the grid; an unknown category lands under Other', async () => {
    const user = userEvent.setup();
    catalog = [
      ...catalog,
      entry({ code: 'svc.start', displayName: 'Start service', category: 'services' }),
      entry({ code: 'proc.kill', displayName: 'Stop process', category: 'processes' }),
      entry({ code: 'profile.delete', displayName: 'Delete user profile', category: 'profiles' }),
      entry({ code: 'future.thing', displayName: 'Brand new action', category: 'brand-new' }),
    ];
    mount(`/endpoints/${H}/actions/other`);
    expect(await screen.findByRole('button', { name: 'Run Brand new action' })).toBeInTheDocument();
    expect(names()).toEqual(['Run Brand new action']);
    expect(tab(/Other/)).toHaveTextContent('1');
    for (const c of ['Power & Session', 'System Health & Repair', 'Windows Update & Security', 'Network & Policy', 'Diagnostics', 'Admin Scripts']) {
      await user.click(tab(new RegExp(c)));
      expect(screen.queryByRole('button', { name: /Start service|Stop process|Delete user profile/ })).toBeNull();
    }
    await user.type(screen.getByRole('searchbox', { name: 'Search actions' }), 'e');   // matches all of them by name
    expect(screen.queryByRole('button', { name: /Start service|Stop process|Delete user profile/ })).toBeNull();
    expect(screen.getAllByRole('tab')).toHaveLength(7);
  });

  it('Back from an actions category goes to the workstation page', async () => {
    const user = userEvent.setup();
    const router = mount(`/endpoints/${H}/actions/health-repair`);
    const back = await screen.findByRole('link', { name: 'Back to workstation' });
    expect(back).toHaveAttribute('href', `/endpoints/${H}`);
    expect(screen.getByRole('link', { name: 'Remote Actions' })).toHaveAttribute('aria-current', 'page');
    await user.click(back);
    expect(router.state.location.pathname).toBe(`/endpoints/${H}`);
  });

  it('a high-risk action keeps its confirmation: nothing is submitted until it is confirmed', async () => {
    const user = userEvent.setup();
    mount(`/endpoints/${H}/actions/power-session`);
    await user.click(await screen.findByRole('button', { name: 'Run Restart computer' }));
    expect(await screen.findByRole('dialog')).toBeInTheDocument();
    expect(api.post).not.toHaveBeenCalledWith('/api/jobs', expect.anything());
  });
});

describe('job navigation', () => {
  it('returns to the originating action category with its search after a refresh', async () => {
    const from = `/endpoints/${H}/actions/network-policy?q=DNS&risk=Low`;
    mount(`/endpoints/${H}/jobs/${JOB}?from=${encodeURIComponent(from)}`);
    const back = await screen.findByTestId('job-back');
    expect(back).toHaveAttribute('href', from);
    await userEvent.click(back);
    expect(await screen.findByRole('searchbox', { name: 'Search actions' })).toHaveValue('DNS');
    expect(screen.getByRole('button', { name: 'Low' })).toHaveAttribute('aria-pressed', 'true');
  });
  it('workstation -> Remote Actions -> run: the output opens in place; Open job -> job page; Back returns to the workstation page', async () => {
    const user = userEvent.setup();
    const router = mount(`/endpoints/${H}/actions/network-policy`);
    await user.click(await screen.findByRole('button', { name: 'Run Flush DNS cache' }));
    expect(api.post).not.toHaveBeenCalledWith('/api/jobs', expect.anything());
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: /^Run$/ }));
    const open = await screen.findByRole('link', { name: 'Open job' });
    expect(router.state.location.pathname).toBe(`/endpoints/${H}/actions/network-policy`);   // the popup opened in place
    expect(open).toHaveAttribute('href', `/endpoints/${H}/jobs/${JOB}?from=${encodeURIComponent(`/endpoints/${H}/actions/network-policy`)}`);
    await user.click(open);
    await waitFor(() => expect(router.state.location.pathname).toBe(`/endpoints/${H}/jobs/${JOB}`));
    const crumbs = await screen.findByRole('navigation', { name: 'Breadcrumb' });
    expect(crumbs).toHaveTextContent(`Endpoints > ${H} > Job ${JOB.slice(0, 8)}`);
    await user.click(screen.getByTestId('job-back'));
    expect(await screen.findByRole('heading', { level: 1, name: 'WKS-111' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe(`/endpoints/${H}/actions/network-policy`);
  });

  it('the Jobs tab links open the job under the workstation and Back returns to the workstation', async () => {
    const user = userEvent.setup();
    const router = mount(`/endpoints/${H}/jobs`);
    const link = await screen.findByRole('link', { name: /Flush DNS cache/ });
    expect(link.getAttribute('href')).toBe(`/endpoints/${H}/jobs/${JOB}?from=${encodeURIComponent(`/endpoints/${H}/jobs`)}`);
    await user.click(link);
    await user.click(await screen.findByTestId('job-back'));
    await screen.findByRole('heading', { level: 1, name: 'WKS-111' });
    expect(router.state.location.pathname).toBe(`/endpoints/${H}/jobs`);
  });

  it.each(['services', 'processes', 'actions', 'jobs'])('scoped job reload returns to the originating %s tab', async (tab) => {
    mount(`/endpoints/${H}/jobs/${JOB}?from=${encodeURIComponent(`/endpoints/${H}/${tab}`)}`);
    expect(await screen.findByTestId('job-back')).toHaveAttribute('href', `/endpoints/${H}/${tab}`);
  });

  it('a scoped job refuses an origin on another workstation', async () => {
    mount(`/endpoints/${H}/jobs/${JOB}?from=${encodeURIComponent('/endpoints/OTHER/services')}`);
    expect(await screen.findByTestId('job-back')).toHaveAttribute('href', `/endpoints/${H}`);
  });

  it('a job with no target on that workstation is not found, with a way out', async () => {
    mount(`/endpoints/${H}/jobs/${OTHER_JOB}`);
    expect(await screen.findByRole('heading', { name: 'Job not found' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /workstation list/ })).toHaveAttribute('href', '/endpoints');
  });

  it('an unknown job id is not found', async () => {
    mount(`/endpoints/${H}/jobs/44444444-4444-4444-8444-444444444444`);
    expect(await screen.findByRole('heading', { name: 'Job not found' })).toBeInTheDocument();
  });

  it('global Jobs → job → Back returns to the global Jobs page', async () => {
    const user = userEvent.setup();
    const router = mount('/jobs');
    await user.click(await screen.findByRole('link', { name: /Flush DNS cache/ }));
    await waitFor(() => expect(router.state.location.pathname).toBe(`/jobs/${JOB}`));
    expect(router.state.location.search).toBe('');
    const back = await screen.findByTestId('job-back');
    expect(back).toHaveTextContent('Jobs');
    await user.click(back);
    expect(await screen.findByRole('heading', { name: 'Jobs' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/jobs');
  });

  it('a direct job link falls back to the Jobs list', async () => {
    mount(`/jobs/${JOB}`);
    expect(await screen.findByTestId('job-back')).toHaveAttribute('href', '/jobs');
  });

  it('reloading the job page keeps the origin (it lives in the URL)', async () => {
    mount(`/jobs/${JOB}?from=%2Fendpoints%2F${E1}%2Fstorage`); // an old link: GUID and legacy tab name
    const back = await screen.findByTestId('job-back');
    expect(back).toHaveAttribute('href', `/endpoints/${E1}/storage`);
    await waitFor(() => expect(back).toHaveTextContent('WKS-111 · Storage'));
  });

  it.each([
    'https://evil.example/endpoints/x',
    '//evil.example',
    '/\\evil.example',
    'javascript:alert(1)',
    `/endpoints/${E1}/../../admin`,
    '/settings',
    `/endpoints/${E1}/actions?x=1&redirect=http://evil`,
  ])('an unsafe origin is ignored: %s', async (from) => {
    mount(`/jobs/${JOB}?from=${encodeURIComponent(from)}`);
    const back = await screen.findByTestId('job-back');
    const href = back.getAttribute('href')!;
    expect(href === '/jobs' || href === `/endpoints/${E1}/actions`).toBe(true);
    expect(href).not.toContain('evil');
  });
});
