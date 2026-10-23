import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createContext, useContext, useEffect, useRef, useState } from 'react';
import { get } from '../../api';
import type { MetricPoint, OverviewLive } from '../../types';

interface Reply { endpointId: string; hostname: string; sample: OverviewLive }
export interface PerformanceView { sample: OverviewLive | null; samples: OverviewLive[]; status: 'connecting' | 'live' | 'paused' | 'unavailable' }
export const LivePerformanceContext = createContext<PerformanceView>({ sample: null, samples: [], status: 'connecting' });
export const useLivePerformance = () => useContext(LivePerformanceContext);

export function useOnScreenPerformance(id: string | undefined, hostname: string | undefined): PerformanceView {
  const [visible, setVisible] = useState(() => document.visibilityState !== 'hidden');
  const [history, setHistory] = useState<{ id: string; hostname: string; samples: OverviewLive[] }>({ id: '', hostname: '', samples: [] });
  const qc = useQueryClient();
  const readDuration = useRef(0);
  useEffect(() => {
    const changed = () => setVisible(document.visibilityState !== 'hidden');
    document.addEventListener('visibilitychange', changed);
    return () => document.removeEventListener('visibilitychange', changed);
  }, []);
  const q = useQuery({
    queryKey: ['live-performance', id, hostname],
    queryFn: async ({ signal }) => {
      const started = performance.now();
      try { return await get<Reply>(`/api/endpoints/${id}/performance`, signal); }
      finally { readDuration.current = Math.max(0, performance.now() - started); }
    },
    enabled: !!id && !!hostname && visible,
    refetchInterval: query => query.state.error ? 5_000 : Math.max(50, 1_000 - readDuration.current),
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: true,
    staleTime: 0,
    gcTime: 30_000,
    retry: false,
  });
  useEffect(() => {
    if (!visible) void qc.cancelQueries({ queryKey: ['live-performance', id, hostname], exact: true });
  }, [visible, qc, id, hostname]);
  const reply = q.data;
  const matches = reply?.endpointId === id && reply?.hostname.toLowerCase() === hostname?.toLowerCase();
  const sample = reply && matches && !q.isError && Date.now() - Date.parse(reply.sample.sampledUtc) < 5_000 ? reply.sample : null;
  useEffect(() => {
    if (!sample || !id || !hostname) return;
    setHistory(old => {
      const previous = old.id === id && old.hostname === hostname ? old.samples : [];
      if (previous.at(-1)?.sampledUtc === sample.sampledUtc) return old;
      return { id, hostname, samples: [...previous.filter(s => s.sampledUtc !== sample.sampledUtc), sample].slice(-120) };
    });
  }, [sample, id, hostname]);
  return {
    sample,
    samples: history.id === id && history.hostname === hostname ? history.samples : [],
    status: !visible ? 'paused' : q.isError ? 'unavailable' : sample ? 'live' : 'connecting',
  };
}

/** Recent local one-second samples supplement history without increasing database writes. */
export function mergePerformanceHistory(stored: MetricPoint[], samples: OverviewLive[]): MetricPoint[] {
  if (!samples.length) return stored;
  const merged = new Map<number, MetricPoint>(stored.map(p => [Date.parse(p.timeUtc), p]));
  for (const s of samples)
    merged.set(Date.parse(s.sampledUtc), { timeUtc: s.sampledUtc, cpu: s.cpuLoadPercent, ram: s.ramPercentUsed, disk: null });
  return [...merged.entries()].filter(([time]) => Number.isFinite(time)).sort(([a], [b]) => a - b).map(([, point]) => point);
}
