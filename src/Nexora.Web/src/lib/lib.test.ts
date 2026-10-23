// @vitest-environment jsdom
import { describe, expect, it } from 'vitest';
import {
  METRIC_COLORS, exportLabel, formatAbsolute, formatOu, formatRelative, isStale, metricColor, uptimeText,
} from './format';
import { addMany, emptySelection, headerState, hiddenCount, removeMany, toggle, toggleHeader } from './selection';

describe('formatOu', () => {
  it('keeps the last two segments and drops the domain-level root', () => {
    expect(formatOu('CONTOSO/BRANCHES/BranchA/Workstations')).toBe('BranchA › Workstations');
  });
  it('handles short paths and empty values', () => {
    expect(formatOu('Computers')).toBe('Computers');
    expect(formatOu('Head Office/IT')).toBe('Head Office › IT');
    expect(formatOu('')).toBeNull();
    expect(formatOu(null)).toBeNull();
    expect(formatOu('//')).toBeNull();
  });
  it('trims stray spaces', () => {
    expect(formatOu(' A / B / C ')).toBe('B › C');
  });
});

describe('metricColor', () => {
  it.each([
    [0, METRIC_COLORS.ok], [69.9, METRIC_COLORS.ok], [70, METRIC_COLORS.warn], [74, METRIC_COLORS.warn],
    [89.9, METRIC_COLORS.warn], [90, METRIC_COLORS.bad], [100, METRIC_COLORS.bad],
  ])('%s%% is %s', (value, color) => expect(metricColor(value)).toBe(color));

  it('uses the agreed colors', () => {
    expect(METRIC_COLORS).toEqual({ ok: '#69BA31', warn: '#E5A50A', bad: '#E5484D' });
  });
});

describe('time formatting', () => {
  const now = Date.parse('2026-10-12T12:00:00Z');
  it('is relative', () => {
    expect(formatRelative('2026-10-12T11:59:40Z', now)).toBe('just now');
    expect(formatRelative('2026-10-12T11:58:00Z', now)).toBe('2 min ago');
    expect(formatRelative('2026-10-12T09:00:00Z', now)).toBe('3 h ago');
    expect(formatRelative('2026-10-09T12:00:00Z', now)).toBe('3 d ago');
    expect(formatRelative(null, now)).toBe('—');
  });
  it('shows the absolute time as dd.MM.yyyy HH:mm:ss in Yerevan (UTC+4)', () => {
    expect(formatAbsolute('2026-10-12T11:44:06Z')).toBe('12.10.2026 15:44:06');
    expect(formatAbsolute('2026-10-12T20:00:00Z')).toBe('13.10.2026 00:00:00');
  });
  it('flags online endpoints that went quiet for more than 15 minutes', () => {
    expect(isStale(true, '2026-10-12T11:40:00Z', now)).toBe(true);
    expect(isStale(true, '2026-10-12T11:50:00Z', now)).toBe(false);
    expect(isStale(false, '2026-10-12T08:00:00Z', now)).toBe(false);
  });
  it('formats uptime', () => {
    expect(uptimeText(5 * 3600 + 53 * 60)).toBe('5h 53m');
    expect(uptimeText(2 * 86400 + 3 * 3600)).toBe('2d 3h');
    expect(uptimeText(null)).toBe('—');
  });
});

describe('exportLabel', () => {
  it('says what will be exported in each state', () => {
    expect(exportLabel({ selectedCount: 3, filteredCount: 128, totalCount: 412, filtersActive: true })).toBe('Export 3 selected');
    expect(exportLabel({ selectedCount: 0, filteredCount: 128, totalCount: 412, filtersActive: true })).toBe('Export 128 filtered');
    expect(exportLabel({ selectedCount: 0, filteredCount: 412, totalCount: 412, filtersActive: false })).toBe('Export all 412');
  });
});

describe('selection by id', () => {
  const rows = [{ id: 'a' }, { id: 'b' }, { id: 'c' }, { id: 'd' }, { id: 'e' }];
  const ids = (list: { id: string }[]) => list.map((r) => r.id);

  it('keeps the same three ids after sorting and after a live update replaces the row objects', () => {
    let sel = emptySelection;
    for (const id of ['b', 'd', 'e']) sel = toggle(sel, id, true);

    const sorted = [...rows].sort((x, y) => y.id.localeCompare(x.id));           // user sorts descending
    const live = sorted.map((r) => ({ ...r, cpu: Math.random() })).reverse();     // SignalR delta: new objects, new order

    expect([...sel].sort()).toEqual(['b', 'd', 'e']);
    expect(headerState(sel, ids(live))).toBe('some');
    expect(hiddenCount(sel, ids(live))).toBe(0);
  });

  it('header checkbox selects the visible rows, shows indeterminate for a partial selection, and clears them', () => {
    const visible = ['a', 'b', 'c'];
    expect(headerState(emptySelection, visible)).toBe('none');
    const partial = toggle(emptySelection, 'a', true);
    expect(headerState(partial, visible)).toBe('some');
    const all = toggleHeader(partial, visible);
    expect(headerState(all, visible)).toBe('all');
    expect(headerState(toggleHeader(all, visible), visible)).toBe('none');
  });

  it('keeps selected rows that a filter hides, and counts them', () => {
    const sel = addMany(emptySelection, ['a', 'b', 'x']);
    const filteredView = ['a', 'b', 'c'];
    expect(hiddenCount(sel, filteredView)).toBe(1);
    expect(removeMany(sel, ['a']).has('x')).toBe(true);
  });

  it('never mutates the previous selection', () => {
    const before = addMany(emptySelection, ['a']);
    toggle(before, 'b', true);
    expect([...before]).toEqual(['a']);
  });
});

import { formatModel } from './format';

describe('formatModel', () => {
  it.each([
    ['HP', 'HP t540 Thin Client', 'HP t540 Thin Client'],
    ['Hewlett-Packard', 'HP t620 Dual Core TC', 'HP t620 Dual Core TC'],
    ['HP', 't620 Dual Core TC', 'HP t620 Dual Core TC'],
    ['Dell Inc.', 'Vostro 3030', 'Dell Inc. Vostro 3030'],
    ['Dell Inc.', 'Dell Inc. Vostro', 'Dell Inc. Vostro'],
    ['ASUS', 'System Product Name', 'ASUS System Product Name'],
    [null, 'Vostro 3030', 'Vostro 3030'],
    ['Dell Inc.', null, 'Dell Inc.'],
    [null, null, ''],
  ])('%s + %s => %s', (maker, model, expected) => expect(formatModel(maker, model)).toBe(expected));
});

describe('prune (M11)', () => {
  it('drops ids that no longer exist and keeps the same set when nothing changed', async () => {
    const { prune } = await import('./selection');
    const sel = new Set(['a', 'b', 'gone']);
    expect([...prune(sel, new Set(['a', 'b', 'c']))].sort()).toEqual(['a', 'b']);
    const same = new Set(['a']);
    expect(prune(same, new Set(['a', 'b']))).toBe(same);
  });
});

describe('dates are dd.MM.yyyy HH:mm in Asia/Yerevan whatever the browser says (M8)', () => {
  it('formats in Yerevan time on a 24 hour clock', async () => {
    const { formatMinute, formatClock, formatDayClock } = await import('./format');
    expect(formatMinute('2026-10-13T10:05:00Z')).toBe('13.10.2026 14:05');
    expect(formatMinute('2026-10-12T20:30:00Z')).toBe('13.10.2026 00:30'); // midnight is 00, not 24
    expect(formatClock(Date.parse('2026-10-13T10:05:00Z'))).toBe('14:05');
    expect(formatDayClock(Date.parse('2026-10-13T10:05:00Z'))).toBe('13.10 14:05');
    expect(formatMinute(null)).toBe('');
  });
  it('fmtDate uses it and shows a dash when there is no date', async () => {
    const { fmtDate } = await import('../ui');
    expect(fmtDate('2026-10-13T10:05:00Z')).toBe('13.10.2026 14:05');
    expect(fmtDate(null)).toBe('-');
  });
});

describe('useSort follows the live data behind its keys (M13)', () => {
  it('sorts again when a key changes even though the rows array is the same', async () => {
    const { renderHook } = await import('@testing-library/react');
    const { useSort } = await import('../ui');
    const rows = [{ n: 'a' }, { n: 'b' }, { n: 'c' }];
    let size: Record<string, number> = { a: 1, b: 2, c: 3 };
    const { result, rerender } = renderHook(() => useSort(rows, { size: (r) => size[r.n] }, { key: 'size', dir: 'desc' }));
    expect(result.current.sorted.map((r) => r.n)).toEqual(['c', 'b', 'a']);
    const first = result.current.sorted;

    rerender();
    expect(result.current.sorted).toBe(first); // nothing changed: the very same array

    size = { a: 9, b: 2, c: 3 }; // a measurement arrives; rows and sort are untouched
    rerender();
    expect(result.current.sorted.map((r) => r.n)).toEqual(['a', 'c', 'b']);
  });
});
