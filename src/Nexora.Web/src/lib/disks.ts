import type { EndpointSummary, Volume } from '../types';

const GB = 1024 ** 3;
const TB = 1024 ** 4;

/** Bytes as GB or TB with one decimal, the way Windows reports them (1 GB = 2^30 bytes). */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null || !Number.isFinite(bytes)) return '—';
  return bytes >= TB ? `${(bytes / TB).toFixed(1)} TB` : `${(bytes / GB).toFixed(1)} GB`;
}

/** "120.4 / 475.8 GB": one unit for a pair, chosen by the total. */
export function formatPair(part: number | null | undefined, total: number | null | undefined): string {
  if (part == null || total == null) return '—';
  const unit = total >= TB ? TB : GB;
  return `${(part / unit).toFixed(1)} / ${(total / unit).toFixed(1)} ${unit === TB ? 'TB' : 'GB'}`;
}

/** Used share of a volume or of RAM, 0-100 with one decimal; null when the total is unknown. */
export function usedPercent(free: number | null | undefined, total: number | null | undefined): number | null {
  if (free == null || total == null || !Number.isFinite(free) || !Number.isFinite(total) || total <= 0 || free < 0 || free > total) return null;
  return Math.round((1 - free / total) * 1000) / 10;
}

export const HDS_LABEL = 'Health (heuristic estimate)';
export const HDS_TOOLTIP = 'Calculated independently by Nexora from available SMART and NVMe evidence. Confidence reflects data coverage, not a guaranteed error bound. Estimates can differ from vendor tools.';

/** Sum check: 100 + sum(delta) must equal the score, else the equation footer is not shown. */
export function reasonsTotal(reasons: { delta: number }[], score: number | null): { sum: number; holds: boolean } {
  const sum = reasons.reduce((a, r) => a + r.delta, 0);
  return { sum, holds: score != null && 100 + sum === score };
}

export const signed = (n: number): string => (n > 0 ? `+${n}` : String(n));

export type DriveType ='SSD' | 'HDD' | 'Unknown';

/** Worst SSD / NVMe health of a workstation, from its list row. The server classifies the drives (see DriveClassifier). */
export const ssdHealth = (e: Pick<EndpointSummary, 'ssdHealthPercent'>): number | null => e.ssdHealthPercent ?? null;
export const hddHealth = (e: Pick<EndpointSummary, 'hddHealthPercent'>): number | null => e.hddHealthPercent ?? null;

/** The system drive: C: when present, else the first fixed volume. */
export function systemVolume(e: Pick<EndpointSummary, 'volumes'>): Volume | null {
  const v = e.volumes ?? [];
  return v.find((x) => x.letter.toUpperCase() === 'C:') ?? v[0] ?? null;
}

export const volumeUsedPercent = (v: Volume | null): number | null => (v ? usedPercent(v.freeBytes, v.totalBytes) : null);

/** RAM in use, from the percentage and the installed amount. */
export function ramUsedBytes(e: Pick<EndpointSummary, 'ramPercent' | 'ramTotalBytes'>): number | null {
  return e.ramPercent != null && e.ramTotalBytes != null ? Math.round((e.ramPercent / 100) * e.ramTotalBytes) : null;
}

export const volumeLine = (v: Volume) => `${v.letter} ${v.fileSystem ?? ''} ${formatBytes(v.freeBytes)} free of ${formatBytes(v.totalBytes)}`.replace(/\s+/g, ' ');
