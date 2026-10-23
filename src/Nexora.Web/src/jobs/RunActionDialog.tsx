import { useQuery } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { get, post } from '../api';
import { useToasts } from '../toasts';
import { roleRank, type Me } from '../types';
import { ConfirmDialog, ErrorNotice } from '../ui';
import { RiskBadge } from './JobParts';
import { parseDependents, validateServicePattern } from '../lib/services';
import { actionLabel, riskRank, type CatalogEntry, type JobDetailDto, type RiskLevel, type ParamInfo, type PreflightResult } from './jobTypes';

export interface TargetRef { id: string; name: string; hostname: string }

type Values = Record<string, string | boolean>;

function initial(params: ParamInfo[]): Values {
  const v: Values = {};
  for (const p of params) v[p.name] = p.type === 'Bool' ? p.default === 'true' : (p.default ?? '');
  return v;
}

/** Turns the form values into typed JSON for the API (numbers, booleans, lists); empty optional fields are left out. */
export function toParameters(params: ParamInfo[], values: Values): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const p of params) {
    const raw = values[p.name];
    if (p.type === 'Bool') { out[p.name] = !!raw; continue; }
    const text = String(raw ?? '').trim();
    if (text === '') continue;
    if (p.type === 'Int') out[p.name] = Number(text);
    else if (p.type === 'StringList') out[p.name] = text.split(/[\s,;]+/).filter(Boolean);
    else out[p.name] = text;
  }
  return out;
}

/** Low-risk actions with nothing to fill in run at once: no dialog. (The server still checks role, targets and parameters.) */
export const runsImmediately = (e: CatalogEntry) =>
  !e.needsConfirmation && !e.needsReason && !e.checksProtected && e.parameters.every((p) => !p.required);

interface Props {
  /** Action Center always previews target and parameters, including low-risk actions. */
  review?: boolean;
  entry: CatalogEntry; targets: TargetRef[]; me: Me; onClose: () => void; onCreated: (jobId: string) => void;
  preset?: Record<string, unknown>; presetReason?: string;
}

/**
 * Starts one catalog action on one or more inventory workstations. Low risk runs immediately; everything above goes through the
 * shared ConfirmDialog with what the risk level asks for. The server validates everything again (and issues the confirm token
 * only for what was confirmed here); this form only collects input.
 */
export function RunActionDialog(props: Props) {
  return !props.review && runsImmediately(props.entry) ? <RunNow {...props} /> : <ConfirmRun {...props} />;
}

function RunNow({ entry, targets, onClose, onCreated, preset }: Props) {
  const toasts = useToasts();
  const started = useRef(false);
  useEffect(() => {
    if (started.current) return;
    started.current = true;
    const values = initial(entry.parameters);
    for (const [k, v] of Object.entries(preset ?? {})) values[k] = typeof v === 'boolean' ? v : String(v);
    post<JobDetailDto>('/api/jobs', { actionCode: entry.code, endpointIds: targets.map((t) => t.id), parameters: toParameters(entry.parameters, values), overrideProtected: false })
      .then((job) => onCreated(job.job.id))
      .catch((e: unknown) => { toasts.push({ tone: 'bad', title: `${actionLabel(entry.code, entry.displayName)} did not start`, body: e instanceof Error ? e.message : String(e) }); onClose(); });
  }, [entry, targets, preset, onCreated, onClose, toasts]);
  return null;
}

function ConfirmRun({ entry, targets, me, onClose, onCreated, preset, presetReason }: Props) {
  const [values, setValues] = useState<Values>(() => {
    const v = initial(entry.parameters);
    for (const [k, val] of Object.entries(preset ?? {})) v[k] = typeof val === 'boolean' ? val : Array.isArray(val) ? val.join(', ') : String(val);
    return v;
  });
  const [reason, setReason] = useState(presetReason ?? '');
  const [override, setOverride] = useState(false);
  // Deleting a profile is confirmed by typing the ACCOUNT NAME of that profile (the server checks it against what the PC reports), not the workstation name.
  const byAccount = entry.code === 'profile.delete';
  const [account, setAccount] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const isAdmin = roleRank[me.role] >= roleRank.Admin;
  const bulk = targets.length > 1;
  const set = (name: string, v: string | boolean) => setValues((x) => ({ ...x, [name]: v }));

  const pre = useQuery({
    queryKey: ['preflight', entry.code, targets.map((t) => t.id).join(',')],
    queryFn: () => post<PreflightResult>('/api/jobs/preflight', { actionCode: entry.code, endpointIds: targets.map((t) => t.id) }),
    enabled: bulk, retry: 0, staleTime: 0,
  });

  // Stop / Restart of a service: the server says which services depend on it; those need an explicit opt-in (the server enforces it too).
  // The dependents shown always follow the service name that will actually be sent (parameter serviceName), never a separate copy.
  const svcName = String(values.serviceName ?? '').trim();
  const svcDeps = (entry.code === 'svc.stop' || entry.code === 'svc.restart') && !bulk;
  const [stopChoice, setStopChoice] = useState<{ name: string; on: boolean }>({ name: '', on: false });
  // A hand-typed name is looked up only after the typing pauses, and only when it is a plausible service name (no query per keystroke).
  const [settledName, setSettledName] = useState(svcName);
  useEffect(() => {
    if (svcName === settledName) return;
    const t = setTimeout(() => setSettledName(svcName), 400);
    return () => clearTimeout(t);
  }, [svcName, settledName]);
  const nameReady = svcName !== '' && settledName === svcName && validateServicePattern(svcName, []) === null;
  const stopDependents = stopChoice.on && stopChoice.name === svcName;
  const setStopDependents = (on: boolean) => setStopChoice({ name: svcName, on });
  useEffect(() => { setStopChoice({ name: svcName, on: false }); }, [svcName]); // a new name starts from a fresh, unticked choice
  const deps = useQuery({
    queryKey: ['svc-dependents', targets[0]?.id, settledName],
    queryFn: async () => parseDependents(await get<unknown>(`/api/endpoints/${targets[0].id}/services/${encodeURIComponent(settledName)}/dependents`)),
    enabled: svcDeps && nameReady, retry: 0, staleTime: 0, gcTime: 0,
  });
  const runningDeps = deps.data?.running ?? [];
  const depsBlock = svcDeps && (!nameReady || deps.isLoading || deps.isError || !deps.data || deps.data.unavailable || !!deps.data.anyProtected || (runningDeps.length > 0 && !stopDependents));
  const serverRisk = svcDeps ? (deps.data?.effectiveRisk ?? (runningDeps.length > 0 ? 'High' : null)) : null; // no server value: stopping dependents is at least High
  const preRisk = pre.data?.effectiveRisk ?? null;
  const effectiveRisk = [entry.risk, serverRisk, preRisk].reduce<RiskLevel>((a, r) => (r && riskRank[r] > riskRank[a] ? r : a), entry.risk);

  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      const body = {
        actionCode: entry.code, endpointIds: targets.map((t) => t.id),
        parameters: { ...toParameters(entry.parameters, values), ...(svcDeps ? { stopDependents } : {}) },
        reason: reason.trim() || null, overrideProtected: override,
      };
      let confirmToken: string | null = null;
      if (entry.needsConfirmation) {
        // The typed text was already compared in the dialog; the server compares it again before it issues the token.
        const c = await post<{ token: string }>('/api/jobs/confirm', { ...body, typed: byAccount ? account.trim() : bulk ? String(targets.length) : targets[0].hostname });
        confirmToken = c.token;
      }
      const job = await post<JobDetailDto>('/api/jobs', { ...body, confirmToken });
      onCreated(job.job.id);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const reasonNeeded = entry.needsReason || riskRank[effectiveRisk] >= riskRank.High || (override && entry.checksProtected);
  const missingParam = entry.parameters.some((p) => p.required && p.type !== 'Bool' && String(values[p.name] ?? '').trim() === '');
  const protectedBlock = !!pre.data?.overrideNeeded && !(isAdmin && override);
  const preflightPending = bulk && (pre.isLoading || pre.isError);
  const blocked = (reasonNeeded && reason.trim().length === 0) || missingParam || protectedBlock || preflightPending || depsBlock || (byAccount && account.trim().length === 0);
  const danger = effectiveRisk === 'High' || effectiveRisk === 'Critical';
  const needsTyped = entry.needsTypedConfirmation || effectiveRisk !== entry.risk;
  const firstParam = entry.parameters.find((p) => p.type !== 'Bool')?.name;

  return (
    <ConfirmDialog
      title={actionLabel(entry.code, entry.displayName)} danger={danger} confirmLabel="Run" busy={busy} confirmDisabled={blocked}
      requireText={needsTyped && !byAccount ? (bulk ? String(targets.length) : targets[0].hostname) : undefined}
      typedLabel={bulk ? <>Type the number of workstations (<b>{targets.length}</b>) to confirm</> : <>Type the hostname <b>{targets[0].hostname}</b> to confirm</>}
      onConfirm={submit} onCancel={onClose}
      message={
        <>
          <p className="action-head"><RiskBadge risk={effectiveRisk} />{entry.command && <span className="acmd mono" title="The command this runs">{entry.command}</span>}</p>
          <p className="muted">{entry.description}</p>
          <p>
            {bulk
              ? <>Targets: <b>{targets.length} workstations</b> <span className="muted">({targets.slice(0, 3).map((t) => t.name).join(', ')}{targets.length > 3 ? ', ...' : ''})</span></>
              : <>Target: <b>{targets[0].name}</b> <span className="mono muted">{targets[0].hostname}</span></>}
            {' '}<span className="muted">· runs as <b>{me.account}</b>, through your helper</span>
          </p>

          {bulk && <Preflight query={pre} />}

          {byAccount && (
            <div className="form-row">
              <label htmlFor="profile-account">Type the <b>account name</b> of the profile to confirm</label>
              <input id="profile-account" value={account} onChange={(ev) => setAccount(ev.target.value)} autoComplete="off" spellCheck={false} placeholder="for example old.user" />
              <p className="muted">If the account no longer exists, type the profile folder name instead.</p>
            </div>
          )}

          {svcDeps && <Dependents ready={nameReady} query={deps} stop={stopDependents} onStop={setStopDependents} />}

          {entry.parameters.filter((p) => !(svcDeps && p.name === 'stopDependents')).map((p) => (
            <div className="form-row" key={p.name}>
              {p.type === 'Bool' ? (
                <label className="inline"><input type="checkbox" checked={!!values[p.name]} onChange={(e) => set(p.name, e.target.checked)} /> {p.label}</label>
              ) : (
                <>
                  <label htmlFor={`p-${p.name}`}>{p.label}{p.required && ' *'}</label>
                  {p.type === 'Enum' ? (
                    <select id={`p-${p.name}`} value={String(values[p.name])} onChange={(e) => set(p.name, e.target.value)} data-autofocus={p.name === firstParam ? '' : undefined}>
                      {p.allowed?.map((a) => <option key={a}>{a}</option>)}
                    </select>
                  ) : (
                    <input id={`p-${p.name}`} value={String(values[p.name])} type={p.type === 'Int' ? 'number' : 'text'}
                      min={p.min ?? undefined} max={p.max ?? undefined} maxLength={p.type === 'Int' ? undefined : p.maxLength}
                      data-autofocus={p.name === firstParam ? '' : undefined}
                      readOnly={p.name === 'serviceName' && typeof preset?.serviceName === 'string'}
                      onChange={(e) => set(p.name, e.target.value)} />
                  )}
                </>
              )}
              {p.help && <small className="muted">{p.help}</small>}
            </div>
          ))}

          <div className="form-row">
            <label htmlFor="reason">Reason{reasonNeeded ? ' *' : ' (optional)'}</label>
            <textarea id="reason" style={{ minHeight: 64 }} value={reason} maxLength={500} onChange={(e) => setReason(e.target.value)}
              data-autofocus={firstParam ? undefined : ''} placeholder="Why is this needed? It is written to the audit log." />
          </div>

          {isAdmin && entry.checksProtected && (
            <label className="inline"><input type="checkbox" checked={override} onChange={(e) => setOverride(e.target.checked)} /> Override protected workstations (servers, the Nexora server, PCs whose operating system is not known yet). Needs a reason.</label>
          )}
          {targets.length > 10 && riskRank[entry.risk] >= riskRank.High && <div className="notice warn">This runs on {targets.length} workstations at once.</div>}
          <ErrorNotice error={error} />
        </>
      }
    />
  );
}

function Dependents({ ready, query, stop, onStop }: { ready: boolean; query: { isLoading: boolean; isError: boolean; data?: ReturnType<typeof parseDependents>; error: unknown }; stop: boolean; onStop: (v: boolean) => void }) {
  if (!ready) return <div className="notice" role="status">Enter a valid service name to check which services depend on it.</div>;
  if (query.isLoading) return <div className="notice" role="status">Checking dependent services...</div>;
  if (query.isError || !query.data || query.data.unavailable) return <div className="notice error" role="alert">Could not check dependent services, so this action is blocked. {query.data?.reason}<ErrorNotice error={query.error} /></div>;
  const d = query.data;
  if (d.anyProtected) return <div className="notice error" role="alert">Blocked: a protected service depends on this one, so it cannot be stopped or restarted from Nexora.</div>;
  if (d.running.length === 0) return <p className="muted">No running services depend on this one.</p>;
  return (
    <div className="notice warn" role="group" aria-label="Dependent services">
      <p><b>{d.running.length} running service{d.running.length === 1 ? '' : 's'} depend on this one</b> and would stop with it:</p>
      <ul>{d.running.map((n) => <li key={n} className="mono">{n}</li>)}</ul>
      <label className="inline"><input type="checkbox" checked={stop} onChange={(e) => onStop(e.target.checked)} /> Also stop dependent services ({d.running.length})</label>
      {!stop && <p className="muted">Without this, the action is blocked.</p>}
    </div>
  );
}

/** Before a bulk run: how many targets are reachable, offline, protected or busy. */
function Preflight({ query }: { query: { isLoading: boolean; isError: boolean; data?: PreflightResult; error: unknown; refetch: () => unknown } }) {
  if (query.isLoading) return <div className="notice" role="status">Checking the targets...</div>;
  if (query.isError || !query.data) {
    return <div className="notice error" role="alert"><ErrorNotice error={query.error} /> <button onClick={() => query.refetch()}>Try again</button></div>;
  }
  const d = query.data;
  const list = (names: string[]) => `${names.slice(0, 5).join(', ')}${names.length > 5 ? `, +${names.length - 5} more` : ''}`;
  return (
    <div className="preflight" role="status" aria-label="Pre-flight check">
      <span className="tag ok">{d.reachable} of {d.total} reachable</span>
      {d.offline.length > 0 && <span className="tag warn" title={d.offline.join('\n')}>{d.offline.length} offline: {list(d.offline)}</span>}
      {d.protected.length > 0 && <span className="tag bad" title={d.protected.map((p) => `${p.name} (${p.reason})`).join('\n')}>{d.protected.length} protected: {list(d.protected.map((p) => p.name))}</span>}
      {d.busy.length > 0 && <span className="tag warn" title={d.busy.join('\n')}>{d.busy.length} busy: {list(d.busy)}</span>}
      {d.protected.length > 0 && <div className="muted">Protected workstations need an Admin override with a reason.</div>}
    </div>
  );
}
