import type { EndpointOverview, EndpointSummary, HardwareInventory, OverviewLive } from '../types';

const isObj = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null);
const str = (v: unknown): string | null => (typeof v === 'string' && v !== '' ? v : null);

/** Validates the /overview payload at the boundary; anything malformed throws so the page shows an error instead of wrong numbers. */
export function parseOverview(raw: unknown): EndpointOverview {
  if (!isObj(raw) || typeof raw.online !== 'boolean') throw new Error('The server returned an unexpected overview response.');
  let live: OverviewLive | null = null;
  if (isObj(raw.live)) {
    const l = raw.live;
    live = {
      cpuLoadPercent: num(l.cpuLoadPercent), ramTotalBytes: num(l.ramTotalBytes), ramUsedBytes: num(l.ramUsedBytes),
      ramFreeBytes: num(l.ramFreeBytes), ramPercentUsed: num(l.ramPercentUsed), sampledUtc: str(l.sampledUtc) ?? '',
    };
  }
  const inventory = isObj(raw.inventory) ? (raw.inventory as unknown as HardwareInventory) : null;
  return {
    online: raw.online, lastSeenUtc: str(raw.lastSeenUtc), collectedUtc: str(raw.collectedUtc), stale: raw.stale === true,
    unavailableReason: str(raw.unavailableReason), inventory, live,
  };
}

/** Folds a SignalR EndpointsUpdated entry into a cached overview: live numbers and online state change, the inventory does not. */
export function applyLiveToOverview(old: EndpointOverview, e: EndpointSummary): EndpointOverview {
  const total = e.ramTotalBytes ?? old.live?.ramTotalBytes ?? null;
  const pct = e.ramPercent;
  const used = total != null && pct != null ? Math.round((total * pct) / 100) : null;
  return {
    ...old,
    online: e.online,
    lastSeenUtc: e.lastSeenUtc ?? old.lastSeenUtc,
    live: {
      cpuLoadPercent: e.cpuPercent, ramPercentUsed: pct, ramTotalBytes: total, ramUsedBytes: used,
      ramFreeBytes: total != null && used != null ? total - used : null,
      sampledUtc: e.lastSeenUtc ?? old.live?.sampledUtc ?? '',
    },
  };
}
