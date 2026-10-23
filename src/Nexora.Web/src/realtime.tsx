import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { currentToken } from './api';
import { useToasts } from './toasts';
import { applyLiveToOverview } from './lib/overview';
import type { EndpointOverview, EndpointSummary } from './types';

export type ConnState = 'connected' | 'reconnecting' | 'disconnected';

interface RealtimeValue {
  state: ConnState;
  /** Subscribe to a hub event for the lifetime of the calling component. */
  connection: HubConnection | null;
  /** Counts every time the connection is (re)established. Server-side group subscriptions die with the connection, so anything that
   * subscribed (a job's output, say) must subscribe again and re-read what it missed whenever this changes. */
  epoch: number;
}

const Ctx = createContext<RealtimeValue>({ state: 'disconnected', connection: null, epoch: 0 });
export const useRealtime = () => useContext(Ctx);

/**
 * One SignalR connection for the whole app. Events keep TanStack Query caches fresh, so pages
 * re-render from live data without polling.
 */
/** Subscribe to a hub event for as long as the calling component is mounted. */
export function useHubOn<A extends unknown[]>(event: string, handler: (...args: A) => void) {
  const { connection } = useRealtime();
  const ref = useRef(handler);
  useEffect(() => { ref.current = handler; });
  useEffect(() => {
    if (!connection) return;
    const fn = (...args: unknown[]) => ref.current(...(args as A));
    connection.on(event, fn);
    return () => connection.off(event, fn);
  }, [connection, event]);
}

export function RealtimeProvider({ children }: { children: ReactNode }) {
  const qc = useQueryClient();
  const toasts = useToasts();
  const [state, setState] = useState<ConnState>('disconnected');
  const [connection, setConnection] = useState<HubConnection | null>(null);
  const [epoch, setEpoch] = useState(0);
  const toastsRef = useRef(toasts);
  useEffect(() => { toastsRef.current = toasts; });

  useEffect(() => {
    let cancelled = false;
    let conn: HubConnection | null = null;
    let retry: number | undefined;

    const map = (s: HubConnectionState): ConnState =>
      s === HubConnectionState.Connected ? 'connected'
      : s === HubConnectionState.Reconnecting || s === HubConnectionState.Connecting ? 'reconnecting'
      : 'disconnected';

    (async () => {
      const token = await currentToken().catch(() => '');
      conn = new HubConnectionBuilder()
        .withUrl('/hubs/nexora', { headers: { 'X-XSRF-TOKEN': token } })
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .configureLogging(LogLevel.Warning)
        .build();

      // The server sends at most one message a second with the latest state of every endpoint that changed: apply them all in one cache write.
      conn.on('EndpointsUpdated', (list: EndpointSummary[]) => {
        const byId = new Map(list.map((e) => [e.id, e]));
        const cached = qc.getQueryData<EndpointSummary[]>(['endpoints']);
        // A workstation this browser has never seen (added by another operator) cannot be patched in: reload the list so pickers include it.
        if (cached && list.some((e) => !cached.some((x) => x.id === e.id))) qc.invalidateQueries({ queryKey: ['endpoints'] });
        qc.setQueryData<EndpointSummary[]>(['endpoints'], (old) => (old ? old.map((x) => byId.get(x.id) ?? x) : old));
        for (const e of list) {
          const previous = qc.getQueryData<EndpointSummary>(['endpoint', e.id]);
          qc.setQueryData(['endpoint', e.id], e);
          if (e.lastSeenUtc && e.lastSeenUtc !== previous?.lastSeenUtc)
            void qc.invalidateQueries({ queryKey: ['metrics', e.id] });
          // The Overview tab shows the same live numbers: patch its cache from the batch (no-op when that page was never opened).
          qc.setQueryData<EndpointOverview | undefined>(['overview', e.id], (old) => (old ? applyLiveToOverview(old, e) : old));
        }
      });
      conn.on('DashboardChanged', () => {
        qc.invalidateQueries({ queryKey: ['dashboard'] });
        qc.invalidateQueries({ queryKey: ['groups'] });
      });
      conn.on('AccessChanged', async () => {
        qc.invalidateQueries({ queryKey: ['me'] });
        // Hub groups (viewers, superadmins) are assigned at connect time from the role, so reconnect to pick up a new one.
        try {
          await conn!.stop();
          if (cancelled) return;
          await conn!.start();
          established();
        } catch { /* the retry loop / automatic reconnect takes over */ }
        setState(map(conn!.state));
      });
      conn.on('AccessRequestPending', () => qc.invalidateQueries({ queryKey: ['admin'] }));
      conn.on('RolesChanged', () => qc.invalidateQueries({ queryKey: ['admin'] }));
      conn.on('JobChanged', () => qc.invalidateQueries({ queryKey: ['jobs-list'] }));
      // Pages listen to these through useHubOn; a no-op here keeps the client quiet while no page is listening.
      // A diagnosis finished: re-read that run and the history lists (the 3 s poll on the run page is only the fallback).
      conn.on('DiagnosisCompleted', (runId: string) => {
        void qc.invalidateQueries({ queryKey: ['diagnose-run', runId] });
        void qc.invalidateQueries({ queryKey: ['diagnose-runs'] });
      });
      conn.on('JobLog', () => undefined);
      conn.on('JobTargetChanged', () => undefined);
      conn.on('JobFinished', (job: { id: string; actionName: string; status: string; targetCount: number; failed: number; timedOut: number }) => {
        const bad = job.status === 'Failed' || job.status === 'PartiallyFailed';
        toastsRef.current.push({
          tone: job.status === 'Succeeded' ? 'ok' : job.status === 'Cancelled' ? 'info' : bad ? 'bad' : 'warn',
          title: `${job.actionName}: ${job.status === 'PartiallyFailed' ? 'partially failed' : job.status.toLowerCase()}`,
          body: `${job.targetCount} workstation${job.targetCount === 1 ? '' : 's'}${job.failed + job.timedOut > 0 ? `, ${job.failed + job.timedOut} failed` : ''}`,
          href: `/jobs/${job.id}`,
        });
        // A service, process or profile job just finished: re-read what it changed.
        qc.invalidateQueries({ queryKey: ['profiles'] });
        qc.invalidateQueries({ queryKey: ['endpoint-detail'] });
        qc.invalidateQueries({ queryKey: ['endpoint-services'] });
        qc.invalidateQueries({ queryKey: ['dashboard'] });
      });
      const sync = () => conn && setState(map(conn.state));
      const established = () => {
        if (cancelled) return;
        sync();
        setEpoch((n) => n + 1);
        qc.invalidateQueries();
      };
      conn.onreconnecting(sync);
      conn.onclose(sync);
      conn.onreconnected(established);

      if (cancelled) return;
      setConnection(conn);
      // Automatic reconnect eventually stops. Keep a lifetime safety net for both initial
      // failures and exhausted reconnects; a slow start must not clear the retry timer.
      retry = window.setInterval(async () => {
        if (cancelled || !conn || conn.state !== HubConnectionState.Disconnected) return;
        try { await conn.start(); established(); } catch { /* retry on the next tick */ }
        if (!cancelled) sync();
      }, 10000);
      try {
        await conn.start();
        established();
      } catch { /* the lifetime retry loop covers initial failures */ }
      if (!cancelled) sync();
    })();

    return () => {
      cancelled = true;
      if (retry !== undefined) window.clearInterval(retry); // the loop must not outlive the provider
      void conn?.stop();
    };
  }, [qc]);

  return <Ctx.Provider value={{ state, connection, epoch }}>{children}</Ctx.Provider>;
}
