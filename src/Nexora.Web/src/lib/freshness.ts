import type { EndpointSummary } from '../types';

export type Freshness = 'live' | 'stale' | 'offline' | 'unavailable';

/** How long after the last successful poll the numbers stop being "live". */
export const STALE_AFTER_MS = 5 * 60_000;

/**
 * live        online and polled recently
 * stale       online flag still set but the last successful poll is old (monitoring is behind)
 * offline     the PC does not answer: the numbers are the last known ones
 * unavailable it was never read successfully
 */
export function dataFreshness(e: Pick<EndpointSummary, 'online' | 'lastSeenUtc'>, now = Date.now()): Freshness {
  if (!e.lastSeenUtc) return 'unavailable';
  if (!e.online) return 'offline';
  return now - Date.parse(e.lastSeenUtc) > STALE_AFTER_MS ? 'stale' : 'live';
}

export const FRESHNESS_TEXT: Record<Freshness, string> = {
  live: 'Live',
  stale: 'Stale: the last successful sample is old',
  offline: 'Offline: showing the last known values',
  unavailable: 'Unavailable: this workstation has not been read yet',
};

export interface Gap { fromMs: number; toMs: number }

/**
 * Periods without samples. A workstation that cannot be reached produces no sample, so a long hole in the series is the only record of an
 * outage in the stored history (it says "no data", not why).
 */
export function findGaps(times: number[], factor = 3, minMs = 5 * 60_000): Gap[] {
  if (times.length < 3) return [];
  const diffs = times.slice(1).map((t, i) => t - times[i]).filter((d) => d > 0).sort((a, b) => a - b);
  if (diffs.length === 0) return [];
  const median = diffs[Math.floor(diffs.length / 2)];
  const limit = Math.max(median * factor, minMs);
  const gaps: Gap[] = [];
  for (let i = 1; i < times.length; i++) if (times[i] - times[i - 1] > limit) gaps.push({ fromMs: times[i - 1], toMs: times[i] });
  return gaps;
}

export function formatDuration(ms: number): string {
  const m = Math.round(ms / 60_000);
  if (m < 60) return `${m} min`;
  const h = Math.floor(m / 60);
  return h < 48 ? `${h} h ${m % 60} min` : `${Math.round(h / 24)} days`;
}
