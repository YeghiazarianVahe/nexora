// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider, focusManager } from '@tanstack/react-query';
import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { mergePerformanceHistory, useOnScreenPerformance } from './livePerformance';

const api = vi.hoisted(() => ({ get: vi.fn() }));
vi.mock('../../api', () => api);
let visible = 'visible';
beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  visible = 'visible';
  vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visible as DocumentVisibilityState);
  focusManager.setFocused(true);
  api.get.mockReset();
  api.get.mockImplementation(async (path: string) => ({ endpointId: path.includes('/two/') ? 'two' : 'one', hostname: path.includes('/two/') ? 'PC-2' : 'PC-1',
    sample: { cpuLoadPercent: api.get.mock.calls.length, ramPercentUsed: 50, ramTotalBytes: 1000, ramUsedBytes: 500, ramFreeBytes: 500, sampledUtc: new Date().toISOString() } }));
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.useRealTimers(); focusManager.setFocused(undefined); });

function View({ id = 'one', host = 'PC-1' }: { id?: string; host?: string }) {
  const live = useOnScreenPerformance(id, host);
  return <div>{live.status}<span data-testid="cpu">{live.sample?.cpuLoadPercent ?? 'none'}</span><span data-testid="samples">{live.samples.length}</span></div>;
}
function mount() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}><View /></QueryClientProvider>);
}
async function tick(ms: number) { await act(async () => { await vi.advanceTimersByTimeAsync(ms); }); }

it('reads every second only while the workstation is visible and stops after leaving', async () => {
  const mounted = mount();
  await tick(50);
  expect(api.get).toHaveBeenCalledTimes(1);
  await tick(2100);
  expect(api.get.mock.calls.length).toBeGreaterThanOrEqual(3);
  const before = api.get.mock.calls.length;
  visible = 'hidden';
  act(() => document.dispatchEvent(new Event('visibilitychange')));
  await tick(3100);
  expect(api.get).toHaveBeenCalledTimes(before);
  visible = 'visible';
  act(() => document.dispatchEvent(new Event('visibilitychange')));
  await tick(1100);
  expect(api.get.mock.calls.length).toBeGreaterThan(before);
  mounted.unmount();
  const stopped = api.get.mock.calls.length;
  await tick(3000);
  expect(api.get).toHaveBeenCalledTimes(stopped);
});

it('does not overlap slow requests and gives failures a five-second retry interval', async () => {
  let fail!: (error: Error) => void;
  api.get.mockImplementation(() => new Promise((_resolve, reject) => { fail = reject; }));
  mount();
  await tick(2100);
  expect(api.get).toHaveBeenCalledTimes(1);
  await act(async () => { fail(new Error('offline')); });
  await tick(2000);
  expect(api.get).toHaveBeenCalledTimes(1);
  await tick(3100);
  expect(api.get).toHaveBeenCalledTimes(2);
});

it('never substitutes one workstation’s live sample for a different workstation', async () => {
  api.get.mockResolvedValue({ endpointId: 'two', hostname: 'PC-2', sample: { cpuLoadPercent: 99, sampledUtc: new Date().toISOString() } });
  mount();
  await tick(50);
  expect(screen.getByTestId('cpu')).toHaveTextContent('none');
  expect(screen.getByTestId('samples')).toHaveTextContent('0');
});

it('includes read time in the one-second cadence instead of adding another full second after each read', async () => {
  api.get.mockImplementation(() => new Promise(resolve => setTimeout(() => resolve({ endpointId: 'one', hostname: 'PC-1', sample: {
    cpuLoadPercent: 10, ramPercentUsed: 50, ramTotalBytes: 1000, ramUsedBytes: 500, ramFreeBytes: 500, sampledUtc: new Date().toISOString(),
  } }), 300)));
  mount();
  await tick(3450);
  expect(api.get.mock.calls.length).toBeGreaterThanOrEqual(4);
  expect(api.get.mock.calls.length).toBeLessThanOrEqual(5);
});

it('adds high-frequency CPU/RAM points without inventing fresh disk readings', () => {
  const stored = [{ timeUtc: '2026-10-15T10:00:00Z', cpu: 1, ram: 40, disk: 50 }, { timeUtc: '2026-10-15T10:00:01Z', cpu: 2, ram: 41, disk: 50 }];
  const sample = { sampledUtc: '2026-10-15T10:00:01Z', cpuLoadPercent: 20, ramPercentUsed: 60, ramTotalBytes: 1000, ramUsedBytes: 600, ramFreeBytes: 400 };
  expect(mergePerformanceHistory(stored, [sample])).toEqual([stored[0], { timeUtc: sample.sampledUtc, cpu: 20, ram: 60, disk: null }]);
});

it('preserves stored samples collected while the browser was hidden', () => {
  const stored = [{ timeUtc: '2026-10-15T10:05:00Z', cpu: 10, ram: 50, disk: 60 }];
  const fields = { cpuLoadPercent: 20, ramPercentUsed: 60, ramTotalBytes: 1000, ramUsedBytes: 600, ramFreeBytes: 400 };
  const merged = mergePerformanceHistory(stored, [{ ...fields, sampledUtc: '2026-10-15T10:00:00Z' }, { ...fields, sampledUtc: '2026-10-15T10:10:00Z' }]);
  expect(merged).toHaveLength(3);
  expect(merged[1]).toEqual(stored[0]);
});
