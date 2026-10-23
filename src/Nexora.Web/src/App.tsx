import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef } from 'react';
import { NavLink, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { ApiError, get, post } from './api';
import { Audit } from './pages/Audit';
import { Dashboard } from './pages/Dashboard';
import { DiskAudit } from './pages/DiskAudit';
import { HelperCoverage } from './pages/HelperCoverage';
import { endpointRoutes } from './pages/EndpointDetail';
import { JobDetailPage } from './jobs/JobDetail';
import { Jobs } from './jobs/Jobs';
import { Profiles } from './pages/Profiles';
import { Endpoints } from './pages/Endpoints';
import { Groups } from './pages/Groups';
import { RoleManagement } from './pages/RoleManagement';
import { Settings } from './pages/Settings';
import { HelperIndicator } from './jobs/helper';
import { RealtimeProvider, useRealtime } from './realtime';
import { roleRank, type AccessRequest, type Me } from './types';
import { ErrorBoundary } from './components/ErrorBoundary';
import { ErrorNotice, Loading } from './ui';
import { ThemeToggle } from './components/ThemeToggle';

export function App() {
  const me = useQuery({ queryKey: ['me'], queryFn: () => get<Me>('/api/me'), staleTime: 0 });

  if (me.isLoading) return <Gate><Loading label="Signing you in" /></Gate>;
  if (me.error || !me.data) {
    const err = me.error;
    return (
      <Gate>
        <h1>Cannot sign you in</h1>
        <ErrorNotice error={err} />
        {err instanceof ApiError && err.status === 401 && (
          <p className="muted">
            Open Nexora with the short host name (for example <span className="mono">{window.location.protocol}//{window.location.hostname.split('.')[0]}{window.location.port ? `:${window.location.port}` : ''}</span>)
            so your browser can use Windows single sign-on.
          </p>
        )}
      </Gate>
    );
  }

  // The realtime channel is open for every signed-in user, including those waiting for approval.
  return (
    <RealtimeProvider>
      <Router me={me.data} />
    </RealtimeProvider>
  );
}

function Router({ me }: { me: Me }) {
  switch (me.status) {
    case 'Active': return <Shell me={me} />;
    case 'Denied': return <Denied me={me} />;
    case 'Revoked': return <Revoked me={me} />;
    default: return <Waiting me={me} />;
  }
}

function Gate({ children }: { children: React.ReactNode }) {
  return (
    <div className="gate">
      <div className="logo"><b>Nexora</b> | IT Operations Platform</div>
      <div className="gate-theme"><ThemeToggle /></div>
      {children}
    </div>
  );
}

function Denied({ me }: { me: Me }) {
  return (
    <Gate>
      <h1>Access denied</h1>
      <p>{me.account} is not a member of the IT Service Desk group, so Nexora is not available to this account.</p>
      <p className="muted">If you think this is a mistake, ask your administrator to review your group membership.</p>
    </Gate>
  );
}

function Revoked({ me }: { me: Me }) {
  return (
    <Gate>
      <h1>Access revoked</h1>
      <p>Your Nexora access was revoked, {me.displayName}.</p>
      <p className="muted">Contact the Nexora administrator if you need it restored.</p>
    </Gate>
  );
}

/** Waiting for approval. The SuperAdmin's decision arrives over SignalR and flips this page into the app. */
function Waiting({ me }: { me: Me }) {
  const qc = useQueryClient();
  const requested = useRef(false);
  const { state } = useRealtime();
  const request = useMutation({
    mutationFn: () => post<AccessRequest>('/api/access/request'),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['me'] }),
  });

  useEffect(() => {
    if (me.status === 'NoAccess' && !requested.current) {
      requested.current = true;
      request.mutate();
    }
  }, [me.status, request]);

  // Safety net if the push is missed (for example the connection is down).
  useEffect(() => {
    const t = window.setInterval(() => qc.invalidateQueries({ queryKey: ['me'] }), 30_000);
    return () => window.clearInterval(t);
  }, [qc]);

  return (
    <Gate>
      {me.status === 'Rejected' ? (
        <>
          <h1>Request not approved</h1>
          <p>Reason: <b>{me.detail}</b></p>
          <button className="primary" onClick={() => request.mutate()} disabled={request.isPending}>Request access again</button>
        </>
      ) : (
        <>
          <h1>Waiting for approval</h1>
          <p>Your access request was sent to the Nexora administrator.<br />This page opens the app as soon as it is approved.</p>
          <div className="spinner" />
        </>
      )}
      <ErrorNotice error={request.error} />
      <p className="muted">Signed in as {me.account} &middot; live updates {state}</p>
    </Gate>
  );
}

function Shell({ me }: { me: Me }) {
  const location = useLocation();
  const { state } = useRealtime();
  const rank = roleRank[me.role];
  const pending = useQuery({
    queryKey: ['admin', 'pending-count'],
    queryFn: () => get<AccessRequest[]>('/api/admin/access-requests?status=Pending'),
    enabled: me.isSuperAdmin,
  });

  const dot = state === 'connected' ? 'ok' : state === 'reconnecting' ? 'warn' : 'bad';

  return (
    <div className="shell">
      <header className="topbar">
        <div className="brand"><b>Nexora</b> <span>|</span> IT Operations Platform</div>
        <div className="spacer" />
        <ThemeToggle />
        {rank >= roleRank.Operator && <HelperIndicator />}
        <div className="conn" title="SignalR connection" role="status">
          <span className={`dot ${dot}`} /> {state === 'connected' ? 'Live' : state === 'reconnecting' ? 'Reconnecting' : 'Offline'}
        </div>
        <div className="identity">
          {me.displayName} <span className="role-pill">{me.role}</span>
          <small className="mono">{me.account}</small>
        </div>
      </header>
      <div className="body">
        <nav className="nav" aria-label="Main">
          <NavLink to="/dashboard">Dashboard</NavLink>
          <NavLink to="/endpoints">Workstations</NavLink>
          <NavLink to="/groups">Groups</NavLink>
          {rank >= roleRank.Operator && <NavLink to="/profiles">Profiles</NavLink>}
          <NavLink to="/jobs">Jobs</NavLink>
          <NavLink to="/audit">Audit</NavLink>
          {rank >= roleRank.Admin && <NavLink to="/admin/helpers">Helpers</NavLink>}
          {rank >= roleRank.Admin && <NavLink to="/settings">Settings</NavLink>}
          {me.isSuperAdmin && (
            <NavLink to="/roles">
              Role Management
              {(pending.data?.length ?? 0) > 0 && <span className="badge">{pending.data!.length}</span>}
            </NavLink>
          )}
        </nav>
        <main className="main">
          {/* keyed by the path: moving to another page gives it a fresh boundary, so one broken page never traps the rest */}
          <ErrorBoundary key={location.pathname} what="this page">
          <Routes>
            <Route path="/" element={<Navigate to="/dashboard" replace />} />
            <Route path="/dashboard" element={<Dashboard />} />
            <Route path="/endpoints" element={<Endpoints me={me} />} />
            {endpointRoutes(me).map((r) => <Route key={r.path} path={r.path} element={r.element} />)}
            <Route path="/groups" element={<Groups me={me} />} />
            {rank >= roleRank.Operator && <Route path="/profiles" element={<Profiles me={me} />} />}
            <Route path="/jobs" element={<Jobs me={me} />} />
            <Route path="/jobs/:id" element={<JobDetailPage me={me} />} />
            <Route path="/audit" element={<Audit me={me} />} />
            {rank >= roleRank.Admin && <Route path="/settings" element={<Settings me={me} />} />}
            {rank >= roleRank.Admin && <Route path="/admin/disk-audit" element={<DiskAudit />} />}
            {rank >= roleRank.Admin && <Route path="/admin/helpers" element={<HelperCoverage />} />}
            {me.isSuperAdmin && <Route path="/roles" element={<RoleManagement />} />}
            <Route path="*" element={<p className="muted">Page not found.</p>} />
          </Routes>
          </ErrorBoundary>
        </main>
      </div>
    </div>
  );
}
