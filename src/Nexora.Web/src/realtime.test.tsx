// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, render } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { RealtimeProvider } from './realtime';

const fake = vi.hoisted(() => ({
  state: 'Disconnected', start: vi.fn(), stop: vi.fn(), on: vi.fn(),
  onclose: vi.fn(), onreconnecting: vi.fn(), onreconnected: vi.fn(),
}));
vi.mock('./api', () => ({ currentToken: async () => 'test-token' }));
vi.mock('./toasts', () => ({ useToasts: () => ({ push: vi.fn() }) }));
vi.mock('@microsoft/signalr', () => ({
  HubConnectionState: { Connected: 'Connected', Disconnected: 'Disconnected', Connecting: 'Connecting', Reconnecting: 'Reconnecting' },
  LogLevel: { Warning: 3 },
  HubConnectionBuilder: class {
    withUrl() { return this; }
    withAutomaticReconnect() { return this; }
    configureLogging() { return this; }
    build() { return fake; }
  },
}));
beforeEach(() => {
  vi.useFakeTimers(); vi.clearAllMocks(); fake.state = 'Disconnected';
  fake.start.mockImplementation(async () => { fake.state = 'Connected'; });
  fake.stop.mockImplementation(async () => { fake.state = 'Disconnected'; });
});
afterEach(() => { cleanup(); vi.useRealTimers(); });

async function show() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const invalidate = vi.spyOn(qc, 'invalidateQueries');
  await act(async () => { render(<QueryClientProvider client={qc}><RealtimeProvider>App</RealtimeProvider></QueryClientProvider>); });
  return invalidate;
}
it('retries after automatic reconnect is exhausted and refreshes missed data', async () => {
  const invalidate = await show();
  invalidate.mockClear();
  fake.state = 'Disconnected';
  await act(async () => { fake.onclose.mock.calls[0][0](); });
  await act(async () => { await vi.advanceTimersByTimeAsync(10000); });
  expect(fake.start).toHaveBeenCalledTimes(2);
  expect(invalidate).toHaveBeenCalled();
});
it('does not abandon a slow initial retry or start concurrent connections', async () => {
  fake.start.mockRejectedValueOnce(new Error('offline'));
  await show();
  let complete: () => void = () => undefined;
  fake.start.mockImplementationOnce(() => {
    fake.state = 'Connecting';
    return new Promise<void>((_resolve, reject) => { complete = () => { fake.state = 'Disconnected'; reject(new Error('still offline')); }; });
  });
  await act(async () => { await vi.advanceTimersByTimeAsync(20000); });
  expect(fake.start).toHaveBeenCalledTimes(2);
  await act(async () => { complete(); });
  await act(async () => { await vi.advanceTimersByTimeAsync(10000); });
  expect(fake.start).toHaveBeenCalledTimes(3);
});
it('clears retries when the provider unmounts', async () => {
  fake.start.mockRejectedValue(new Error('offline'));
  await show();
  cleanup();
  await act(async () => { await vi.advanceTimersByTimeAsync(30000); });
  expect(fake.start).toHaveBeenCalledTimes(1);
});
it('reloads the endpoint list when a push mentions a workstation this browser has never seen', async () => {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  qc.setQueryData(['endpoints'], [{ id: 'a', name: 'A' }]);
  const invalidate = vi.spyOn(qc, 'invalidateQueries');
  await act(async () => { render(<QueryClientProvider client={qc}><RealtimeProvider>App</RealtimeProvider></QueryClientProvider>); });
  const push = fake.on.mock.calls.find((c) => c[0] === 'EndpointsUpdated')![1] as (l: unknown[]) => void;
  invalidate.mockClear();
  act(() => push([{ id: 'a', name: 'A2' }]));
  expect(invalidate).not.toHaveBeenCalledWith({ queryKey: ['endpoints'] });
  act(() => push([{ id: 'new', name: 'New PC' }]));
  expect(invalidate).toHaveBeenCalledWith({ queryKey: ['endpoints'] });
});
it('DiagnosisCompleted refreshes that run and the diagnosis history', async () => {
  const invalidate = await show();
  invalidate.mockClear();
  const handler = fake.on.mock.calls.find((c) => c[0] === 'DiagnosisCompleted')?.[1] as ((id: string) => void) | undefined;
  expect(handler).toBeDefined();
  handler?.('run-1');
  expect(invalidate).toHaveBeenCalledWith({ queryKey: ['diagnose-run', 'run-1'] });
  expect(invalidate).toHaveBeenCalledWith({ queryKey: ['diagnose-runs'] });
});

it('refreshes CPU history when a new sample arrives, avoiding a stale chart beside a current meter', async () => {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  qc.setQueryData(['endpoint', 'a'], { id: 'a', lastSeenUtc: '2026-10-15T09:00:00Z' });
  const invalidate = vi.spyOn(qc, 'invalidateQueries');
  await act(async () => { render(<QueryClientProvider client={qc}><RealtimeProvider>App</RealtimeProvider></QueryClientProvider>); });
  const push = fake.on.mock.calls.find(c => c[0] === 'EndpointsUpdated')![1] as (list: unknown[]) => void;
  invalidate.mockClear();
  act(() => push([{ id: 'a', lastSeenUtc: '2026-10-15T09:01:00Z', cpuPercent: 22 }]));
  expect(invalidate).toHaveBeenCalledWith({ queryKey: ['metrics', 'a'] });
  invalidate.mockClear();
  act(() => push([{ id: 'a', lastSeenUtc: '2026-10-15T09:01:00Z', cpuPercent: 22 }]));
  expect(invalidate).not.toHaveBeenCalledWith({ queryKey: ['metrics', 'a'] });
});
