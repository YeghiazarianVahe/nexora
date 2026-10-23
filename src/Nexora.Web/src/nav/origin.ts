import { GRID_CATEGORIES, OTHER_CATEGORY } from '../jobs/jobTypes';

/**
 * Routes and navigation helpers.
 *
 *   /endpoints                              list (filters and sort live in the query string)
 *   /endpoints/:host[/:tab]                 workstation (host name as stored by Nexora; a GUID still works and is replaced by the host form)
 *   /endpoints/:host/jobs/:jobId            a job seen from its workstation
 *   /endpoints/:host/diagnose/:runId        a diagnose run
 *   /jobs, /jobs/:id                        global jobs list and bulk jobs
 *
 * A legacy `?from=` value (carried by /jobs/:id) is user-controlled input: only a fixed set of in-app paths is ever accepted, so it cannot
 * become an open redirect.
 */

export const ORIGIN_PARAM = 'from';

export const ENDPOINT_TABS = ['overview', 'hardware', 'storage', 'actions', 'processes', 'services', 'profiles', 'jobs', 'monitoring', 'diagnose', 'logs'] as const;
export type EndpointTab = (typeof ENDPOINT_TABS)[number];

export const TAB_LABEL: Record<EndpointTab, string> = {
  hardware: 'Hardware', storage: 'Storage', monitoring: 'Monitoring', overview: 'Overview', processes: 'Processes', services: 'Services', profiles: 'Profiles', diagnose: 'Fast Diagnostics',
  jobs: 'Jobs', actions: 'Remote Actions', logs: 'Logs',
};

/** Tab names used by earlier versions, and the tab that replaces each. */
export const LEGACY_TABS: Readonly<Record<string, EndpointTab>> = { disks: 'storage' };

export const isEndpointTab = (t: string | undefined): t is EndpointTab => !!t && (ENDPOINT_TABS as readonly string[]).includes(t);

/** A current tab name, or the replacement of a legacy one, or null. */
export const normalizeTab = (t: string | undefined): EndpointTab | null => (isEndpointTab(t) ? t : t && Object.hasOwn(LEGACY_TABS, t) ? LEGACY_TABS[t] : null);

/** Route patterns of the workstation area; one list for the app and the tests. */
export const ENDPOINT_ROUTE_PATHS = {
  workstation: '/endpoints/:host',
  tab: '/endpoints/:host/:tab',
  actionsCategory: '/endpoints/:host/actions/:category',
  job: '/endpoints/:host/jobs/:jobId',
  diagnoseRun: '/endpoints/:host/diagnose/:runId',
} as const;

const GUID_SRC = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}';
const HOST_SRC = '[A-Za-z0-9][A-Za-z0-9._-]{0,252}';
const GUID_RE = new RegExp(`^${GUID_SRC}$`);
export const isGuid = (s: string) => GUID_RE.test(s);

const TAB_ALT = [...ENDPOINT_TABS, ...Object.keys(LEGACY_TABS)].join('|');
const WORKSTATION = new RegExp(`^/endpoints/(${GUID_SRC}|${HOST_SRC})(?:/(${TAB_ALT})(?:/([a-z-]+))?)?$`);
const BASE = 'http://nexora.invalid';

export type Origin = { kind: 'jobs'; path: '/jobs' } | { kind: 'workstation'; path: string; ref: string; tab: EndpointTab };

/** Returns the parsed origin when `raw` is an allowed in-app location, otherwise null. Never returns anything built from outside input beyond the matched path. */
export function parseOrigin(raw: string | null | undefined): Origin | null {
  if (typeof raw !== 'string' || raw.length === 0 || raw.length > 300) return null;
  // single leading slash only: rejects "//host", "/\host", "http://host", "javascript:" and control characters
  if (!raw.startsWith('/') || raw.startsWith('//') || /[\\\u0000-\u001f\u007f]/.test(raw)) return null;
  let url: URL;
  try { url = new URL(raw, BASE); } catch { return null; }
  if (url.origin !== BASE) return null;
  if (url.pathname === '/jobs') return { kind: 'jobs', path: '/jobs' };
  const m = WORKSTATION.exec(url.pathname);
  if (!m) return null;
  const tab = normalizeTab(m[2]) ?? 'overview';
  const category = m[3];
  if (category && (tab !== 'actions' || ![...GRID_CATEGORIES, OTHER_CATEGORY].includes(category))) return null;
  const filters = new URLSearchParams();
  if (tab === 'actions') {
    const query = url.searchParams.get('q');
    if (query && query.length <= 160) filters.set('q', query);
    const risk = (url.searchParams.get('risk') ?? '').split(',').filter(r => ['Low', 'Medium', 'High', 'Critical'].includes(r));
    if (risk.length) filters.set('risk', [...new Set(risk)].join(','));
  }
  return { kind: 'workstation', path: endpointPath(m[1], tab) + (category ? `/${category}` : '') + (filters.size ? `?${filters}` : ''), ref: m[1], tab };
}

/** The validated path to return to, or null. */
export const safeOrigin = (raw: string | null | undefined): string | null => parseOrigin(raw)?.path ?? null;

/** `/endpoints/<host>` or `/endpoints/<host>/<tab>`. The host (untrusted data) is always encoded. */
export const endpointPath = (host: string, tab?: EndpointTab) => `/endpoints/${encodeURIComponent(host)}${tab ? `/${tab}` : ''}`;

/** A job as seen from its workstation. `output` opens the output window on arrival. */
export function endpointJobPath(host: string, jobId: string, opts?: { output?: boolean; from?: string }): string {
  const q = new URLSearchParams();
  const origin = parseOrigin(opts?.from);
  if (origin?.kind === 'workstation' && origin.ref.toLowerCase() === host.toLowerCase()) q.set(ORIGIN_PARAM, origin.path);
  if (opts?.output) q.set('output', '1');
  return `${endpointPath(host)}/jobs/${encodeURIComponent(jobId)}${q.size ? `?${q}` : ''}`;
}

/** Remote Actions, optionally on one category tab: `/endpoints/<host>/actions[/<category>]`. */
export const endpointActionsPath = (host: string, category?: string) => `${endpointPath(host, 'actions')}${category ? `/${encodeURIComponent(category)}` : ''}`;

export const diagnoseRunPath = (host: string, runId: string) => `${endpointPath(host, 'diagnose')}/${encodeURIComponent(runId)}`;

/** Link to a bulk job's page; `from` (a workstation path or /jobs) is validated and only kept when it is a workstation. */
export function jobPath(jobId: string, from?: string | null, opts?: { output?: boolean }): string {
  const q = new URLSearchParams();
  const origin = safeOrigin(from);
  if (origin && origin !== '/jobs') q.set(ORIGIN_PARAM, origin);
  if (opts?.output) q.set('output', '1');
  const s = q.toString();
  return `/jobs/${encodeURIComponent(jobId)}${s ? `?${s}` : ''}`;
}

/** The page one level up. Pure: the list's remembered query string is added by `backTarget`. */
export function parentOf(pathname: string): string {
  const segs = pathname.split('/').filter(Boolean);
  if (segs[0] === 'endpoints') {
    if (segs.length === 1) return '/dashboard';
    if (segs.length === 4 && segs[2] === 'jobs') return `/endpoints/${segs[1]}`;
    if (segs.length === 4 && segs[2] === 'actions') return `/endpoints/${segs[1]}`;
    if (segs.length === 4 && segs[2] === 'diagnose') return `/endpoints/${segs[1]}/diagnose`;
    return '/endpoints';
  }
  if (segs[0] === 'jobs' && segs.length > 1) return '/jobs';
  return '/dashboard';
}

// ---- the list's query string, remembered so "Back" from a workstation restores filters and sort ----

const LIST_QUERY_KEY = 'nexora.endpoints.listQuery';
/** The only parameters the list understands; anything else is dropped when the saved query is read back. */
export const LIST_PARAMS = ['q', 'status', 'alert', 'health', 'group', 'maker', 'model', 'ou', 'sort', 'dir'] as const;

function cleanListQuery(raw: string): string {
  const src = new URLSearchParams(raw);
  const out = new URLSearchParams();
  for (const k of LIST_PARAMS) {
    const v = src.get(k);
    if (v && v.length <= 200) out.set(k, v);
  }
  return out.toString();
}

export function saveListQuery(search: string): void {
  try { window.sessionStorage.setItem(LIST_QUERY_KEY, cleanListQuery(search)); } catch { /* storage unavailable: Back still works, without the filters */ }
}

/** The saved query string including its leading "?", or ''. */
export function loadListQuery(): string {
  try {
    const s = cleanListQuery(window.sessionStorage.getItem(LIST_QUERY_KEY) ?? '');
    return s ? `?${s}` : '';
  } catch { return ''; }
}

/** Where the top Back button goes: the parent page, and for the list, the filters and sort the user left it with. */
export function backTarget(pathname: string): string {
  const parent = parentOf(pathname);
  return parent === '/endpoints' ? `${parent}${loadListQuery()}` : parent;
}
