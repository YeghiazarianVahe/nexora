// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { CatalogEntry } from '../../jobs/jobTypes';
import type { EndpointSummary, Me } from '../../types';
import { DiagnoseRunPage } from './DiagnoseRunPage';
import { DiagnoseTab } from './DiagnoseTab';

const api = vi.hoisted(() => {
  class ApiError extends Error { constructor(public status: number, message: string) { super(message); } }
  return { get: vi.fn(), post: vi.fn(), ApiError };
});
vi.mock('../../api', () => ({ ...api }));
const rt = vi.hoisted(() => ({ connection: null as null | { invoke: (n: string, ...a: unknown[]) => Promise<void> }, epoch: 1 }));
vi.mock('../../realtime', () => ({ useRealtime: () => ({ connection: rt.connection, state: 'connected', epoch: rt.epoch }), useHubOn: () => undefined }));
vi.mock('../../toasts', () => ({ useToasts: () => ({ push: vi.fn() }) }));
vi.mock('../../jobs/RunActionDialog', () => ({
  RunActionDialog: ({ entry, preset, targets }: { entry: CatalogEntry; preset?: Record<string, unknown>; targets: { id: string }[] }) =>
    <div data-testid="run-dialog">{entry.code}|{JSON.stringify(preset)}|{targets[0].id}</div>,
}));

const E1 = 'e1';
const me = (role: Me['role']) => ({ account: 'AB\op', displayName: 'Op', role, isSuperAdmin: false, status: 'Active' } as Me);
const ep = (over: Partial<EndpointSummary> = {}) => ({ id: E1, hostname: 'WKS-111', name: 'WKS-111', online: true, ...over } as EndpointSummary);
const RUN = 'abcdef12-0000-4000-8000-000000000000';

const profiles = [{ id: 'slow-pc', name: 'PC is slow', description: 'Sluggish <b>PC</b>', collectors: 5 }, { id: 'no-net', name: 'No network', description: 'Cannot connect', collectors: 3 }];
const hist = (over: Record<string, unknown> = {}) => ({ id: RUN, profileId: 'slow-pc', profileName: 'PC is slow', status: 'Complete', complete: true, startedUtc: '2026-03-05T10:07:00Z', finishedUtc: '2026-03-05T10:08:00Z', findingCount: 2, topSeverity: 'critical', ...over });
const catEntry = (code: string, allowed = true) => ({ code, displayName: code, allowed } as CatalogEntry);
const run = (over: Record<string, unknown> = {}) => ({
  id: RUN, endpointId: E1, hostname: 'WKS-111', profileId: 'slow-pc', profileName: 'PC is slow', status: 'Complete', complete: true,
  startedUtc: '2026-03-05T10:07:00Z', finishedUtc: '2026-03-05T10:08:00Z', jobId: 'job-1', schemaVersion: 1,
  findings: [
    { id: 'f1', cause: 'Disk is full', confidence: 'high', severity: 'critical', score: 90, evidence: [{ label: 'Free', value: '1 GB' }, { label: 'Note', value: '<img src=x onerror=alert(1)>' }],
      actions: [{ kind: 'catalog', actionCode: 'disk.cleanup', params: { aggressive: true }, label: 'Clean disk' }, { kind: 'catalog', actionCode: 'svc.restart', label: 'Restart spooler' },
        { kind: 'manual', label: 'Check cables', instructions: 'Step 1: unplug.\nStep 2: replug.' }] },
    { id: 'f2', cause: 'Many startup apps <script>x</script>', confidence: 'low', severity: 'info', score: 10, evidence: [], actions: [] },
  ],
  collectors: [{ id: 'disk', status: 'ok' }, { id: 'eventlog', status: 'failed', reason: 'Access denied' }, { id: 'net', status: 'missing', reason: 'not collected' }],
  notEvaluated: ['rule.ram-pressure'], ...over,
});

let routes: Record<string, () => unknown>;
beforeEach(() => {
  vi.resetAllMocks();
  rt.connection = null;
  routes = {
    '/api/diagnose/profiles': () => profiles,
    [`/api/endpoints/${E1}/diagnose/runs`]: () => [hist()],
    [`/api/diagnose/runs/${RUN}`]: () => run(),
    [`/api/diagnose/runs/${RUN}/evidence`]: () => ({ disk: { free: 1 } }),
    '/api/jobs/catalog': () => [catEntry('disk.cleanup'), catEntry('svc.restart', false)],
  };
  api.get.mockImplementation(async (path: string) => { const r = routes[path]; if (!r) throw new Error(`unmocked ${path}`); return r(); });
});
afterEach(() => { cleanup(); vi.useRealTimers(); });

function Where() { return <div data-testid="where">{useLocation().pathname}</div>; }
function mount(ui: React.ReactNode, at = '/endpoints/WKS-111/diagnose') {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}><MemoryRouter initialEntries={[at]}><Routes><Route path="*" element={<>{ui}<Where /></>} /></Routes></MemoryRouter></QueryClientProvider>);
}
const mountRun = (role: Me['role'] = 'Operator', e = ep()) => mount(<DiagnoseRunPage e={e} me={me(role)} runId={RUN} />, `/endpoints/WKS-111/diagnose/${RUN}`);
const calls = (path: string) => api.get.mock.calls.filter((c) => c[0] === path).length;

describe('Diagnose tab', () => {
  it('a Viewer sees the notice and no request is made', async () => {
    mount(<DiagnoseTab e={ep()} me={me('Viewer')} />);
    expect(await screen.findByTestId('operator-required')).toHaveTextContent('Operator role required');
    expect(api.get).not.toHaveBeenCalled();
    expect(api.post).not.toHaveBeenCalled();
  });

  it('pick a symptom, run: POST body is exactly {profileId} and it navigates to the run page', async () => {
    api.post.mockResolvedValue({ runId: RUN, jobId: 'j1' });
    mount(<DiagnoseTab e={ep()} me={me('Operator')} />);
    const run1 = screen.getByRole('button', { name: 'Run diagnosis' });
    expect(run1).toBeDisabled();
    await userEvent.click(await screen.findByRole('radio', { name: /No network/ }));
    await userEvent.click(run1);
    await waitFor(() => expect(api.post).toHaveBeenCalledWith(`/api/endpoints/${E1}/diagnose`, { profileId: 'no-net' }));
    expect(api.post.mock.calls[0][1]).toStrictEqual({ profileId: 'no-net' });
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent(`/endpoints/WKS-111/diagnose/${RUN}`));
  });

  it('never runs anything by itself', async () => {
    mount(<DiagnoseTab e={ep()} me={me('Operator')} />);
    await screen.findByRole('radio', { name: /PC is slow/ });
    expect(api.post).not.toHaveBeenCalled();
  });

  it('a 501 shows the friendly disabled message inline', async () => {
    api.post.mockRejectedValue(new api.ApiError(501, 'Diagnostics feature is off'));
    mount(<DiagnoseTab e={ep()} me={me('Operator')} />);
    await userEvent.click(await screen.findByRole('radio', { name: /PC is slow/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Run diagnosis' }));
    const p = await screen.findByTestId('dg-problem');
    expect(p).toHaveTextContent('Diagnostics is not enabled on this server.');
    expect(p).toHaveTextContent('Diagnostics feature is off');
    expect(screen.getByTestId('where')).toHaveTextContent('/endpoints/WKS-111/diagnose');
  });

  it('is disabled with an explanation while a run is Running and when the endpoint is offline', async () => {
    routes[`/api/endpoints/${E1}/diagnose/runs`] = () => [hist({ status: 'Running', complete: false, finishedUtc: null })];
    mount(<DiagnoseTab e={ep()} me={me('Operator')} />);
    await userEvent.click(await screen.findByRole('radio', { name: /PC is slow/ }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Run diagnosis' })).toBeDisabled());
    expect(screen.getByTestId('dg-blocked')).toHaveTextContent('already running');
    cleanup();
    routes[`/api/endpoints/${E1}/diagnose/runs`] = () => [];
    mount(<DiagnoseTab e={ep({ online: false })} me={me('Operator')} />);
    await userEvent.click(await screen.findByRole('radio', { name: /PC is slow/ }));
    expect(screen.getByRole('button', { name: 'Run diagnosis' })).toBeDisabled();
    expect(screen.getByTestId('dg-blocked')).toHaveTextContent('offline');
  });

  it('history rows show Yerevan time, profile, status, count, severity and link to the run', async () => {
    mount(<DiagnoseTab e={ep()} me={me('Operator')} />);
    const link = await screen.findByRole('link', { name: 'Diagnosis abcdef12' });
    expect(link).toHaveAttribute('href', `/endpoints/WKS-111/diagnose/${RUN}`);
    expect(link).toHaveTextContent('05.03.2026 14:07');
    expect(link).toHaveTextContent('PC is slow');
    expect(link).toHaveTextContent('Complete');
    expect(link).toHaveTextContent('2 findings');
    expect(link).toHaveTextContent('critical');
  });

  it('renders server text as text', async () => {
    mount(<DiagnoseTab e={ep()} me={me('Operator')} />);
    expect(await screen.findByText('Sluggish <b>PC</b>')).toBeInTheDocument();
    expect(document.querySelector('b b')).toBeNull();
  });
});

describe('Diagnosis run page', () => {
  it('a Viewer sees the notice and no request is made', async () => {
    mountRun('Viewer');
    expect(await screen.findByTestId('operator-required')).toBeInTheDocument();
    expect(api.get).not.toHaveBeenCalled();
  });

  it('unknown run shows the not-found page', async () => {
    routes[`/api/diagnose/runs/${RUN}`] = () => { throw new api.ApiError(404, 'nope'); };
    mountRun();
    expect(await screen.findByRole('alert')).toHaveTextContent(/does not exist/);
  });

  it('shows the header, the incomplete banner with collector reasons, and findings in the given order', async () => {
    routes[`/api/diagnose/runs/${RUN}`] = () => run({ complete: false, status: 'Incomplete' });
    mountRun();
    const banner = await screen.findByTestId('dg-incomplete');
    expect(banner).toHaveTextContent('Incomplete result');
    expect(banner).toHaveTextContent('eventlog: failed - Access denied');
    expect(banner).toHaveTextContent('net: missing - not collected');
    expect(banner).not.toHaveTextContent('disk');
    expect(screen.getByRole('region', { name: 'Diagnosis summary' })).toHaveTextContent('05.03.2026 14:07');
    const items = screen.getAllByRole('article');
    expect(items[0]).toHaveTextContent('Disk is full');
    expect(items[0]).toHaveTextContent('critical');
    expect(items[0]).toHaveTextContent('confidence: high');
    expect(items[1]).toHaveTextContent('Many startup apps');
  });

  it('no banner for a complete run', async () => {
    mountRun();
    await screen.findByText('Disk is full');
    expect(screen.queryByTestId('dg-incomplete')).toBeNull();
  });

  it('findings expand to evidence (as text) and manual steps; catalog actions open the dialog preset', async () => {
    mountRun();
    const head = await screen.findByRole('button', { name: /Disk is full/ });
    expect(head).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByText('1 GB')).toBeNull();
    await userEvent.click(head);
    expect(head).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('1 GB')).toBeInTheDocument();
    expect(screen.getByText('<img src=x onerror=alert(1)>')).toBeInTheDocument();
    expect(document.querySelector('img')).toBeNull();
    expect(screen.getByText(/Step 1: unplug\.\s+Step 2: replug\./)).toBeInTheDocument();
    await userEvent.click(await screen.findByRole('button', { name: 'Run: Clean disk' }));
    expect(screen.getByTestId('run-dialog')).toHaveTextContent(`disk.cleanup|{"aggressive":true}|${E1}`);
  });

  it('an action missing from the user catalog is disabled with a tooltip', async () => {
    mountRun();
    await userEvent.click(await screen.findByRole('button', { name: /Disk is full/ }));
    const b = await screen.findByRole('button', { name: 'Run: Restart spooler' });
    await waitFor(() => expect(b).toHaveAttribute('title', 'Not available for your role'));
    expect(b).toBeDisabled();
  });

  it('cause HTML is rendered as text', async () => {
    mountRun();
    await screen.findByText(/Many startup apps/);
    expect(document.querySelector('script')).toBeNull();
  });

  it('Not evaluated is collapsed; Raw evidence is fetched lazily on open', async () => {
    mountRun();
    const ne = await screen.findByTestId('dg-notevaluated');
    expect(ne).not.toHaveAttribute('open');
    expect(ne).toHaveTextContent('rule.ram-pressure');
    expect(calls(`/api/diagnose/runs/${RUN}/evidence`)).toBe(0);
    const raw = screen.getByTestId('dg-raw');
    raw.setAttribute('open', '');
    await act(async () => { raw.dispatchEvent(new Event('toggle')); });
    expect(await screen.findByLabelText('Raw evidence JSON')).toHaveTextContent('"free": 1');
    expect(calls(`/api/diagnose/runs/${RUN}/evidence`)).toBe(1);
  });

  it('caps a large raw evidence view and offers Show more', async () => {
    routes[`/api/diagnose/runs/${RUN}/evidence`] = () => ({ big: 'x'.repeat(50_000) });
    mountRun();
    const raw = await screen.findByTestId('dg-raw');
    raw.setAttribute('open', '');
    await act(async () => { raw.dispatchEvent(new Event('toggle')); });
    const pre = await screen.findByLabelText('Raw evidence JSON');
    expect(pre.textContent?.length).toBe(20_000);
    await userEvent.click(screen.getByText('Raw JSON for troubleshooting'));
    await userEvent.click(screen.getByRole('button', { name: /Show more/ }));
    expect(screen.getByLabelText('Raw evidence JSON').textContent?.length).toBe(40_000);
  });

  it('Running: spinner, host, job link, polls every 3 s and stops at a terminal status', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let status = 'Running';
    routes[`/api/diagnose/runs/${RUN}`] = () => run(status === 'Running' ? { status: 'Running', complete: false, finishedUtc: null, findings: [] } : {});
    mountRun();
    expect(await screen.findByTestId('dg-running')).toHaveTextContent('Collecting evidence from WKS-111...');
    expect(screen.getByRole('link', { name: 'View the job' })).toHaveAttribute('href', '/endpoints/WKS-111/jobs/job-1?from=%2Fendpoints%2FWKS-111%2Fdiagnose');
    const path = `/api/diagnose/runs/${RUN}`;
    const n0 = calls(path);
    await act(async () => { await vi.advanceTimersByTimeAsync(3100); });
    expect(calls(path)).toBeGreaterThan(n0);
    status = 'Complete';
    await act(async () => { await vi.advanceTimersByTimeAsync(3100); });
    expect(await screen.findByText('Disk is full')).toBeInTheDocument();
    const n1 = calls(path);
    await act(async () => { await vi.advanceTimersByTimeAsync(20_000); });
    expect(calls(path)).toBe(n1);
  });

  it('stops polling after 30 minutes and points to the job', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    routes[`/api/diagnose/runs/${RUN}`] = () => run({ status: 'Running', complete: false, finishedUtc: null, findings: [] });
    mountRun();
    await screen.findByTestId('dg-running');
    await act(async () => { await vi.advanceTimersByTimeAsync(20 * 60 * 1000); });
    expect(screen.queryByTestId('dg-capped')).toBeNull();
    await act(async () => { await vi.advanceTimersByTimeAsync(10 * 60 * 1000 + 4000); });
    expect(screen.getByTestId('dg-capped')).toHaveTextContent('Still running - check the job page.');
    expect(screen.getByRole('link', { name: 'View the job' })).toBeInTheDocument();
    const n = calls(`/api/diagnose/runs/${RUN}`);
    await act(async () => { await vi.advanceTimersByTimeAsync(30_000); });
    expect(calls(`/api/diagnose/runs/${RUN}`)).toBe(n);
  });

  it('joins the job group while Running and leaves on unmount', async () => {
    const invoke = vi.fn().mockResolvedValue(undefined);
    rt.connection = { invoke };
    routes[`/api/diagnose/runs/${RUN}`] = () => run({ status: 'Running', complete: false, finishedUtc: null, findings: [] });
    const view = mountRun();
    await screen.findByTestId('dg-running');
    await waitFor(() => expect(invoke).toHaveBeenCalledWith('SubscribeJob', 'job-1'));
    view.unmount();
    expect(invoke).toHaveBeenCalledWith('UnsubscribeJob', 'job-1');
  });
});
