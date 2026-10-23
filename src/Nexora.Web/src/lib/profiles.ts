import type { ProfileDeleteConfirm, ProfileDeleteResult, ProfileRowDto, ProfilesResponse } from '../types';

const isObj = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null;
const str = (v: unknown): string | null => (typeof v === 'string' ? v : null);
const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null);

/** Defense in depth for cached/older API payloads. Deletion remains guarded by server and helper. */
export function visibleUserProfile(r: Pick<ProfileRowDto, 'sid' | 'account' | 'localPath'>): boolean {
  const leaf = r.localPath.replace(/[\\. ]+$/, '').split('\\').pop()?.toLowerCase() ?? '';
  const account = r.account?.split('\\').pop()?.trim().toLowerCase();
  return !/^S-1-5-(18|19|20|80(?:-.*)?|82(?:-.*)?)$/i.test(r.sid)
    && !/^S-1-5-21-\d+-\d+-\d+-(500|501|503|504)$/i.test(r.sid)
    && !/^[a-z]:\\windows\\/i.test(r.localPath)
    && !['default', 'default user', 'public', 'all users', 'defaultuser0', 'systemprofile', 'localservice', 'networkservice', 'default.migrated', 'administrator', 'system', 'local service', 'network service', 'wdagutilityaccount', 'defaultaccount'].includes(leaf)
    && !['system', 'local service', 'network service'].includes(account ?? '');
}

export const SOURCE_LABEL: Record<string, string> = {
  ProfileList: 'registry load/unload time',
  NtUserDat: 'NTUSER.DAT modified',
  Wmi: 'WMI',
};

/** Boundary validation: rows without a SID or path are dropped, unknown values become null. */
export function parseProfiles(raw: unknown): ProfilesResponse {
  const o = isObj(raw) ? raw : {};
  const items: ProfileRowDto[] = [];
  for (const r of Array.isArray(o.items) ? o.items : []) {
    if (!isObj(r)) continue;
    const sid = str(r.sid);
    if (!sid) continue;
    const src = str(r.lastUsedSource);
    items.push({
      sid, account: str(r.account), localPath: str(r.localPath) ?? '', loaded: r.loaded === true,
      sizeBytes: num(r.sizeBytes), sizeMeasuredUtc: str(r.sizeMeasuredUtc), lastUsedUtc: str(r.lastUsedUtc),
      lastUsedSource: src && src.trim() ? src : null,
      deletable: r.deletable === true, blockReason: str(r.blockReason), state: str(r.state),
    });
  }
  return { unavailable: o.unavailable === true, reason: str(o.reason), items };
}

export function parseDeleteConfirm(raw: unknown): ProfileDeleteConfirm {
  if (!isObj(raw)) throw new Error('Unexpected response from the server.');
  const token = str(raw.token);
  const expectedHostname = str(raw.expectedHostname);
  if (!token || !expectedHostname) throw new Error('Unexpected response from the server.');
  const items = (Array.isArray(raw.items) ? raw.items : []).filter(isObj).flatMap((i) => {
    const sid = str(i.sid);
    return sid ? [{ sid, account: str(i.account), path: str(i.path) ?? '', sizeBytes: num(i.sizeBytes) }] : [];
  });
  return { token, expectedHostname, count: num(raw.count) ?? items.length, items };
}

export function parseDeleteResult(raw: unknown): ProfileDeleteResult {
  if (!isObj(raw)) throw new Error('Unexpected response from the server.');
  const jobIds = (Array.isArray(raw.jobIds) ? raw.jobIds : []).filter((x): x is string => typeof x === 'string');
  return { batchId: str(raw.batchId) ?? '', jobIds };
}

/** Per-profile refusal reasons from a 400: `reasons: [{sid, reason}]`. Anything malformed is ignored. */
export function parseReasons(err: unknown): { sid: string; reason: string }[] {
  const body = isObj(err) && isObj((err as { body?: unknown }).body) ? ((err as { body: Record<string, unknown> }).body) : null;
  const list = body && Array.isArray(body.reasons) ? body.reasons : [];
  return list.filter(isObj).flatMap((r) => (typeof r.sid === 'string' && typeof r.reason === 'string' ? [{ sid: r.sid, reason: r.reason }] : []));
}

/** Label for a last-used source; a source this UI does not know is shown as its raw text. */
export const sourceLabel = (s: string): string => (Object.prototype.hasOwnProperty.call(SOURCE_LABEL, s) ? SOURCE_LABEL[s] : s);

/** Rows to select for "not used for N days": deletable ones with a KNOWN last use older than the cutoff. Unknown last use is never matched. */
export function staleDeletable(rows: ProfileRowDto[], days: number, nowMs: number): string[] {
  const cutoff = nowMs - days * 86_400_000;
  return rows.filter((r) => {
    if (!r.deletable || !r.lastUsedUtc) return false;
    const t = Date.parse(r.lastUsedUtc);
    return Number.isFinite(t) && t < cutoff;
  }).map((r) => r.sid);
}
