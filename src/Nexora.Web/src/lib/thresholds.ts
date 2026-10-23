import { useQuery } from '@tanstack/react-query';
import { get } from '../api';
import { METRIC_COLORS } from './format';

/** Alert and health thresholds, from Settings and the health evaluator (GET /api/thresholds). Every meter colours by these. */
export interface Thresholds {
  cpuAlertPercent: number;
  ramAlertPercent: number;
  diskAlertPercent: number;
  healthOkFrom: number;
  healthCriticalBelow: number;
  /** Per-class drive temperature limits (F4); may be absent on an older server. */
  diskTempHddWarnC?: number;
  diskTempHddCritC?: number;
  diskTempSsdWarnC?: number;
  diskTempSsdCritC?: number;
  diskTempNvmeWarnC?: number;
  diskTempNvmeCritC?: number;
}

/** Used until the server answers (and the server's own defaults): the original 70 / 90 bands. */
export const DEFAULT_THRESHOLDS: Thresholds = { cpuAlertPercent: 90, ramAlertPercent: 90, diskAlertPercent: 90, healthOkFrom: 85, healthCriticalBelow: 50 };

export type MetricKind = 'cpu' | 'ram' | 'disk';

/** Amber starts this far below the alert threshold, so the default 90 gives the familiar 70 / 90 bands. */
export const WARN_MARGIN = 20;

export function alertPercent(kind: MetricKind, t: Thresholds): number {
  return kind === 'cpu' ? t.cpuAlertPercent : kind === 'ram' ? t.ramAlertPercent : t.diskAlertPercent;
}

/** Usage meters: green below the amber band, amber from alert-20, red from the alert threshold. */
export function metricColorFor(value: number, kind: MetricKind, t: Thresholds): string {
  const bad = alertPercent(kind, t);
  if (value >= bad) return METRIC_COLORS.bad;
  if (value >= Math.max(0, bad - WARN_MARGIN)) return METRIC_COLORS.warn;
  return METRIC_COLORS.ok;
}

/** Health meters run the other way: OK from the evaluator's OK line, Critical below its critical line. */
export function healthColorFor(percent: number, t: Thresholds): string {
  if (percent < t.healthCriticalBelow) return METRIC_COLORS.bad;
  if (percent < t.healthOkFrom) return METRIC_COLORS.warn;
  return METRIC_COLORS.ok;
}

export function useThresholds(): Thresholds {
  const q = useQuery({ queryKey: ['thresholds'], queryFn: () => get<Thresholds>('/api/thresholds'), staleTime: 5 * 60_000, retry: 1 });
  return q.data ?? DEFAULT_THRESHOLDS;
}
