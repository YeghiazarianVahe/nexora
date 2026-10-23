import { useQuery } from '@tanstack/react-query';
import { ApiError, get } from '../../api';
import { roleRank, type EndpointDetail, type EndpointSummary, type Me } from '../../types';
import { ErrorNotice } from '../../ui';

/** The live /detail read (processes, software, event log) is Operator-only on the server; the UI does not even ask a Viewer. */
export const canReadLiveDetails = (me: Me) => roleRank[me.role] >= roleRank.Operator;

export const isForbidden = (error: unknown) => error instanceof ApiError && error.status === 403;

export function OperatorRequired({ what = 'live details' }: { what?: string }) {
  return <div className="notice" role="status" data-testid="operator-required">Operator role required for {what}.</div>;
}

/** Shows the friendly notice for a 403, the plain error otherwise. */
export function LiveError({ error }: { error: unknown }) {
  return isForbidden(error) ? <OperatorRequired /> : <ErrorNotice error={error} />;
}

/** One query for the live detail of a workstation, shared by the Processes, Overview (software) and Logs sections. */
export function useLiveDetail(e: EndpointSummary, me: Me, asked: boolean) {
  return useQuery({
    queryKey: ['endpoint-detail', e.id],
    queryFn: () => get<EndpointDetail>(`/api/endpoints/${e.id}/detail`),
    enabled: asked && e.online && canReadLiveDetails(me),
    staleTime: 60_000,
    retry: (n, err) => !isForbidden(err) && n < 1,
  });
}
