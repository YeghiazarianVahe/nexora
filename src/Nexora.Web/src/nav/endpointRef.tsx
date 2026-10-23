import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { ApiError, get } from '../api';
import type { EndpointSummary } from '../types';
import { ErrorNotice } from '../ui';
import { backTarget, isGuid, parentOf } from './origin';

/** The API path that resolves a URL's workstation reference: by host name (any letter case), or by id for old links. The host is encoded. */
export const endpointApiPath = (ref: string) => (isGuid(ref) ? `/api/endpoints/${ref}` : `/api/endpoints/by-host/${encodeURIComponent(ref)}`);

/**
 * Resolves the `:host` of a URL to the workstation. The resolved record then lives under ['endpoint', id], where live updates and the
 * Refresh button already work. `ref` null = nothing to resolve.
 */
export function useEndpointByRef(ref: string | null) {
  const qc = useQueryClient();
  const resolved = useQuery({
    queryKey: ['endpoint-ref', ref],
    queryFn: async () => {
      const e = await get<EndpointSummary>(endpointApiPath(ref!));
      qc.setQueryData(['endpoint', e.id], e);
      return e;
    },
    enabled: !!ref, retry: false, staleTime: 30_000,
  });
  const id = resolved.data?.id;
  const live = useQuery({
    queryKey: ['endpoint', id ?? ''], queryFn: () => get<EndpointSummary>(`/api/endpoints/${id}`),
    enabled: !!id, initialData: resolved.data, staleTime: 30_000,
  });
  return { data: live.data ?? resolved.data, isLoading: !!ref && resolved.isLoading, error: resolved.error };
}

/** What the page says when a workstation, job or run cannot be shown. Never blank, always a way out. */
export function NotFound({ what, detail }: { what: string; detail?: string }) {
  return (
    <div className="notfound" role="alert">
      <h1>{what} not found</h1>
      {detail && <p className="muted">{detail}</p>}
      <p><Link to="/endpoints">Go to the workstation list</Link></p>
    </div>
  );
}

/** Not found (404), ambiguous name (409) or any other failure of resolving a workstation. */
export function EndpointLoadProblem({ error, host }: { error: unknown; host: string }) {
  if (error instanceof ApiError && error.status === 404) return <NotFound what="Workstation" detail={`No workstation is named "${host}".`} />;
  if (error instanceof ApiError && error.status === 409) {
    return (
      <div className="notfound" role="alert">
        <h1>More than one workstation matches</h1>
        <p className="muted">Two workstations are named "{host}" and differ only by letter case, so the name alone cannot open one of them. Open the right one from the list.</p>
        <p><Link to="/endpoints">Go to the workstation list</Link></p>
      </div>
    );
  }
  return <><p className="muted"><Link to="/endpoints">&larr; Workstations</Link></p><ErrorNotice error={error} /></>;
}

/** The top Back button: always the parent page (see parentOf), never history.back(). Browser back/forward are untouched. */
export function BackButton({ testId }: { testId?: string }) {
  const { pathname } = useLocation();
  const parent = parentOf(pathname);
  const label = parent === '/endpoints' ? 'Back to workstation list' : parent === '/jobs' ? 'Back to jobs' : parent.startsWith('/endpoints/') ? 'Back to workstation' : 'Back';
  return <Link to={backTarget(pathname)} className="back" aria-label={label} data-testid={testId}>&larr; Back</Link>;
}

/** Replaces the host in the URL with the real host name of the workstation (an old GUID link, or other letter case), keeping tab, query and fragment. */
export function useCanonicalHost(endpoint: EndpointSummary | undefined, hostParam: string) {
  const nav = useNavigate();
  const { pathname, search, hash } = useLocation();
  useEffect(() => {
    if (!endpoint || endpoint.hostname === hostParam) return;
    const segs = pathname.split('/');
    segs[2] = encodeURIComponent(endpoint.hostname);
    nav(`${segs.join('/')}${search}${hash}`, { replace: true });
  }, [endpoint, hostParam, pathname, search, hash, nav]);
}
