// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { JobOutputDialog } from './JobOutputDialog';
import type { JobDetailDto, JobTargetInfo, LogLineDto } from './jobTypes';

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }));
vi.mock('../api', () => ({ get: api.get, post: api.post, ApiError: class extends Error {} }));
vi.mock('../realtime', () => ({ useRealtime: () => ({ connection: null, state: 'connected', epoch: 1 }), useHubOn: () => undefined }));
vi.mock('@tanstack/react-virtual', () => ({
  useVirtualizer: ({ count }: { count: number }) => ({
    getTotalSize: () => count * 20, scrollToIndex: () => undefined,
    getVirtualItems: () => Array.from({ length: count }, (_, i) => ({ index: i, start: i * 20, key: i })),
  }),
}));
const toasts = vi.hoisted(() => ({ push: vi.fn() }));
vi.mock('../toasts', () => ({ useToasts: () => toasts }));

const target = (id: string, name: string, status: JobTargetInfo['status']): JobTargetInfo => ({
  id, jobId: 'job-1', endpointId: `e-${id}`, endpointName: name, host: `${name}.local`, status, startedUtc: null, finishedUtc: null, durationSeconds: null,
  exitCode: null, resultSummary: null, skipReason: null, progress: null, logLines: 0, hasOutputFile: false, outputSha256: null,
});

const detail = (status: JobDetailDto['job']['status'], targets: JobTargetInfo[]): JobDetailDto => ({
  job: { id: 'job-1', actionCode: 'repair.sfc', actionName: 'Repair system files', initiatorSid: 'S', initiatorAccount: 'CONTOSO\\demo', status, createdUtc: '2026-10-13T10:00:00Z',
    finishedUtc: null, reason: null, targetCount: targets.length, queued: 0, running: 0, succeeded: 0, warning: 0, failed: 0, cancelled: 0, timedOut: 0, skipped: 0, protectedOverride: false, risk: 'Medium' },
  parametersJson: '{}', targets,
});

const lines = (texts: string[]): LogLineDto[] => texts.map((text, i) => ({ seq: i + 1, timestampUtc: '2026-10-13T10:00:00Z', stream: 'stdout', text }));

function show(job: JobDetailDto, logs: Record<string, LogLineDto[]>, props: Partial<Parameters<typeof JobOutputDialog>[0]> = {}) {
  api.get.mockImplementation(async (path: string) => {
    if (path === '/api/jobs/job-1') return job;
    const m = /targets\/([^/]+)\/log/.exec(path);
    return m ? (logs[m[1]] ?? []) : [];
  });
  const onClose = vi.fn();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={qc}><JobOutputDialog jobId="job-1" onClose={onClose} {...props} /></QueryClientProvider>);
  return { onClose };
}

beforeEach(() => { api.get.mockReset(); api.post.mockReset(); toasts.push.mockReset(); });
afterEach(cleanup);

describe('Job output window (N9)', () => {
  it('shows the output in sequence order with a plain-language summary of what sfc printed', async () => {
    show(detail('Succeeded', [target('t1', 'PC-1', 'Succeeded')]),
      { t1: lines(['Beginning system scan.', 'Windows Resource Protection did not find any integrity violations.']) });

    expect(await screen.findByTestId('output-summary')).toHaveTextContent('No integrity violations found.');
    const log = screen.getByRole('log');
    expect(log.textContent).toContain('Beginning system scan.');
    expect(log.textContent!.indexOf('Beginning')).toBeLessThan(log.textContent!.indexOf('Windows Resource Protection'));
  });

  it('lists every workstation of a bulk job, starts on the failed one, and switches output when another is picked', async () => {
    const user = userEvent.setup();
    show(detail('PartiallyFailed', [target('t1', 'PC-1', 'Succeeded'), target('t2', 'PC-2', 'Failed')]),
      { t1: lines(['output of the first']), t2: lines(['output of the second']) });

    expect(await screen.findByText('output of the second')).toBeInTheDocument(); // the failed one is shown first
    await user.click(screen.getByRole('button', { name: /PC-1/ }));
    expect(await screen.findByText('output of the first')).toBeInTheDocument();
    expect(screen.queryByText('output of the second')).toBeNull();
  });

  it('Esc closes the window and does NOT cancel the job', async () => {
    const user = userEvent.setup();
    const { onClose } = show(detail('Running', [target('t1', 'PC-1', 'Running')]), { t1: lines(['working...']) });
    await screen.findByText('working...');

    await user.keyboard('{Escape}');

    expect(onClose).toHaveBeenCalledTimes(1);
    expect(api.post).not.toHaveBeenCalled();
  });

  it('Cancel job asks first, and Esc then only closes that question', async () => {
    const user = userEvent.setup();
    const { onClose } = show(detail('Running', [target('t1', 'PC-1', 'Running')]), { t1: lines(['working...']) });
    await user.click(await screen.findByRole('button', { name: 'Cancel job' }));
    expect(screen.getByRole('dialog', { name: 'Cancel this job' })).toBeInTheDocument();

    await user.keyboard('{Escape}');

    expect(screen.queryByRole('dialog', { name: 'Cancel this job' })).toBeNull();
    expect(onClose).not.toHaveBeenCalled();
    expect(api.post).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: 'Cancel job' }));
    await user.click(within(screen.getByRole('dialog', { name: 'Cancel this job' })).getByRole('button', { name: 'Cancel job' }));
    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/jobs/job-1/cancel'));
  });

  it('copies the output, offers a download, and offers a re-run for failed targets', async () => {
    const user = userEvent.setup();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const onRerun = vi.fn();
    show(detail('Failed', [target('t1', 'PC-1', 'Failed')]), { t1: lines(['one', 'two']) }, { onRerun });
    await screen.findByText('two');

    await user.click(screen.getByRole('button', { name: 'Copy' }));
    expect(writeText).toHaveBeenCalledWith('one\ntwo');
    await waitFor(() => expect(toasts.push).toHaveBeenCalledWith(expect.objectContaining({ title: 'Output copied' })));
    expect(screen.getByRole('link', { name: 'Download' })).toHaveAttribute('href', '/api/jobs/job-1/targets/t1/output');

    await user.click(screen.getByRole('button', { name: /Re-run on 1 failed/ }));
    expect(onRerun).toHaveBeenCalled();
  });
});

