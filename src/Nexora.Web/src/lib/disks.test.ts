import { describe, expect, it } from 'vitest';
import type { Volume } from '../types';
import { formatBytes, formatPair, hddHealth, ramUsedBytes, ssdHealth, systemVolume, usedPercent, volumeUsedPercent } from './disks';
import { DEFAULT_THRESHOLDS, healthColorFor, metricColorFor } from './thresholds';

const GB = 1024 ** 3;

describe('formatting', () => {
  it('shows GB or TB with one decimal', () => {
    expect(formatBytes(0)).toBe('0.0 GB');
    expect(formatBytes(120.4 * GB)).toBe('120.4 GB');
    expect(formatBytes(475 * GB)).toBe('475.0 GB');
    expect(formatBytes(1023.9 * GB)).toBe('1023.9 GB');
    expect(formatBytes(1024 * GB)).toBe('1.0 TB');
    expect(formatBytes(1.8 * 1024 * GB)).toBe('1.8 TB');
    expect(formatBytes(null)).toBe('—');
  });

  it('formats a free / total pair in one unit', () => {
    expect(formatPair(120 * GB, 475 * GB)).toBe('120.0 / 475.0 GB');
    expect(formatPair(500 * GB, 2048 * GB)).toBe('0.5 / 2.0 TB');
    expect(formatPair(null, 1)).toBe('—');
  });

  it('computes used percent from free and total', () => {
    expect(usedPercent(125, 500)).toBe(75);
    expect(usedPercent(0, 100)).toBe(100);
    expect(usedPercent(100, 100)).toBe(0);
    expect(usedPercent(1, 3)).toBe(66.7);
    expect(usedPercent(5, 0)).toBeNull();
    expect(usedPercent(null, 10)).toBeNull();
  });

  it('derives RAM in use from the percentage and the installed amount', () => {
    expect(ramUsedBytes({ ramPercent: 50, ramTotalBytes: 16 * GB })).toBe(8 * GB);
    expect(ramUsedBytes({ ramPercent: null, ramTotalBytes: 16 * GB })).toBeNull();
    expect(ramUsedBytes({ ramPercent: 50, ramTotalBytes: null })).toBeNull();
  });
});

describe('SSD and HDD health', () => {
  it('reads the server-computed worst-of fields from the list row, null when absent', () => {
    expect([ssdHealth({ ssdHealthPercent: 96 }), hddHealth({ hddHealthPercent: 100 })]).toEqual([96, 100]);
    expect([ssdHealth({ ssdHealthPercent: null }), hddHealth({ hddHealthPercent: null })]).toEqual([null, null]);
  });
});

describe('volumes', () => {
  const c: Volume = { letter: 'C:', fileSystem: 'NTFS', totalBytes: 500, freeBytes: 125 };
  const d: Volume = { letter: 'D:', fileSystem: 'NTFS', totalBytes: 1000, freeBytes: 900 };
  it('the system drive is C: or else the first volume', () => {
    expect(systemVolume({ volumes: [d, c] })).toBe(c);
    expect(systemVolume({ volumes: [d] })).toBe(d);
    expect(systemVolume({ volumes: [] })).toBeNull();
    expect(systemVolume({ volumes: null })).toBeNull();
    expect(volumeUsedPercent(c)).toBe(75);
  });
});

describe('colours follow the thresholds', () => {
  it('usage meters use the Settings alert threshold, amber 20 below it', () => {
    expect(metricColorFor(69, 'cpu', DEFAULT_THRESHOLDS)).toBe('#69BA31');
    expect(metricColorFor(70, 'cpu', DEFAULT_THRESHOLDS)).toBe('#E5A50A');
    expect(metricColorFor(90, 'cpu', DEFAULT_THRESHOLDS)).toBe('#E5484D');
    const strict = { ...DEFAULT_THRESHOLDS, cpuAlertPercent: 80, ramAlertPercent: 95 };
    expect(metricColorFor(80, 'cpu', strict)).toBe('#E5484D');
    expect(metricColorFor(60, 'cpu', strict)).toBe('#E5A50A');
    expect(metricColorFor(90, 'ram', strict)).toBe('#E5A50A');
    expect(metricColorFor(95, 'ram', strict)).toBe('#E5484D');
  });

  it('health meters use the evaluator lines', () => {
    expect(healthColorFor(85, DEFAULT_THRESHOLDS)).toBe('#69BA31');
    expect(healthColorFor(84, DEFAULT_THRESHOLDS)).toBe('#E5A50A');
    expect(healthColorFor(50, DEFAULT_THRESHOLDS)).toBe('#E5A50A');
    expect(healthColorFor(49, DEFAULT_THRESHOLDS)).toBe('#E5484D');
  });
});
