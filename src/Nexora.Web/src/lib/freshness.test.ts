import { describe, expect, it } from 'vitest';
import { dataFreshness, findGaps, STALE_AFTER_MS } from './freshness';

const now = Date.parse('2026-10-14T12:00:00Z');
const ago = (ms: number) => new Date(now - ms).toISOString();

describe('dataFreshness', () => {
  it('distinguishes live, stale, offline and unavailable', () => {
    expect(dataFreshness({ online: true, lastSeenUtc: ago(30_000) }, now)).toBe('live');
    expect(dataFreshness({ online: true, lastSeenUtc: ago(STALE_AFTER_MS + 1000) }, now)).toBe('stale');
    expect(dataFreshness({ online: false, lastSeenUtc: ago(1000) }, now)).toBe('offline');
    expect(dataFreshness({ online: false, lastSeenUtc: null }, now)).toBe('unavailable');
    expect(dataFreshness({ online: true, lastSeenUtc: null }, now)).toBe('unavailable');
  });
});

describe('findGaps', () => {
  const min = 60_000;
  it('finds holes much larger than the normal interval', () => {
    const t = [0, 1, 2, 3, 40, 41, 42].map((m) => m * min);
    expect(findGaps(t)).toEqual([{ fromMs: 3 * min, toMs: 40 * min }]);
  });
  it('reports nothing for an even series or too little data', () => {
    expect(findGaps([0, 1, 2, 3, 4, 5].map((m) => m * min))).toEqual([]);
    expect(findGaps([0, 60_000])).toEqual([]);
  });
  it('ignores small jitter below the minimum gap', () => {
    expect(findGaps([0, 1, 2, 6, 7].map((m) => m * min))).toEqual([]);
  });
});
