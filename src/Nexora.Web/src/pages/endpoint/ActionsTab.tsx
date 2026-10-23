import { useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { Link, Navigate, useNavigate, useSearchParams } from 'react-router-dom';
import { ActionIcon, CategoryIcon } from '../../jobs/actionIcons';
import { ActionLabel } from '../../jobs/ActionLabel';
import { useCatalog } from '../../jobs/catalog';
import { HelperBanner } from '../../jobs/helper';
import {
  CATEGORY_LABEL, DEFAULT_CATEGORY, GRID_CATEGORIES, NON_GRID_CATEGORIES, OTHER_CATEGORY, OTHER_LABEL, type CatalogEntry, type RiskLevel,
} from '../../jobs/jobTypes';
import { RiskBadge } from '../../jobs/JobParts';
import { RunActionDialog } from '../../jobs/RunActionDialog';
import { endpointActionsPath, endpointPath } from '../../nav/origin';
import { useOnJobStarted } from './startedJob';
import type { EndpointSummary, Me } from '../../types';
import { ErrorNotice, Loading } from '../../ui';

/** What running this action will ask of the operator, in words (the server enforces all of it again). */
export function requirements(a: CatalogEntry): string[] {
  const r: string[] = [];
  if (a.needsTypedConfirmation) r.push('Type the workstation name');
  else if (a.needsConfirmation) r.push('Confirmation');
  if (a.needsReason) r.push('Reason required');
  if (a.checksProtected) r.push('Blocked on protected PCs');
  if (a.heavy) r.push('One heavy job at a time');
  if (a.requiresUserNotice) r.push('Warns the signed-in user');
  if (a.parameters.some((p) => p.required)) r.push('Needs input');
  if (!a.supportsCancel) r.push('Cannot be cancelled once running');
  return r;
}

export const matchesAction = (a: CatalogEntry, q: string) => {
  const t = q.trim().toLowerCase();
  return !t || [a.displayName, a.description, a.command ?? '', a.code].some((x) => x.toLowerCase().includes(t));
};

const RISKS: readonly RiskLevel[] = ['Low', 'Medium', 'High', 'Critical'];
const isGridCategory = (c: string) => (GRID_CATEGORIES as readonly string[]).includes(c);
/** The tab an action belongs to: its own category when known, otherwise "Other". */
const tabOf = (a: CatalogEntry): string => (isGridCategory(a.category) ? a.category : OTHER_CATEGORY);
const labelOf = (c: string) => (c === OTHER_CATEGORY ? OTHER_LABEL : CATEGORY_LABEL[c] ?? c);

/**
 * The Action Center: one tab per category (the active one lives in the URL), search across all categories, a risk filter, and one card per action.
 * Starting an action opens the shared run dialog, then its output opens in place.
 */
export function ActionsTab({ e, me, category }: { e: EndpointSummary; me: Me; category?: string }) {
  const onStarted = useOnJobStarted();
  const catalog = useCatalog();
  const nav = useNavigate();
  const [params, setParams] = useSearchParams();
  const [picked, setPicked] = useState<CatalogEntry | null>(null);
  const tabRefs = useRef(new Map<string, HTMLButtonElement>());

  const query = params.get('q') ?? '';
  const risks = useMemo(() => new Set((params.get('risk') ?? '').split(',').filter((r): r is RiskLevel => (RISKS as readonly string[]).includes(r))), [params]);
  const qs = params.toString() ? `?${params.toString()}` : '';
  const searching = query.trim().length > 0;

  // Grid actions only: services / processes / profiles live in their own tabs and are never listed here.
  const entries = useMemo(
    () => (catalog.data ?? []).filter((a) => a.implemented && !NON_GRID_CATEGORIES.includes(a.category)),
    [catalog.data],
  );
  const hasOther = entries.some((a) => tabOf(a) === OTHER_CATEGORY);
  const tabs = useMemo(() => [...GRID_CATEGORIES, ...(hasOther ? [OTHER_CATEGORY] : [])] as string[], [hasOther]);

  const matching = useMemo(
    () => entries.filter((a) => matchesAction(a, query) && (risks.size === 0 || risks.has(a.risk))),
    [entries, query, risks],
  );
  const counts = useMemo(() => {
    const c: Record<string, number> = {};
    for (const a of matching) c[tabOf(a)] = (c[tabOf(a)] ?? 0) + 1;
    return c;
  }, [matching]);

  if (category === undefined) return <Navigate to={`${endpointActionsPath(e.hostname, DEFAULT_CATEGORY)}${qs}`} replace />;
  if (catalog.isLoading) return <Loading label="Loading actions" />;
  if (catalog.error) return <ErrorNotice error={catalog.error} />;
  if (!tabs.includes(category)) return <Navigate to={`${endpointActionsPath(e.hostname, DEFAULT_CATEGORY)}${qs}`} replace />;

  const go = (c: string) => nav(`${endpointActionsPath(e.hostname, c)}${qs}`);
  const update = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value); else next.delete(key);
    setParams(next, { replace: true });
  };
  const toggleRisk = (r: RiskLevel) => {
    const next = new Set(risks);
    if (!next.delete(r)) next.add(r);
    update('risk', RISKS.filter((x) => next.has(x)).join(','));
  };

  const onKeyDown = (ev: KeyboardEvent<HTMLButtonElement>) => {
    const i = tabs.indexOf(category);
    const to = ev.key === 'ArrowRight' || ev.key === 'ArrowDown' ? (i + 1) % tabs.length
      : ev.key === 'ArrowLeft' || ev.key === 'ArrowUp' ? (i - 1 + tabs.length) % tabs.length
        : ev.key === 'Home' ? 0 : ev.key === 'End' ? tabs.length - 1 : -1;
    if (to < 0) return;
    ev.preventDefault();
    tabRefs.current.get(tabs[to])?.focus();
    go(tabs[to]);
  };

  // Searching spans every category; otherwise only the active tab is listed.
  const groups = tabs
    .filter((c) => searching || c === category)
    .map((c) => [c, matching.filter((a) => tabOf(a) === c)] as const)
    .filter(([, l]) => l.length > 0);
  const filtered = searching || risks.size > 0;

  return (
    <>
      <div className="console-section-title"><div><h2>Action Center</h2><p className="sub">Target <b>{e.hostname}</b> · executing as <b>{me.account}</b> through your helper.</p></div><span className="tag">Review before execution</span></div>
      <HelperBanner />
      {!e.online && <div className="notice warn">This workstation is offline; jobs will fail to reach it.</div>}

      <div className="ac ac-tabs">
        <div className="ac-filters">
          <div className="ac-search">
            <input type="search" placeholder="Search all actions (name, command, description)" aria-label="Search actions" value={query} onChange={(ev) => update('q', ev.target.value)} />
          </div>
          <div className="risks" role="group" aria-label="Filter by risk">
            {RISKS.map((r) => <button key={r} type="button" aria-pressed={risks.has(r)} onClick={() => toggleRisk(r)}>{r}</button>)}
          </div>
        </div>

        <div className="ac-tabbar" role="tablist" aria-label="Action categories">
          {tabs.map((c) => (
            <button key={c} type="button" role="tab" id={`ac-tab-${c}`} aria-selected={c === category} aria-controls="ac-panel" tabIndex={c === category ? 0 : -1}
              ref={(el) => { if (el) tabRefs.current.set(c, el); else tabRefs.current.delete(c); }} onClick={() => go(c)} onKeyDown={onKeyDown}>
              <CategoryIcon category={c} /><span className="lbl">{labelOf(c)}</span><span className="cnt" aria-label={`${counts[c] ?? 0} actions`}>{counts[c] ?? 0}</span>
            </button>
          ))}
        </div>

        <nav className="ac-related" aria-label="Managed actions">
          <Link to={endpointPath(e.hostname, 'services')}>Services, Processes &amp; Software</Link>
          <Link to={endpointPath(e.hostname, 'processes')}>Processes</Link>
          <Link to={endpointPath(e.hostname, 'hardware')}>Installed software</Link>
          <Link to={endpointPath(e.hostname, 'profiles')}>Profiles</Link>
        </nav>

        <div className="ac-main" role="tabpanel" id="ac-panel" aria-labelledby={`ac-tab-${category}`}>
          {groups.length === 0 && (
            <div className="empty" role="status">
              {filtered ? 'No action matches your search or filter.' : entries.length === 0 ? 'No actions are available.' : 'No actions in this category.'}
            </div>
          )}
          {groups.map(([c, list]) => (
            <section key={c} aria-label={labelOf(c)} className="ac-group">
              {searching && (
                <h3 className="ac-title">{labelOf(c)}<span className="ac-hint">in the {labelOf(c)} tab</span></h3>
              )}
              <ul className="ac-cards">
                {list.map((a) => {
                  const reqs = requirements(a);
                  return (
                    <li key={a.code}>
                      <button className={`ac-card ${a.allowed ? '' : 'locked'}`} data-testid="action-card" disabled={!a.allowed} onClick={() => setPicked(a)}
                        aria-label={a.allowed ? `Run ${a.displayName}` : `${a.displayName}, requires ${a.minRole}`}>
                        <span className="ac-ico"><ActionIcon code={a.code} category={a.category} /></span>
                        <span className="ac-body">
                          <span className="ac-top"><ActionLabel name={a.displayName} command={a.command} /> <RiskBadge risk={a.risk} /></span>
                          <span className="ac-desc">{a.description}</span>
                          <span className="ac-meta">
                            <span className={`perm ${a.allowed ? 'yes' : 'no'}`}>{a.allowed ? 'You can run this' : `Requires ${a.minRole}`}</span>
                            {a.parameters.filter(p => p.required).map(p => <span key={`param-${p.name}`} className="req">{p.label} required</span>)}
                            {reqs.map((r) => <span key={r} className="req">{r}</span>)}
                          </span>
                        </span>
                      </button>
                    </li>
                  );
                })}
              </ul>
            </section>
          ))}
        </div>
      </div>

      {picked && (
        <RunActionDialog review entry={picked} targets={[{ id: e.id, name: e.name, hostname: e.hostname }]} me={me} onClose={() => setPicked(null)}
          onCreated={(id) => { setPicked(null); onStarted(id); }} />
      )}
    </>
  );
}
