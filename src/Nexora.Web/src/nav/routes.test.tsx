// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { RouterProvider, createMemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Endpoints } from '../pages/Endpoints';
import { endpointRoutes } from '../pages/EndpointDetail';
import { JobDetailPage } from '../jobs/JobDetail';
import type { EndpointSummary, Me } from '../types';
import { ENDPOINT_TABS, TAB_LABEL, endpointJobPath, endpointPath } from './origin';

const api = vi.hoisted(() => {
  class ApiError extends Error { constructor(public status: number, message: string) { super(message); } }
  return { get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn(), apiDownload: vi.fn(), ApiError };
});
vi.mock('../api', () => ({ ...api }));
vi.mock('../realtime', () => ({ useRealtime: () => ({ connection: null, state: 'connected', epoch: 1 }), useHubOn: () => undefined }));
vi.mock('../toasts', () => ({ useToasts: () => ({ push: vi.fn() }) }));
vi.mock('../jobs/JobOutputDialog', () => ({ JobOutputDialog: () => null }));
vi.mock('@tanstack/react-virtual', () => ({
  useVirtualizer: ({ count }: { count: number }) => ({
    getTotalSize: () => count * 44, scrollToIndex: () => undefined,
    getVirtualItems: () => Array.from({ length: count }, (_, i) => ({ index: i, start: i * 44, key: i })),
  }),
}));
class ResizeObserverStub { observe() {} unobserve() {} disconnect() {} }
(globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= ResizeObserverStub;

const GUID44 = '44444444-4444-4444-8444-444444444444';
const GUID11 = '11111111-1111-4111-8111-111111111111';
const JOB = '22222222-2222-4222-8222-222222222222';
const me = { account: 'AB\\op', displayName: 'Op', role: 'Operator', isSuperAdmin: false, status: 'Active' } as Me;

const pc = (id: string, hostname: string, over: Partial<EndpointSummary> = {}): EndpointSummary => ({
  id, hostname, name: hostname, reportedHostname: null, ipAddress: null, lastUser: null, osCaption: null, osVersion: null, uptimeSeconds: null,
  lastSeenUtc: null, online: true, cpuPercent: 10, ramPercent: 10, diskPercent: 10, groupIds: [], diskHealth: 'OK', lastError: null, alerts: [],
  diskHealthPercent: 95, manufacturer: null, model: null, organizationalUnit: null, isPinned: false, ssdHealthPercent: null, hddHealthPercent: null,
  ramTotalBytes: null, volumes: null, ...over,
} as EndpointSummary);
const fleet = [pc(GUID44, 'WKS-44'), pc(GUID11, 'WKS-111'), pc('id-3', 'WKS-LAPTOP7', { online: false })];

const jobFor = (endpointId: string) => ({
  job: { id: JOB, actionCode: 'net.flushdns', actionName: 'Flush DNS cache', initiatorSid: 'S-1', initiatorAccount: 'AB\\op', status: 'Succeeded', createdUtc: new Date().toISOString(),
    finishedUtc: new Date().toISOString(), reason: null, targetCount: 1, queued: 0, running: 0, succeeded: 1, warning: 0, failed: 0, cancelled: 0, timedOut: 0, skipped: 0, protectedOverride: false, risk: 'Low' },
  parametersJson: '{}',
  targets: [{ id: 't1', jobId: JOB, endpointId, endpointName: 'WKS-44', host: 'WKS-44', status: 'Succeeded', startedUtc: null, finishedUtc: null, durationSeconds: 3, exitCode: 0, resultSummary: 'ok', skipReason: null, progress: 100 }],
});

function route(path: string): unknown {
  const p = path.split('?')[0];
  const byHost = /^\/api\/endpoints\/by-host\/(.+)$/.exec(p);
  if (byHost) {
    const h = decodeURIComponent(byHost[1]);
    if (h.toLowerCase() === 'dup') throw new api.ApiError(409, 'More than one workstation matches that name.');
    const hit = fleet.find((e) => e.hostname.toLowerCase() === h.toLowerCase());
    if (!hit) throw new api.ApiError(404, 'Endpoint not found.');
    return hit;
  }
  const byId = /^\/api\/endpoints\/([0-9a-f-]{36})$/.exec(p);
  if (byId) {
    const hit = fleet.find((e) => e.id === byId[1]);
    if (!hit) throw new api.ApiError(404, 'Endpoint not found.');
    return hit;
  }
  if (p === '/api/endpoints') return fleet;
  if (p === `/api/jobs/${JOB}`) return jobFor(GUID44);
  if (p.startsWith('/api/jobs/') && p !== '/api/jobs/catalog') throw new api.ApiError(404, 'Job not found.');
  if (p === '/api/jobs' || p === '/api/audit') return { items: [], total: 0, page: 1, pageSize: 25 };
  if (p === '/api/thresholds') return { cpuAlertPercent: 90, ramAlertPercent: 90, diskAlertPercent: 90, healthOkFrom: 85, healthCriticalBelow: 50 };
  if (p.includes('/hardware')) return { status: 'Complete', collectedUtc: null, cached: false, serialsHidden: false, errors: [], os: null, cpu: null, memory: null, board: null, bios: null, computer: null };
  return [];
}

function mount(initial: string) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const router = createMemoryRouter([
    { path: '/endpoints', element: <Endpoints me={me} /> },
    ...endpointRoutes(me),
    { path: '/jobs/:id', element: <JobDetailPage me={me} /> },
    { path: '/jobs', element: <p>jobs</p> },
    { path: '/dashboard', element: <p>dashboard</p> },
  ], { initialEntries: [initial] });
  const view = render(<QueryClientProvider client={qc}><RouterProvider router={router} /></QueryClientProvider>);
  return { router, view };
}

beforeEach(() => {
  vi.resetAllMocks();
  window.sessionStorage.clear();
  api.get.mockImplementation(async (path: string) => route(path));
  api.post.mockImplementation(async () => ({}));
});
afterEach(cleanup);

const heading = () => screen.findByRole('heading', { level: 1, name: 'WKS-44' });

describe('every workstation path renders (deep link + refresh)', () => {
  it.each([
    ['/endpoints/WKS-44'],
    ...ENDPOINT_TABS.map((t) => [`/endpoints/WKS-44/${t}`]),
    ['/endpoints/WKS-44/actions/health-repair'],
  ])('%s', async (path) => {
    // the bare actions tab settles on its default category
    const settled = path.endsWith('/actions') ? `${path}/power-session` : path;
    const first = mount(path);
    expect(await heading()).toBeInTheDocument();
    await waitFor(() => expect(first.router.state.location.pathname).toBe(settled));
    first.view.unmount();
    // "refresh": a fresh app on the same URL shows the same view
    const again = mount(settled);
    expect(await heading()).toBeInTheDocument();
    expect(again.router.state.location.pathname).toBe(settled);
  });

  it('the active tab follows the URL', async () => {
    mount('/endpoints/WKS-44/services');
    await heading();
    expect(screen.getByRole('link', { name: TAB_LABEL.services })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('link', { name: TAB_LABEL.storage })).not.toHaveAttribute('aria-current');
  });

  it('a diagnose run URL renders under the Diagnose tab with a breadcrumb', async () => {
    mount('/endpoints/WKS-44/diagnose/run-1234567890');
    await heading();
    expect(screen.getByRole('navigation', { name: 'Breadcrumb' })).toHaveTextContent('Endpoints > WKS-44 > Diagnosis #run-1234');
  });

  it('a job path renders with the real host casing in the breadcrumb', async () => {
    mount(endpointJobPath('wks-44', JOB));
    const crumbs = await screen.findByRole('navigation', { name: 'Breadcrumb' });
    expect(crumbs).toHaveTextContent(`Endpoints > WKS-44 > Job ${JOB.slice(0, 8)}`);
  });
});

describe('old links', () => {
  it('a GUID in :host resolves the endpoint and replaces the URL with the host form', async () => {
    const { router } = mount(`/endpoints/${GUID44}/jobs`);
    await heading();
    await waitFor(() => expect(router.state.location.pathname).toBe('/endpoints/WKS-44/jobs'));
    expect(api.get).toHaveBeenCalledWith(`/api/endpoints/${GUID44}`);
  });

  it('a bare GUID link and a GUID job link are replaced too', async () => {
    const a = mount(`/endpoints/${GUID44}`);
    await heading();
    await waitFor(() => expect(a.router.state.location.pathname).toBe('/endpoints/WKS-44'));
    a.view.unmount();
    const b = mount(`/endpoints/${GUID44}/jobs/${JOB}`);
    await screen.findByRole('navigation', { name: 'Breadcrumb' });
    await waitFor(() => expect(b.router.state.location.pathname).toBe(`/endpoints/WKS-44/jobs/${JOB}`));
  });

  it('another letter case in the URL is replaced by the real host name', async () => {
    const { router } = mount('/endpoints/wks-44/logs');
    await heading();
    await waitFor(() => expect(router.state.location.pathname).toBe('/endpoints/WKS-44/logs'));
  });

  it.each([
    ['disks', 'storage'],
  ])('old tab name %s opens %s', async (old, now) => {
    const { router } = mount(`/endpoints/WKS-44/${old}`);
    await heading();
    expect(router.state.location.pathname).toBe(`/endpoints/WKS-44/${now}`);
  });

  it('an unknown tab goes to Overview', async () => {
    const { router } = mount('/endpoints/WKS-44/nonsense');
    await heading();
    expect(router.state.location.pathname).toBe('/endpoints/WKS-44/overview');
  });
});

describe('not found and conflicts', () => {
  it('an unknown host shows a not-found page with a link to /endpoints', async () => {
    mount('/endpoints/NOPE/overview');
    expect(await screen.findByRole('heading', { name: 'Workstation not found' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /workstation list/ })).toHaveAttribute('href', '/endpoints');
  });

  it('an unknown host in a job URL is not found too', async () => {
    mount(`/endpoints/NOPE/jobs/${JOB}`);
    expect(await screen.findByRole('heading', { name: 'Workstation not found' })).toBeInTheDocument();
  });

  it('a job with no target on that host is not found', async () => {
    mount(`/endpoints/WKS-111/jobs/${JOB}`);
    expect(await screen.findByRole('heading', { name: 'Job not found' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /workstation list/ })).toHaveAttribute('href', '/endpoints');
  });

  it('an unknown job is not found', async () => {
    mount('/endpoints/WKS-44/jobs/99999999-9999-4999-8999-999999999999');
    expect(await screen.findByRole('heading', { name: 'Job not found' })).toBeInTheDocument();
  });

  it('two rows that differ only by case (409) show a clear message', async () => {
    mount('/endpoints/dup/overview');
    expect(await screen.findByRole('heading', { name: 'More than one workstation matches' })).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent(/differ only by letter case/);
    expect(screen.getByRole('link', { name: /workstation list/ })).toHaveAttribute('href', '/endpoints');
  });

  it('the host from the URL is encoded in the request and never rendered as markup', async () => {
    const evil = '<img src=x onerror=alert(1)>';
    mount(`/endpoints/${encodeURIComponent(evil)}/overview`);
    await screen.findByRole('heading', { name: 'Workstation not found' });
    expect(api.get).toHaveBeenCalledWith(`/api/endpoints/by-host/${encodeURIComponent(evil)}`);
    expect(document.querySelector('img')).toBeNull();
  });
});

describe('Back button', () => {
  it('job -> the workstation; workstation (any tab) -> the list', async () => {
    const user = userEvent.setup();
    const { router } = mount(endpointJobPath('WKS-44', JOB));
    await user.click(await screen.findByTestId('job-back'));
    await heading();
    expect(router.state.location.pathname).toBe('/endpoints/WKS-44');
    await user.click(screen.getByRole('link', { name: 'Storage' }));
    await user.click(screen.getByRole('link', { name: 'Back to workstation list' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/endpoints'));
  });

  it('does not use history.back(): it works on a page opened directly', async () => {
    const user = userEvent.setup();
    const back = vi.spyOn(window.history, 'back');
    const { router } = mount(endpointPath('WKS-44', 'logs'));
    await heading();
    await user.click(screen.getByRole('link', { name: 'Back to workstation list' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/endpoints'));
    expect(back).not.toHaveBeenCalled();
  });

  it('browser back/forward walk through tabs normally', async () => {
    const user = userEvent.setup();
    const { router } = mount('/endpoints/WKS-44/overview');
    await heading();
    await user.click(screen.getByRole('link', { name: 'Jobs' }));
    await user.click(screen.getByRole('link', { name: 'Logs' }));
    await router.navigate(-1);
    expect(router.state.location.pathname).toBe('/endpoints/WKS-44/jobs');
    await router.navigate(1);
    expect(router.state.location.pathname).toBe('/endpoints/WKS-44/logs');
  });
});

describe('list filters survive list -> workstation -> list', () => {
  it('filters and sort land in the query string and come back with the Back button', async () => {
    const user = userEvent.setup();
    const { router } = mount('/endpoints');
    await screen.findAllByTestId('endpoint-row');
    await user.type(screen.getByLabelText('Search'), 'wks');
    await user.selectOptions(screen.getByLabelText('Status'), 'online');
    await user.click(screen.getByRole('button', { name: /^Endpoint/ })); // name is the default ascending sort: a click flips it
    await waitFor(() => expect(router.state.location.search).toContain('q=wks'));
    expect(router.state.location.search).toContain('status=online');
    expect(router.state.location.search).toContain('sort=name');
    expect(router.state.location.search).toContain('dir=desc');
    await waitFor(() => expect(screen.getAllByTestId('endpoint-row')).toHaveLength(2));

    await user.click(screen.getAllByTestId('endpoint-row')[0]);
    await screen.findByRole('link', { name: 'Back to workstation list' });
    expect(router.state.location.pathname).toMatch(/^\/endpoints\/WKS/);
    await user.click(screen.getByRole('link', { name: 'Back to workstation list' }));

    await waitFor(() => expect(router.state.location.pathname).toBe('/endpoints'));
    const q = new URLSearchParams(router.state.location.search);
    expect(q.get('q')).toBe('wks');
    expect(q.get('status')).toBe('online');
    expect(q.get('dir')).toBe('desc');
    expect(screen.getByLabelText('Search')).toHaveValue('wks');
    expect(screen.getByLabelText('Status')).toHaveValue('online');
    const rows = await screen.findAllByTestId('endpoint-row');
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent('WKS-111'); // descending by name (numeric-aware)
  });

  it('a pasted list URL restores the filters', async () => {
    mount('/endpoints?q=laptop&status=offline');
    await waitFor(() => expect(screen.getAllByTestId('endpoint-row')).toHaveLength(1));
    expect(screen.getByLabelText('Search')).toHaveValue('laptop');
  });

  it('the selection is not part of the URL', async () => {
    const user = userEvent.setup();
    const { router } = mount('/endpoints');
    await screen.findAllByTestId('endpoint-row');
    await user.click(screen.getAllByRole('checkbox')[1]);
    expect(router.state.location.search).toBe('');
  });
});

describe('open redirect', () => {
  it.each(['https://evil.example/x', '//evil.example', '/\\evil.example', 'javascript:alert(1)', '/settings'])('?from=%s never becomes the Back target', async (from) => {
    mount(`/jobs/${JOB}?from=${encodeURIComponent(from)}`);
    const back = await screen.findByTestId('job-back');
    expect(back.getAttribute('href')).toBe('/jobs');
  });

  it('a saved list query cannot carry anything but list parameters', async () => {
    window.sessionStorage.setItem('nexora.endpoints.listQuery', 'q=wks&next=https%3A%2F%2Fevil.example');
    mount('/endpoints/WKS-44/overview');
    await heading();
    expect(screen.getByRole('link', { name: 'Back to workstation list' })).toHaveAttribute('href', '/endpoints?q=wks');
  });
});
