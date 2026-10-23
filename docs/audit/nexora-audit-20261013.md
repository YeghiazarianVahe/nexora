# Nexora audit, 2026-10-13

Read-only review of Nexora.Api, Application, Domain, Infrastructure, Helper, Contracts and Nexora.Web. No source file was changed by this audit. To observe the app I ran `dotnet build --no-incremental` (rewrites `bin/obj`), `dotnet test`, `vitest`, and started the server with `start.ps1` because it was not running; the browser pass only loaded pages (GET). Destructive controls were not clicked. Line numbers refer to the tree as it is now (including the earlier `Dialog` focus fix in `ui.tsx`).

## 1. Summary

| Severity | Count |
|---|---|
| Critical | 0 |
| High | 5 |
| Medium | 16 |
| Low | 13 |

Builds are clean (0 warnings, 0 errors, `TreatWarningsAsErrors` on), `tsc` is clean, 339 tests pass twice in a row (no flaky test seen), `npm audit` and `dotnet list --vulnerable` report nothing. The browser pass over all 9 screens plus a workstation page produced **no console errors, no failed network calls**. Role checks on every controller were read and are consistent (details in section 6, "Verified OK").

**Fix first (in this order):**

1. **H1. The old `/api/actions` path skips every protection of the new job framework.** The UI still uses it for service start/stop/restart, process kill and profile delete. An Operator can kill a process or stop a service on wks-111, a protected host or a server OS with no confirmation token, no reason and no protected-host check.
2. **H2. Queued jobs never expire.** A "Restart" queued while the operator's helper is offline fires whenever that helper next connects, days later, even if the operator has since been demoted or revoked.
3. **H3. `RunScript` uses `Invoke-Command -ComputerName`, which is WinRM.** That breaks the "no WinRM" constraint.
4. **H4. Every poll rebuilds the DTO from full-table reads, then broadcasts it to every browser.** With 2699 workstations, one monitoring cycle does about 2699 full scans of `GroupMembers` and `DiskHealth` and sends 2699 SignalR messages, each forcing the Workstations page to rebuild a 2699-item array. This is also what made the confirmation dialog lose focus.
5. **H5. Bulk delete is not atomic and can lose its audit entry.** Deletes run as separate auto-committed statements and the audit row is written afterwards. A failure in between leaves a partial delete with no record.

## 2. Findings

Ordered by severity. Effort: S under 1 h, M under a day, L more than a day.

### High

| ID | Sev | Category | File:line | What's wrong | Impact | Suggested fix | Effort |
|---|---|---|---|---|---|---|---|
| H1 | High | Security / guards | `Nexora.Application/Services/ActionService.cs:48-107`, `:184-224`; `Nexora.Api/Controllers/OperationsControllers.cs:22-30`; used by `Nexora.Web/src/pages/EndpointDetail.tsx:64`, `Groups.tsx:158` | The legacy action path (`StartService`, `StopService`, `RestartService`, `KillProcess`, `RunScript`, `DeleteProfile`, `RemoteSession`) never calls `JobService.ProtectedReason`, needs no confirm token, no reason and no typed confirmation. Confirmation exists only as a client dialog. | Operators can act on the Nexora server, listed protected hosts and server-OS machines. The server-side confirmation the user asked for does not apply to the actions the UI actually uses today. | Move these six actions into the job catalog and delete the legacy path, or at minimum call `ProtectedReason`, require a reason and consume a confirm token in `ActionService.SubmitAsync`. | M |
| H2 | High | Robustness / security | `Nexora.Application/Jobs/JobOrchestrator.cs:99-120`; `JobService.cs:132-172` | A target with no connected helper goes back to `Queued` and the job to `WaitingForHelper` indefinitely; only `Running` targets get a timeout. Role and protection are checked only at creation, not at dispatch. | A power action (restart/shutdown) queued on Monday runs on Friday when the helper reconnects. A revoked or demoted operator's queued jobs still run under their identity. | Expire `Queued`/`WaitingForHelper` targets after a short TTL (for example 10 min, `Skipped: helper not available`). Re-check the initiator's role, protected status and `Implemented` at dispatch time. | M |
| H3 | High | Constraint violation | `Nexora.Helper/JobExecutor.cs:334` | Legacy `RunScript` wraps the script in `Invoke-Command -ComputerName '<host>'`, which uses WinRM. The wrapper also does not set console output encoding (Armenian/Russian output becomes mojibake) and has a fixed 90 s limit. | Violates "no WinRM" and fails on hosts with WinRM off. | Either drop the legacy action in favour of an approved WMI/admin-share executor, or confirm with the owner that script catalog runs may use WinRM. | M |
| H4 | High | Performance | `Nexora.Application/Services/EndpointServices.cs:82-96` (`EndpointDtoFactory.BuildAsync`); called from `Nexora.Api/Services/MonitoringService.cs:251`, `EndpointServices.cs:405`; client `Nexora.Web/src/realtime.tsx:62-66` | `BuildAsync` reads all of `GroupMembers` and `DiskHealth` and the settings row, then filters in memory, and it is called once per polled endpoint (`BuildOneAsync`). Each result is pushed to all viewers as `EndpointUpdated`; the client replaces the whole 2699-item cache array for every message. `DashboardService.GetAsync` (`:405`) also builds DTOs for the whole fleet on each `DashboardChanged`. | A poll cycle is O(n²) in database reads, floods browsers (main thread busy, dialogs and typing disturbed) and scales badly. | Query only the needed ids (`Where(ids.Contains)`) or cache group/disk lookups per cycle. Batch `EndpointUpdated` (for example one message per second with a list). Compute dashboard counts with SQL aggregates. | M |
| H5 | High | Data integrity / audit | `Nexora.Application/Services/EndpointServices.cs:300-322`, `:324-333`; single delete `:254-269` | Bulk delete runs five `ExecuteDelete` statements that each commit on their own, then writes the audit row with `SaveChanges`. A failure in between leaves history deleted with the endpoints still present, or endpoints deleted with no audit. The audit entry holds a count and 50 host names but not the mode/id-list hash the spec asks for. `ids` is unbounded; no progress for large deletes. Single delete (`:260-263`) uses `RemoveRange(IQueryable)`, which loads every metric row into memory. | Partial deletes and unaudited deletes are possible. A large single delete can exhaust memory. | One transaction around deletes + audit insert (`BeginTransactionAsync`), cap ids (5000), chunk the deletes, add a SHA-256 of the sorted id list and the mode to the audit details, and use `ExecuteDelete` for single delete. | M |

### Medium

| ID | Sev | Category | File:line | What's wrong | Impact | Suggested fix | Effort |
|---|---|---|---|---|---|---|---|
| M1 | Medium | Security | `Nexora.Api/Hubs/Hubs.cs:182-194`, `:223-227`; claim in `Nexora.Web/src/pages/RoleManagement.tsx:153` | Hub group membership (`viewers`, `jobs-admin`, `superadmins`) is assigned once at connect. `AccessChanged` only sends a message to the user; the server never removes groups or closes the connection. The Revoke dialog says the user is "signed out of live updates immediately". | A revoked or demoted user's open tab (or a modified client) keeps receiving fleet and job data until it reconnects. Helper connections keep reporting. | Track connections per SID; on role change remove groups and `Abort()` the connections. Fix the dialog wording until then. | M |
| M2 | Medium | Correctness | `Nexora.Infrastructure/Wmi/WmiDisksAndProfiles.cs:136-141`, `:346-352` | `ResolveAccount` runs on the Nexora server and caches **failures** (null) permanently. A transient DC error, or a machine-local account of a remote PC, shows as `unknown account (deleted)` until the process restarts. | Wrong "deleted" labels. If bulk delete is built on "unresolved", live accounts could be treated as deletable. | Do not cache null (or cache briefly), resolve machine-local SIDs on the target (`Win32_UserAccount`/`Win32_SID`), and require positive evidence before treating a profile as orphaned. | M |
| M3 | Medium | Guards | `Nexora.Application/Jobs/JobService.cs:80-93`, `:99-109` | Protected status is decided by `Hostname`/`ReportedHostname == Environment.MachineName` and `OsCaption` containing "Server". The server added by IP or FQDN, or never polled yet (`OsCaption` null), is not recognised. `IsPinned` is not used. Protection applies only to `IsPower` or `Critical` actions. | The Nexora server can be treated as a normal PC in some states. High-risk non-power actions (`repair.dism.restore`, `update.reset`) ignore protection. | Treat `IsPinned` as protected, compare against IP/FQDN/AD name too, and decide whether High-risk actions on protected hosts need the override. | S |
| M4 | Medium | Guards | `Nexora.Api/Controllers/FleetControllers.cs:107-113`; `EndpointServices.cs:300-306` | `POST /api/endpoints/remove {all:true}` needs only Admin and a CSRF token. The typed `DELETE ALL` phrase is enforced only in the browser. The "empty ids means all" mistake is correctly rejected (`All == (Ids.Count>0)`), which is good. | One API call (or a script on a logged-in Admin's session) wipes the inventory. | Require `expectedCount` (and for All a short-lived confirm token like jobs use) and verify it server-side. | S |
| M5 | Medium | Guards | `ActionService.cs:210-216`; `JobPolicy.cs:54-58`; `JobExecutor.cs:300-322` | Profile delete guards are partial: Special and Loaded are checked on the helper, path must match, SID prefix `S-1-5-21-`/`S-1-12-1-`. Missing: operator's own SID, default/public profiles, server-side verification against collected data, typed account name, size in the audit. This is Step 2 of the profiles task, still pending; the UI delete was removed, but the API accepts it from any Admin. | Data loss is limited to Admin callers and non-loaded profiles, but the guards described in the spec do not exist yet. | Implement Step 2 as specified. | M |
| M6 | Medium | Robustness | `Nexora.Infrastructure/Wmi/WmiEndpointCollector.cs:21`, `:76`; `Nexora.Api/Services/MonitoringService.cs:127`, `:197-199` | `Task.Run(() => ..., ct)` only checks the token between queries; WMI calls themselves are not cancelled. A hung host holds a concurrency slot for up to 7 sequential 30 s timeouts, and `Task.WhenAll(inFlight)` makes the whole cycle wait for the slowest host. | One bad PC delays the poll of the whole fleet. | Overall per-host deadline (run the query on a task and abandon it on timeout), and do not await all in-flight tasks before starting the next cycle. | M |
| M7 | Medium | SignalR / state | `Nexora.Web/src/jobs/JobDetail.tsx:301-305`; `EndpointDetail.tsx:57-61`; `jobs/JobParts.tsx:219-241` | `SubscribeJob`/`SubscribeEndpoint` are called once; a reconnect creates a new connection id that is in no group, so live log lines stop silently (no `onreconnected` resubscribe). In `LogViewer`, a live line arriving before the initial page load sets `lastSeq` high and the earlier lines are then dropped as "already seen". The endpoint subscription is unused anyway (server never sends to `endpoint:*`). | Missing or frozen log output for running sfc/DISM jobs. | Resubscribe on reconnect, refetch from `lastSeq` after reconnect, and merge by `seq` rather than a high-water mark. | S |
| M8 | Medium | Formatting | `Nexora.Web/src/ui.tsx:93-94` (used in 10 places); `charts.tsx:57-62` | `fmtDate` uses `toLocaleString([])`, so it follows the browser locale. Observed on Audit: `10/13/26, 9:26:38 AM`. The required format is `dd.MM.yyyy HH:mm`, Asia/Yerevan. A correct `formatAbsolute` already exists in `lib/format.ts:118`. | US-format dates and local time zone on Audit, Jobs, Dashboard, Role Management, Settings, Actions, charts. | Make `fmtDate` call `formatAbsolute` (minutes variant) and give the charts the same zone. | S |
| M9 | Medium | React | `Nexora.Web/src/main.tsx`, `App.tsx` | No error boundary anywhere. | One exception in any widget (for example a malformed row in a table) blanks the whole app. | Add a boundary per route and one around the shell, with a retry button. | S |
| M10 | Medium | Correctness / UX | `lib/format.ts:80-81`; `ui.tsx:101-103` (`Meter` ignores `warnAt/badAt`); server alerts in `Mapping.AlertsFor` | Meter colours use fixed 70/90, while alerts and dashboard counts use the Settings thresholds (`cpuAlertPercent` etc.). | A PC can be red in the table with no alert, or alerting while green. The Dashboard cards the user asked for will inherit this. | Feed the Settings thresholds to the colour function (one source). | S |
| M11 | Medium | Selection | `Nexora.Web/src/pages/Endpoints.tsx:75`, `:169`, `:385-392` | The selection set is never reconciled with the current list: ids of endpoints deleted elsewhere stay counted. "Delete all" text counts the pinned server (`totalCount`) although the server skips it. | Toolbar count, dialog phrase and rows actually deleted can differ by a few. | Prune the selection when `all` changes; subtract pinned rows from the delete-all count. | S |
| M12 | Medium | React / a11y | `Endpoints.tsx:363`, `components/EndpointParts.tsx:53-56` | The row's `onKeyDown` navigates on Enter for any key event that bubbles, including Enter on the OU filter button and the checkbox inside the row. | Keyboard users trying to filter by OU or tick a row are taken to the detail page. | Ignore the event when `ev.target !== ev.currentTarget`. | S |
| M13 | Medium | React | `ui.tsx:143-158` (`useSort`), `profiles/ProfilesPanel.tsx:37` | The sorted list is memoised on `[rows, sort]` only (the eslint rule is disabled) while accessors close over other state. Sorting profiles by size does not re-sort when new sizes arrive. | Wrong order after measuring. | Include `accessors` in deps (memoise them at the call site) or sort inside the accessor input. | S |
| M14 | Medium | Robustness | `Nexora.Api/Services/MonitoringService.cs:153-160` | Retention purges only metrics, fleet samples and disk temps. `JobLogChunks` (up to 50 000 rows per target), `Jobs`, `JobTargets`, `ActionJobs` and `AuditEntries` grow forever. | Database growth; slower job pages. | Add a retention setting for job logs/jobs (audit may need its own policy). | M |
| M15 | Medium | Config | `Nexora.Application/Jobs/JobOrchestrator.cs:21`; `JobService.cs:42`, `:60`; catalog `ActionCatalog.cs` (about 50 entries, only 5 `implemented: true`) | `Jobs:EnableDemo` defaults to **true**, so "Simulate" actions show in production. The user asked for `Features:DemoActions`, default off. About 45 catalog entries are listed but not runnable. | Confusing UI; demo actions callable by Operators. | Default off, rename to the agreed flag, hide non-implemented entries unless requested. | S |
| M16 | Medium | Security / robustness | `Nexora.Api/Services/JobsRuntime.cs:73-90`; `Nexora.Helper/HelperJobs.cs:405-415` | Helper reconnect gets a new session key; a job dispatched with the old key (or before the helper stored the new one) fails MAC verification, and the helper reports it as `Failed`. | A transient reconnect turns into failed targets that are not retried. | Treat BadMac/no-key as "not started": have the helper return a retryable status and the orchestrator re-queue. | S |

### Low

| ID | Sev | Category | File:line | What's wrong | Impact | Suggested fix | Effort |
|---|---|---|---|---|---|---|---|
| L1 | Low | Validation | `EndpointServices.cs:109-112`; `JobPolicy.cs:14` | Host/name regexes end with `$`, which also matches before a trailing `\n`. AD import values (`:344`) are not trimmed. The host is later placed in a PowerShell string (`JobExecutor.cs:334`). | Unlikely injection path, but cheap to close. | Use `\z`, trim input. | S |
| L2 | Low | Config | `Nexora.Helper/Program.cs:12,135`; `Nexora.Web/src/App.tsx:32`; `README.md:48,51,71,146,157` | Default/help text says port 5080; the pilot runs on 5090. | A helper started without `--server` connects to the wrong port. | Read the port from one config value. | S |
| L3 | Low | Security | `Nexora.Application/Services/EndpointExport.cs` (`EndpointCsv.Text`) | Formula guard misses a leading `\n` and leading spaces before `=`. | CSV injection through attacker-controlled names (last user, OU). | Trim start, include `\n`. | S |
| L4 | Low | Robustness | `Nexora.Application/Jobs/JobContracts.cs:169`, `:178` | `Regex.IsMatch(..., timeout)` can throw `RegexMatchTimeoutException`, which nothing catches (500 instead of 400). | Poor error for pathological input. | Catch and return a validation error. | S |
| L5 | Low | Leak | `JobOrchestrator.cs:37`; `Nexora.Helper/Program.cs:26`, `HelperJobs.cs:388` | `CancelSent`, `seenJobs`, `_seen` only grow. | Slow memory growth in a long-lived process. | Remove entries when targets finish. | S |
| L6 | Low | React | `Nexora.Web/src/realtime.tsx:43-46`, `:117-120` | The retry `setInterval` is never cleared on unmount; the `started` ref is never reset, so a remount would not reconnect. | Leak and no reconnect after remount (not reachable today). | Clear in cleanup; reset the ref. | S |
| L7 | Low | Forms | `Nexora.Web/src/pages/Settings.tsx:47-48` | `Number('')` becomes 0, so a number field cannot be emptied; there is no client min/max check (the server validates). | Awkward editing. | Keep text state, validate on blur. | S |
| L8 | Low | Race | `JobService.cs:138-148` | The confirm token is consumed before the heavy-job check, so a busy conflict burns it. The "one heavy job per target" check is check-then-insert, not atomic. | Re-confirm needed; two simultaneous requests can both pass. | Check heavy first; add a partial unique index or lock. | M |
| L9 | Low | Info leak | `EndpointServices.cs:167`, `:178` | `ex.Message` from WMI/DCOM is returned to the browser. | Internal host/error detail visible to Viewers/Operators. | Log it, return a generic message. | S |
| L10 | Low | Secrets | `scripts/nexora.ps1:262`, `:269-270` | The DB role password is passed on the `psql` command line; `run/db.secret` and `run/db.conn` are plain ASCII with default ACLs. | Visible briefly in process lists; readable by anyone with access to the folder. | Use `PGPASSWORD`/`.pgpass`, restrict the ACL to the service user. | S |
| L11 | Low | Hygiene | repo root is the user's home directory (`git status` lists `NTUSER.DAT`, `.claude.json`, ...) | Nexora lives inside a home-directory git repo with everything untracked. | A careless `git add .` would stage credentials and profile data. | Make `Desktop\Nexora` its own repository. | S |
| L12 | Low | Robustness | `Nexora.Api/Services/JobsRuntime.cs` (`JobSigner.Key`) | A DPAPI-protected key tied to `CurrentUser`; if the service identity changes, `Unprotect` throws and `/api/helper/public-key` fails. | Helpers cannot pin the key. | Fall back to regenerating with a clear log and notice. | S |
| L13 | Low | UX | `Nexora.Web/src/pages/Dashboard.tsx:187-188`, `:230` | Dashboard error has no retry; the attention list is capped at 50 silently; "Offline" counts never-polled endpoints. | Minor confusion. | Retry button, "showing 50 of N", separate "never polled". | S |

## 3. Build & lint output

**Backend** (`dotnet build Nexora.sln --no-incremental`):
```
0 Warning(s)
0 Error(s)
```
`Directory.Build.props` sets `TreatWarningsAsErrors`, `Nullable=enable`, `AnalysisLevel=latest`, and suppresses `CA1416` globally (Windows-only by design).

**Frontend:**
- `tsc --noEmit`: no output (clean). There is no `any`, `@ts-ignore` or `@ts-expect-error` in `src/`.
- ESLint is **not configured** (no `lint` script, no config). Two `eslint-disable react-hooks/exhaustive-deps` comments exist (`ui.tsx:157`, `jobs/JobParts.tsx:240`); both hide real stale-closure risks (M13, M7).
- `vite build` was not run (it writes into `Nexora.Api/wwwroot`, which would change files).

**Packages:**
- `dotnet list package --vulnerable --include-transitive`: none in any project.
- `npm audit`: 0 vulnerabilities.
- Outdated NuGet (not updated): `Serilog.AspNetCore` 9.0.0 → 10.0.0; `FluentAssertions` 6.12.2 → 8.11.0 (major, licence change in 8.x); `Microsoft.NET.Test.Sdk` 17.12.0 → 18.10.1; `xunit.runner.visualstudio` 3.0.0 → 4.0.1.
- Outdated npm: `@tanstack/react-virtual` 3.14.16 → 3.14.17.

**Browser pass** (Edge, loaded `/dashboard /endpoints /groups /profiles /jobs /actions /audit /settings /roles`, a workstation page and all its tabs): 0 console errors or warnings, 0 requests with status ≥ 400, 0 failed requests. First audit date cell rendered as `10/13/26, 9:26:38 AM` (M8).

## 4. Test results

| Suite | Run 1 | Run 2 |
|---|---|---|
| Nexora.Application.Tests | 141 passed | 141 passed |
| Nexora.Api.Tests | 160 passed | 160 passed |
| vitest (Nexora.Web) | 38 passed | 38 passed |

No failures, no flaky tests across two runs. The Playwright e2e suites (profiles 20/20, endpoints 26/26, jobs 27/27, confirm 10/10) were last run earlier in this session and were not re-run here because they use real accounts and interact with the UI. Test coverage gaps worth knowing: no frontend tests for the Endpoints selection flow, `LogViewer` ordering, `realtime.tsx`, or `useSort`; no backend test for the legacy `/api/actions` protections (because there are none, H1); no test for queue expiry (H2).

## 5. Improvement ideas (enhancements, not bugs)

| Idea | Value | Effort |
|---|---|---|
| Batch `EndpointUpdated` into one message per second and apply it with a single `setQueryData` | High | S |
| Dashboard counts in SQL (`COUNT ... FILTER`) and a materialised alert flag on `ManagedEndpoint` | High | M |
| Single catalog for all remote actions; remove `ActionService`/`ActionType`/`JobDto` legacy types and `HelperRemoteActionExecutor` | High | M |
| Send helper log lines in batches (now one SignalR invoke per line, `HelperJobs.cs:468`) | Medium | S |
| ESLint (`react-hooks`, `jsx-a11y`) plus a CI script running build, tsc, lint, tests | Medium | S |
| Shared `<DataTable>` (sticky header, nowrap/ellipsis, skeleton, empty/error/retry) used by Profiles, Jobs, Audit, Role Management | Medium | M |
| Replace `Dialog` ad-hoc `aria-label` with `aria-labelledby`/`aria-describedby`; restore focus and trap tested in Playwright for every dialog | Medium | S |
| Heartbeat/lease for helpers so a dead helper fails its `Running` targets quickly instead of at the action timeout | Medium | M |
| Structured per-job audit "bundle" export (job, targets, output hash) for investigations | Medium | M |
| Move SignalR access checks to a per-connection principal refresh (re-evaluate role every N s) | Medium | M |
| Server-side paging/virtual query for the endpoint list instead of loading up to 5000 rows | Medium | L |
| Optional 24 h sparkline per dashboard card from `FleetSamples` (already stored) | Low | S |

## 6. Verified OK (read, no issue found)

- Every controller has a policy: Viewer for reads; Operator for profiles, scripts list, disk refresh, helper, jobs actions; Admin for add/update/remove/import/bulk-groups/settings/script changes/profile delete; SuperAdmin for `/api/admin/*` with `[HideWhenForbidden]` returning 404. `/internal/shutdown` is loopback-only with a constant-time token check and returns 404 otherwise.
- Jobs: ownership is enforced (non-admins see their own jobs; others get 404), parameters are validated against the catalog schema (unknown names rejected), targets come only from the inventory, confirm tokens are HMAC-bound to SID, action, targets and parameters, expire in 5 min and are single use.
- Helper: MAC verification, expiry, SID match and known-action check before execution; the helper re-validates legacy jobs itself; reports are checked against the initiating SID.
- SIDs are compared by value (case-insensitive string of the SID), never by name; the SuperAdmin is identified by SID.
- CSRF: Origin check on `/api` and `/hubs`, anti-forgery token on every unsafe `/api` call and on `/hubs/nexora` negotiation. The helper hub relies on Origin and Windows identity (documented).
- `Features:CredentialOps` is enforced in `ActionService.SubmitAsync` (`:53-64`), audited as denied, and `ResetPassword` is not implemented even when the flag is on.
- CSV export: UTF-8 BOM, CRLF, quoting, formula guard (with the L3 gap), ids capped at 5000, exactly one of ids/filter.
- LDAP filters are escaped (`AdDirectoryLookup.cs:157-160`); parameter values written to logs go through `OutputRedactor`.
- Sort/filter/SignalR updates keep the selection (Set of ids); header checkbox has an indeterminate state; WebSocket CSP allows only the same host.
- Contrast on `#171717`: `#9A9A9A` ≈ 6.4:1, `#E5484D` ≈ 4.6:1, `#69BA31` ≈ 7.7:1, `#E5A50A` > 9:1 (computed, not tool-measured). All meet AA for normal text.

## 7. Open questions

1. **Legacy actions (H1, H3):** may I remove `/api/actions` and move service/process/profile/script/RDP actions into the job catalog, with `RunScript` implemented without WinRM (admin share + scheduled task)? Or is WinRM acceptable for catalog scripts?
2. **Queue expiry (H2):** what is the right lifetime for a job waiting on a helper: 10 minutes, 1 hour, or "until the operator cancels"? Should power actions have a shorter one?
3. **Revocation (M1):** should a revoked or demoted user be disconnected immediately (hub abort) or at next reconnect?
4. **Bulk delete (H5, M4):** do you want a hard cap (for example 500 per call, chunked in the UI with progress) and a server-checked expected count for "Delete all"?
5. **Retention (M14):** how long must job output and job history be kept? Does the audit table need an archive policy?
6. **Pinned server filters:** should the pinned server stay visible in the Workstations table even when a filter would hide it (earlier design note)?
7. **Demo actions (M15):** confirm the flag name `Features:DemoActions` and default off; should unimplemented catalog entries be hidden or shown as "coming soon"?
8. **Orphaned profiles (M2):** what counts as evidence that a profile belongs to a deleted account (for the planned bulk delete): a failed SID lookup on the DC, or also the absence of the account in AD?
