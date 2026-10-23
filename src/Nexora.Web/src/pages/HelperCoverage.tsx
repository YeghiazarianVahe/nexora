import { useQuery } from '@tanstack/react-query';
import { get } from '../api';
import { formatMinute } from '../lib/format';
import { ErrorNotice, Loading } from '../ui';

interface PcRow { pc: string; state: 'connected' | 'not connected' | 'never seen'; sid: string | null; account: string | null; lastSeenUtc: string | null; nameVerified: boolean; configured: boolean }
interface Coverage { total: number; connected: number; notConnected: number; neverSeen: number; pcs: PcRow[] }

const when = (iso: string | null) => (iso ? formatMinute(iso) : '—');

/** Admin page: which operator PCs have a helper connected, as whom, and since when. The PC list is Nexora:OperatorPcs in appsettings. */
export function HelperCoverage() {
  const q = useQuery({ queryKey: ['helper-coverage'], queryFn: () => get<Coverage>('/api/helper-coverage'), refetchInterval: 15000, retry: 0 });
  if (q.isLoading) return <Loading />;
  if (q.error || !q.data) return <ErrorNotice error={q.error} />;
  const c = q.data;
  return (
    <>
      <h1>Helper coverage</h1>
      <p className="sub">Operator PCs and their helper. Seen since the server started; a helper reconnects by itself within seconds. Add a PC to <code>Nexora:OperatorPcs</code> to track it.</p>
      <div className="tiles">
        <div className="tile"><div className="n">{c.total}</div><div className="l">Operator PCs</div></div>
        <div className={`tile ${c.connected === c.total ? 'accent' : ''}`}><div className="n" data-testid="cov-connected">{c.connected}</div><div className="l">Connected</div></div>
        <div className={`tile ${c.notConnected > 0 ? 'alert' : ''}`}><div className="n">{c.notConnected}</div><div className="l">Not connected</div></div>
        <div className={`tile ${c.neverSeen > 0 ? 'bad' : ''}`}><div className="n">{c.neverSeen}</div><div className="l">Never seen</div></div>
      </div>
      <div className="tablewrap">
        <table>
          <thead><tr><th>PC</th><th>Helper</th><th>Operator account</th><th>Operator SID</th><th>Last seen</th></tr></thead>
          <tbody>
            {c.pcs.map((p) => (
              <tr key={p.pc} data-testid={`cov-${p.pc}`}>
                <td><b>{p.pc}</b>{!p.configured && <span className="tag warn" title="Not in Nexora:OperatorPcs"> not on the list</span>}</td>
                <td><span className={`tag ${p.state === 'connected' ? 'ok' : p.state === 'never seen' ? 'bad' : 'warn'}`}>{p.state}</span>
                  {p.state !== 'never seen' && !p.nameVerified && <span className="muted" title="The helper reported this name; the connecting address does not resolve back to it."> unverified name</span>}</td>
                <td>{p.account ?? '—'}</td>
                <td className="num" style={{ fontSize: '0.8em' }}>{p.sid ?? '—'}</td>
                <td className="num">{when(p.lastSeenUtc)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
