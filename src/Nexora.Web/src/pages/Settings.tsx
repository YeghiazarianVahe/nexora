import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { del, get, post, put } from '../api';
import { validateServicePattern } from '../lib/services';
import { roleRank, type Me, type PlatformSettings, type Script } from '../types';
import { ConfirmDialog, Dialog, ErrorNotice, Loading, fmtDate } from '../ui';

interface SettingsResponse {
  settings: PlatformSettings;
  features: { credentialOps: boolean };
}

const fields: { key: keyof PlatformSettings; label: string; min: number; max: number; hint: string }[] = [
  { key: 'pollIntervalSeconds', label: 'Polling interval (s)', min: 15, max: 3600, hint: 'How often every endpoint is polled over WMI' },
  { key: 'maxConcurrency', label: 'Max concurrent polls', min: 1, max: 256, hint: 'Default 32' },
  { key: 'cpuAlertPercent', label: 'CPU alert (%)', min: 1, max: 100, hint: '' },
  { key: 'ramAlertPercent', label: 'RAM alert (%)', min: 1, max: 100, hint: '' },
  { key: 'diskAlertPercent', label: 'Disk alert (%)', min: 1, max: 100, hint: 'Highest used percentage across fixed disks' },
  { key: 'offlineAfterFailures', label: 'Offline after N failed polls', min: 1, max: 20, hint: '' },
  { key: 'diskPollMinutes', label: 'Drive health check every (min)', min: 5, max: 1440, hint: 'SMART and temperature; changes slowly' },
  { key: 'metricRetentionDays', label: 'Keep CPU/RAM history (days)', min: 1, max: 90, hint: 'Older samples are purged hourly' },
];

export const TEMP_KEYS = ['diskTempHddWarnC', 'diskTempHddCritC', 'diskTempSsdWarnC', 'diskTempSsdCritC', 'diskTempNvmeWarnC', 'diskTempNvmeCritC'] as const;
export type TempKey = (typeof TEMP_KEYS)[number];
export type TempDraft = Record<TempKey, string>;
/** Shown until /api/thresholds answers; the server's own values win once loaded. */
const TEMP_FALLBACK: Record<TempKey, number> = {
  diskTempHddWarnC: 50, diskTempHddCritC: 55, diskTempSsdWarnC: 60, diskTempSsdCritC: 70, diskTempNvmeWarnC: 70, diskTempNvmeCritC: 80,
};
const TEMP_GROUPS: { name: string; warn: TempKey; crit: TempKey }[] = [
  { name: 'HDD', warn: 'diskTempHddWarnC', crit: 'diskTempHddCritC' },
  { name: 'SSD', warn: 'diskTempSsdWarnC', crit: 'diskTempSsdCritC' },
  { name: 'NVMe', warn: 'diskTempNvmeWarnC', crit: 'diskTempNvmeCritC' },
];

const tempDraftFrom = (src: unknown): TempDraft => {
  const o = (typeof src === 'object' && src !== null ? src : {}) as Record<string, unknown>;
  return Object.fromEntries(TEMP_KEYS.map((k) => [k, String(typeof o[k] === 'number' ? o[k] : TEMP_FALLBACK[k])])) as TempDraft;
};

/** Inline errors per box: integer 20..100 and warn < crit. Text state, so an empty box is only an error, never a 0. */
export function validateTemps(d: TempDraft): Partial<Record<TempKey, string>> {
  const errors: Partial<Record<TempKey, string>> = {};
  const num = (k: TempKey): number | null => {
    const t = d[k].trim();
    if (t === '') { errors[k] = 'Required (20-100)'; return null; }
    const n = Number(t);
    if (!Number.isInteger(n) || n < 20 || n > 100) { errors[k] = 'Whole number from 20 to 100'; return null; }
    return n;
  };
  for (const g of TEMP_GROUPS) {
    const w = num(g.warn);
    const c = num(g.crit);
    if (w != null && c != null && w >= c) errors[g.crit] = 'Critical must be higher than warning';
  }
  return errors;
}

export function DiskTempLimits({ draft, onChange }: { draft: TempDraft; onChange: (next: TempDraft) => void }) {
  const errors = validateTemps(draft);
  const box = (k: TempKey, label: string) => (
    <div className="form-row" key={k}>
      <label htmlFor={k}>{label}</label>
      <input id={k} type="text" inputMode="numeric" value={draft[k]} aria-invalid={errors[k] !== undefined} autoComplete="off"
        onChange={(e) => onChange({ ...draft, [k]: e.target.value })} />
      {errors[k] && <small role="alert" className="muted">{errors[k]}</small>}
    </div>
  );
  return (
    <section aria-labelledby="dtemp-h" style={{ margin: '16px 0' }}>
      <h2 id="dtemp-h">Disk temperature limits</h2>
      <p className="sub">Limits in °C, used when a drive does not report its own. NVMe drives that report their own limits use those. Temperature is shown next to the health score and never changes it.</p>
      {TEMP_GROUPS.map((g) => (
        <fieldset key={g.name} style={{ border: 0, padding: 0, margin: '8px 0' }}>
          <legend><b>{g.name}</b></legend>
          <div className="form-grid">{box(g.warn, `${g.name} warning (°C)`)}{box(g.crit, `${g.name} critical (°C)`)}</div>
        </fieldset>
      ))}
    </section>
  );
}

export function Settings({ me }: { me: Me }) {
  const isAdmin = roleRank[me.role] >= roleRank.Admin;
  const qc = useQueryClient();
  const thr = useQuery({ queryKey: ['thresholds'], queryFn: () => get<unknown>('/api/thresholds'), staleTime: 5 * 60_000, retry: 1 });
  const [tempDraft, setTempDraft] = useState<TempDraft>(() => tempDraftFrom(null));
  useEffect(() => { if (thr.data) setTempDraft(tempDraftFrom(thr.data)); }, [thr.data]);
  const tempProblems = isAdmin ? Object.keys(validateTemps(tempDraft)).length : 0;
  const q = useQuery({ queryKey: ['settings'], queryFn: () => get<SettingsResponse>('/api/settings') });
  const [form, setForm] = useState<PlatformSettings | null>(null);
  // What is typed in each box, as text: a number box that holds a number cannot be emptied (Number('') is 0) and then retyped.
  const [protectedList, setProtectedList] = useState<string[]>([]);
  const [draft, setDraft] = useState<Record<string, string>>({});
  useEffect(() => {
    if (!q.data) return;
    setForm(q.data.settings);
    setProtectedList(q.data.settings.protectedServices ?? []);
    setDraft(Object.fromEntries(fields.map((f) => [f.key, String(q.data.settings[f.key])])));
  }, [q.data]);
  const problems = fields.filter((f) => { const v = draft[f.key]; return v === undefined || v.trim() === '' || !Number.isFinite(Number(v)) || Number(v) < f.min || Number(v) > f.max; });

  const save = useMutation({
    mutationFn: (s: PlatformSettings) => put<PlatformSettings>('/api/settings', s),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: ['settings'] }); void qc.invalidateQueries({ queryKey: ['thresholds'] }); void qc.invalidateQueries({ queryKey: ['dashboard'] }); },
  });

  if (q.error) return <ErrorNotice error={q.error} />;
  if (q.isLoading || !form) return <Loading />;

  return (
    <>
      <h1>Settings</h1>
      <p className="sub">Monitoring cadence, alert thresholds and the approved-script catalog.</p>

      <div className="card">
        <div className="form-grid">
          {fields.map((f) => (
            <div className="form-row" key={f.key}>
              <label htmlFor={f.key}>{f.label}</label>
              <input id={f.key} type="text" inputMode="numeric" value={draft[f.key] ?? ''}
                aria-invalid={problems.includes(f)} onChange={(e) => setDraft({ ...draft, [f.key]: e.target.value })} />
              {f.hint && <small className="muted">{f.hint}</small>}
            </div>
          ))}
        </div>
        {isAdmin && <DiskTempLimits draft={tempDraft} onChange={setTempDraft} />}
        {isAdmin && <ProtectedServices floor={form.protectedServicesFloor ?? []} list={protectedList} onChange={setProtectedList} />}
        <ErrorNotice error={save.error} />
        {problems.length > 0 && <p className="muted" role="status">Check: {problems.map((f) => `${f.label} (${f.min}-${f.max})`).join(', ')}.</p>}
        <button className="primary" onClick={() => save.mutate({ ...form, ...Object.fromEntries(fields.map((f) => [f.key, Number(draft[f.key])])), protectedServices: protectedList, ...Object.fromEntries(TEMP_KEYS.map((k) => [k, Number(tempDraft[k])])) } as PlatformSettings)} disabled={save.isPending || problems.length > 0 || tempProblems > 0}>Save settings</button>
        {save.isSuccess && <span className="muted"> Saved.</span>}
      </div>

      <div className="notice">
        Credential operations (password reset): <b>{q.data?.features.credentialOps ? 'enabled' : 'disabled'}</b>.
        This is a server-side flag (<span className="mono">Features:CredentialOps</span>) that stays off while Nexora runs over plain HTTP.
      </div>

      <ScriptCatalog />
    </>
  );
}

/** Services nobody may stop or restart from Nexora: a fixed built-in floor plus names an Admin adds. Saved with the rest of the settings. */
export function ProtectedServices({ floor, list, onChange }: { floor: string[]; list: string[]; onChange: (next: string[]) => void }) {
  // Text state: validated on Add, not on every keystroke (audit L7).
  const [text, setText] = useState('');
  const [error, setError] = useState<string | null>(null);
  const add = () => {
    const problem = validateServicePattern(text, [...floor, ...list]);
    if (problem) { setError(problem); return; }
    onChange([...list, text.trim()]);
    setText('');
    setError(null);
  };
  return (
    <section aria-labelledby="prot-h" style={{ margin: '16px 0' }}>
      <h2 id="prot-h">Protected services</h2>
      <p className="sub">Protected services cannot be stopped or restarted from Nexora on any workstation. Names are case-insensitive. Click Save settings to apply changes.</p>
      <h3>Built-in (cannot be changed)</h3>
      <ul aria-label="Built-in protected services" className="mono">{floor.length === 0 ? <li className="muted">None reported</li> : floor.map((n) => <li key={n}>{n}</li>)}</ul>
      <h3>Added by an Admin</h3>
      <ul aria-label="Added protected services">
        {list.length === 0 && <li className="muted">None</li>}
        {list.map((n) => (
          <li key={n}><span className="mono">{n}</span> <button className="small" aria-label={`Remove ${n}`} onClick={() => onChange(list.filter((x) => x !== n))}>Remove</button></li>
        ))}
      </ul>
      <div className="form-row">
        <label htmlFor="prot-add">Service name</label>
        <input id="prot-add" type="text" value={text} aria-invalid={error !== null} autoComplete="off" spellCheck={false} maxLength={256}
          onChange={(e) => { setText(e.target.value); setError(null); }}
          onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); add(); } }} />
        {error && <small role="alert" className="muted">{error}</small>}
      </div>
      <button onClick={add}>Add</button>
    </section>
  );
}

function ScriptCatalog() {
  const qc = useQueryClient();
  const scripts = useQuery({ queryKey: ['scripts'], queryFn: () => get<Script[]>('/api/scripts') });
  const [editing, setEditing] = useState<Partial<Script> | null>(null);
  const [deleting, setDeleting] = useState<Script | null>(null);
  const refresh = () => qc.invalidateQueries({ queryKey: ['scripts'] });

  const saveScript = useMutation({
    mutationFn: (s: Partial<Script>) => {
      const body = { name: s.name, description: s.description ?? '', body: s.body, enabled: s.enabled ?? true };
      return s.id ? put<Script>(`/api/scripts/${s.id}`, body) : post<Script>('/api/scripts', body);
    },
    onSuccess: () => { refresh(); setEditing(null); },
  });
  const remove = useMutation({
    mutationFn: (id: string) => del<void>(`/api/scripts/${id}`),
    onSuccess: () => { refresh(); setDeleting(null); },
  });

  return (
    <>
      <h2>Approved script catalog</h2>
      <p className="sub">Operators can run only these scripts. Each is pinned by SHA-256, and the helper refuses a script whose hash does not match.</p>
      <div className="toolbar">
        <button onClick={() => setEditing({ enabled: true, name: '', description: '', body: '' })}>New script</button>
      </div>
      <ErrorNotice error={scripts.error} />
      <div className="tablewrap">
        <table>
          <thead><tr><th>Name</th><th>Description</th><th>SHA-256</th><th>Enabled</th><th>Updated</th><th /></tr></thead>
          <tbody>
            {scripts.data?.length === 0 && <tr><td colSpan={6} className="empty">No scripts yet.</td></tr>}
            {scripts.data?.map((s) => (
              <tr key={s.id}>
                <td><b>{s.name}</b></td><td>{s.description}</td><td className="mono">{s.sha256.slice(0, 12)}...</td>
                <td><span className={`tag ${s.enabled ? 'ok' : ''}`}>{s.enabled ? 'Yes' : 'No'}</span></td>
                <td>{fmtDate(s.updatedUtc)}</td>
                <td>
                  <button className="small" onClick={() => setEditing(s)}>Edit</button>{' '}
                  <button className="small danger" onClick={() => setDeleting(s)}>Delete</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {editing && (
        <Dialog title={editing.id ? 'Edit script' : 'New script'} onClose={() => setEditing(null)}>
          <div className="form-row"><label htmlFor="sn">Name</label>
            <input id="sn" value={editing.name ?? ''} onChange={(e) => setEditing({ ...editing, name: e.target.value })} /></div>
          <div className="form-row"><label htmlFor="sd">Description</label>
            <input id="sd" value={editing.description ?? ''} onChange={(e) => setEditing({ ...editing, description: e.target.value })} /></div>
          <div className="form-row"><label htmlFor="sb">PowerShell (runs on the target through Invoke-Command)</label>
            <textarea id="sb" value={editing.body ?? ''} onChange={(e) => setEditing({ ...editing, body: e.target.value })} /></div>
          <label><input type="checkbox" checked={editing.enabled ?? true} onChange={(e) => setEditing({ ...editing, enabled: e.target.checked })} /> Enabled</label>
          <ErrorNotice error={saveScript.error} />
          <div className="actions">
            <button onClick={() => setEditing(null)}>Cancel</button>
            <button className="primary" disabled={saveScript.isPending} onClick={() => saveScript.mutate(editing)}>Save</button>
          </div>
        </Dialog>
      )}
      {deleting && (
        <ConfirmDialog title="Delete script" danger message={<>Delete <b>{deleting.name}</b>? Operators will no longer be able to run it.</>}
          confirmLabel="Delete" busy={remove.isPending} onConfirm={() => remove.mutate(deleting.id)} onCancel={() => setDeleting(null)} />
      )}
    </>
  );
}
