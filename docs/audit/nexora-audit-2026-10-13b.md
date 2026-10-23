# Nexora re-audit, 2026-10-13 (b)

Read-only. No source file was changed; the only file written is this report. To observe the app I rebuilt (`dotnet build --no-incremental`), ran the test suites twice, ran `tsc`, read `publish/logs`, and made GET-only calls against the running server (inventory, disk data, disk audit). Nothing destructive was clicked. Line numbers are for the tree as it is now.

## 1. Summary

| Severity | Count |
|---|---|
| Critical | 0 |
| High | 7 (3 new, 4 carried over) |
| Medium | 22 (7 new, 15 carried over) |
| Low | 17 (7 new, 10 carried over) |

Build: 0 warnings, 0 errors. `tsc`: clean. Tests: 193 + 212 (.NET) and 73 (vitest), identical on two runs, no flaky test. `npm audit`: 0 vulnerabilities. ESLint: still not configured.

**Regression verdict: nothing came back, but little of the first audit was fixed.** Of the 34 findings of the 2026-10-13 audit: 4 Fixed, 4 Partial, 1 N/A, 25 still Open, 0 Regressed.

**Fix first:**
1. **N3, polls silently lost.** A unique-key violation on `disk_health` (530 database errors in the log, 54 poll results thrown away) makes a PC stop updating, with nothing shown in the UI. Two PCs failed on every cycle until they were removed from the inventory.
2. **N1, the helper no longer authenticates the server.** Since the ECDSA signing was removed in H1, the helper accepts jobs from whatever server it connects to. On plain HTTP, a spoofed `wks-111` could run catalog actions as the operator.
3. **N2, the new health scoring rests on one reference PC.** The ADATA "attribute 169 raw = remaining life" rule turns 102 of 131 SSDs into Warning or Critical (36 Critical), and the correlation with power-on hours is weak (-0.34).
4. **H2, queued jobs never expire.** A restart queued while the helper is offline can run days later, even after the operator was revoked.
5. **H5 + N4, deletes and helper state are not reliable.**
   - **H5:** bulk delete is not transactional and can lose its audit entry.
   - **N4:** `HelperHub.OnDisconnectedAsync` throws on every disconnect (30 logged today), so the "helper dropped" push is never sent.

## 2. Regression table (2026-10-13 findings)

| Prior ID | Verdict | Evidence |
|---|---|---|
| H1 legacy `/api/actions` | **Fixed** | No `/api/actions`, `IActionService` or `ActionService` left in `src/`. The six actions are catalog jobs with the protected-host check and confirm token. |
| H2 queue TTL + dispatch re-validation | **Open** | `JobOrchestrator.cs:92-108`: a target with no helper goes back to `Queued` forever; no TTL, no role/protected re-check at dispatch. |
| H3 RunScript without WinRM | **Partial** | No `Invoke-Command`/WinRM in code, but `script.run` is `implemented:false` (`ActionCatalog.cs:178`), so scripts cannot run at all (see N8). |
| H4 O(n²) poll + broadcast | **Open** | `EndpointServices.cs:77-110` still reads all `GroupMembers` and `DiskHealth` per call; `MonitoringService.cs:254` calls it per polled PC; `realtime.tsx:62-66` still replaces the whole list per message. |
| H5 bulk delete atomic, audited | **Open** | `EndpointServices.cs:144-170`: five auto-committed `ExecuteDelete`s, then the audit row; no id-hash, no chunking, ids unbounded; single delete `:274` still `RemoveRange(IQueryable)`. |
| M1 revoke drops hub groups | **Open** | `Hubs.cs`: `AccessChangedAsync` only sends a message; no `Abort()` or group removal. |
| M2 `ResolveAccount` caches failures | **Open** | `WmiDisksAndProfiles.cs:151-155` caches `null` forever. |
| M3 protected-host detection | **Partial** | `IsPinned` and the `Protect` flag are in (`JobService.cs:84`, `ActionModel.cs`); still name-compare only, no IP/FQDN/AD name; null `OsCaption` not protected. |
| M4 delete-all server-checked | **Open** | `FleetControllers.cs` `remove`: `{all:true}` needs only Admin + token; no `expectedCount`/confirm token. |
| M5 profile-delete guards | **Partial** | The helper refuses own SID, loaded, special, moved path (`RemoteActions.cs`); server has no typed account name, no size in the audit, no default/public check. |
| M6 per-host WMI deadline | **Open** | `WmiEndpointCollector.cs:21,80`: `Task.Run(..., ct)` is not cancellable; `MonitoringService.cs:127` still `WhenAll(inFlight)`. |
| M7 resubscribe + log merge | **Open** | `JobDetail.tsx:29` subscribes once; `JobParts.tsx:77` still a `lastSeq` high-water mark. |
| M8 dates | **Open** | `ui.tsx:98` `fmtDate` is locale-based; `charts.tsx:57-62` too; new `helper.tsx` uses it. |
| M9 error boundary | **Open** | No `ErrorBoundary` anywhere in `src/Nexora.Web/src`. |
| M10 thresholds single source | **Partial** | `GET /api/thresholds` + `useThresholds` feed every meter; chart alert lines still hard-coded 90 (`EndpointDetail.tsx:114,120`). |
| M11 selection pruning / delete-all count | **Open** | `Endpoints.tsx:395,400` still counts the pinned server; selection never pruned. |
| M12 row Enter on nested controls | **Open** | `Endpoints.tsx:372` unchanged. |
| M13 `useSort` deps | **Open** | `ui.tsx:161` and `JobParts.tsx:97` still disable `exhaustive-deps`. |
| M14 retention | **Open** | `MonitoringService.cs:157-159` purges metrics only; no job-log/job retention. |
| M15 demo flag | **Fixed** | `Features:DemoActions` default false (`NexoraOptions.cs:62`, `JobService.cs:43,61,116`). One stale comment at `ActionModel.cs:50`. |
| M16 BadMac re-queue | **Open** | `HelperJobs.cs:37-40`: a MAC failure is reported as `Failed`. |
| L1 `$` in host regex | **Open** | `EndpointServices.cs:120` (the helper's own checks use `\z`). |
| L2 port 5080 vs 5090 | **Fixed** | Server default, appsettings, scripts, README, helper default and `App.tsx` all 5090; a test scans `src/` and `scripts/`. |
| L3 CSV guard | **Fixed** | `EndpointExport.cs` `Text()` covers `= + - @`, tab, CR, LF and a leading space; tested. |
| L4 regex timeout unhandled | **Open** | `JobContracts.cs:188`: no `RegexMatchTimeoutException` handling. |
| L5 unbounded sets | **Open** | `JobOrchestrator.cs:35`, `HelperJobs.cs:17`. |
| L6 realtime retry interval | **Open** | `realtime.tsx:114` never cleared. |
| L7 Settings number inputs | **Open** | `Settings.tsx:48` `Number('')` = 0. |
| L8 token burned before heavy check | **Open** | `JobService.cs:165` before `:169`. |
| L9 raw exception text | **Open** | `EndpointServices.cs:178,189,353`. |
| L10 DB password on psql command line | **Open** | `scripts/nexora.ps1:263`. |
| L11 repo is the home directory | **Open** | `git rev-parse --show-toplevel` = `C:/Users/Demo Admin`. |
| L12 DPAPI signing key | **N/A** | `JobSigner` was removed with the legacy path. |
| L13 Dashboard retry / cap / offline | **Open** | `Dashboard.tsx`: no retry button; `EndpointServices.cs` still `Take(50)`. |

## 3. Findings

Order: severity, then impact. IDs `N1…N17` are new; the others carry the old ID and are described in the 2026-10-13 report, with current file:line here.

### High

| ID | Category | File:line | What's wrong | Impact | Suggested fix | Effort |
|---|---|---|---|---|---|---|
| N3 | Robustness | `DiskHealth.cs:423-426` (`StoreAsync`), `NexoraDbContext.cs:61-62` (unique index), `MonitoringService.cs:261` | `StoreAsync` inserts a snapshot for every drive of a scan with no de-duplication and no upsert; two entries with the same `DiskKey` (or two concurrent stores) hit `IX_disk_health_EndpointId_DiskKey`. The whole poll result for that PC is dropped and only logged. | 530 DB errors / 54 failed polls today; wks-206 and wks-207 failed on every cycle until removed, WKS-PH37 once. A PC stops showing CPU/RAM/last seen with no error in the UI. Root cause not confirmed (duplicate serial in one scan, or a "Read now" racing the monitor). | De-duplicate by key inside a scan, upsert, retry once on 23505, and record the failure in `LastError` so it shows. | S |
| N1 | Security | `HelperJobs.cs:23,37` (session key from the server), `Hubs.cs:125` (`SessionKey`), `Program.cs` (helper) | Since the ECDSA signing was removed (H1) the helper trusts the MAC key sent by whichever server it connected to. Nothing proves it is the real Nexora. | On plain HTTP, DNS/ARP spoofing of `wks-111` (or a changed `--server`) lets an attacker run any catalog action (`svc.stop`, `proc.kill`, `profile.delete`, …) as the operator on any host the operator can reach. `/health` identifies the app but not the server. | Re-introduce a pinned server key (published in the helper download, jobs signed with it) or move to HTTPS. Decide before more actions are implemented. | M |
| N2 | Correctness | `DiskHealth.cs:204-210` (LifeLeft, `StartsWith("ADATA")`), `:108` (age penalty removed) | Attribute 169 **raw** is read as remaining life for every ADATA SSD, validated on one PC (wks-44). Fleet data: 117 ADATA drives, raw 0-98, Spearman with power-on hours -0.34; result 36 SSDs Critical (<50), 66 Warning. | If 169 is not "remaining life" on all ADATA models, 100+ PCs show false health alerts and the dashboard alert count jumps. If it is, 36 PCs have worn drives nobody was told about. Either way the number needs proof. | Check 5-10 more PCs against HD Sentinel, limit the rule by model/firmware, add a Settings switch, and cap at Warning until verified. | S |
| H2 | Safety | `JobOrchestrator.cs:92-108` | Queued/WaitingForHelper targets never expire; no re-check of role, protection, `Implemented` at dispatch. | A restart queued Monday runs Friday; a revoked operator's jobs still run. | 10 min TTL (2 min for Power/Critical), re-validate at dispatch. | M |
| H3 | Constraint | `ActionCatalog.cs:178` | See N8: no WinRM, but also no script runner. | Admin scripts cannot run. | Admin share + SYSTEM task with correct encoding, or remove the catalog UI. | M |
| H4 | Performance | `EndpointServices.cs:77-110`, `MonitoringService.cs:254`, `realtime.tsx:62-66` | O(n²) DTO build per polled PC; one SignalR message per PC; whole-list rewrite on the client. | Cycle cost grows with fleet size; page re-renders constantly (the original cause of the dialog focus bug). | Query only polled ids, batch `EndpointUpdated` once a second, SQL aggregates for the dashboard. | M |
| H5 | Data integrity | `EndpointServices.cs:144-170, 274` | Not transactional; audit after delete; ids unbounded; no id hash. | Partial or unaudited deletes; single delete can exhaust memory. | One transaction, chunking, 500 cap, `mode` + SHA-256 of ids in the audit, `ExecuteDelete`. | M |

### Medium

| ID | Category | File:line | What's wrong | Impact | Suggested fix | Effort |
|---|---|---|---|---|---|---|
| N4 | Bug | `Hubs.cs:126,136,153` (`GetAccount()` after the connection is gone) | `HelperHub.OnDisconnectedAsync` reads `Identity.Name` from a disposed `WindowsIdentity` and throws (30 `Error when dispatching 'OnDisconnectedAsync'` today, 38 "Failed disposing connection"). The "helper dropped" push comes after the throwing line. | The browser is not told when a helper drops (only the 15 s poll catches up); noisy error log. | Capture SID/account in `Context.Items` at connect and use those; send the push before logging. | S |
| N5 | Correctness | `Dtos.cs:139` (diskhealth alert only when online), `EndpointExport.cs:54` / `EndpointFilters.HealthMatches` (all PCs) | Dashboard "Drive health alerts" counts online PCs; the table's "Needs attention" filter counts offline ones too. With N2 the number is now ~100. | Card and list disagree; alert fatigue. | One definition for both (and make the card a link). | S |
| N6 | Bug | `start.ps1:34`, `stop.ps1:7` | `start.ps1` skips starting your helper if any user's `Nexora.Helper` runs; `stop.ps1` force-kills every helper process it may. With the all-users startup, other admins' helpers on the server PC are affected. | Another admin's helper is killed on every restart; yours may not start. | Filter by current session/user. | S |
| N7 | Correctness | `EndpointExport.cs:208,238,247` | CSV "Last Logon" is the last time Nexora saw the PC online, "PC Name" is the display name, and the drive-to-volume attribution is inferred. All documented, but the headers promise otherwise. | Misleading data in the file. | Collect real logon time, export the hostname, or rename the columns. | S/M |
| N8 | Product gap | `Settings.tsx:68` (script catalog), `ActionCatalog.cs:178` | Admins can still create approved scripts; nothing can run them. | Dead feature. | Implement `script.run` or hide the UI. | M |
| N9 | Scope | `JobDetail.tsx`/`JobParts.tsx` (inline `LogViewer`), `Dashboard.tsx` | The Job Output popup (names/confirmation task, Part C) and interactive dashboard cards were not built, although listed as done work. | M7 and the output requirements are unmet. | Build them or confirm they were dropped. | M/L |
| N10 | Robustness | `DiskAudit.cs:149`, `DependencyInjection.cs:82` | Rescan-all and the startup score-version check mark every PC due at once, so one cycle does a full drive scan on all PCs. | Cycle length spikes; WMI load. | Stagger (due-time jitter or a per-cycle cap). | S |
| M1 | Security | `Hubs.cs` | Revocation does not drop live connections. | Revoked users keep live data. | Track connections per SID; remove groups and `Abort()`. | M |
| M2 | Correctness | `WmiDisksAndProfiles.cs:151-155` | Failed account lookups cached forever. | False "deleted" accounts. | Do not cache null; resolve local SIDs on the target. | M |
| M3 | Guards | `JobService.cs:84` | Protection by name only; null OsCaption unprotected. | Server reachable by IP/FQDN. | Also IP/FQDN/AD name. | S |
| M4 | Guards | `FleetControllers.cs` (`remove`) | `{all:true}` not server-confirmed. | One call wipes the inventory. | `expectedCount` + confirm token. | S |
| M5 | Guards | `ActionCatalog.cs:170-175` | Server-side profile-delete guards incomplete. | See prior. | Add typed account name, size in audit, default/public check. | M |
| M6 | Robustness | `WmiEndpointCollector.cs:21,80`, `MonitoringService.cs:127` | No per-host deadline; one slow PC holds the cycle. | Fleet polling delayed. | Abandon on timeout; do not await all. | M |
| M7 | Bug | `JobDetail.tsx:29`, `JobParts.tsx:77` | No resubscribe on reconnect; high-water-mark log merge. | Lost log lines. | Resubscribe + merge by seq. | S |
| M8 | Formatting | `ui.tsx:98`, `charts.tsx:57-62` | Locale dates. | Not `dd.MM.yyyy HH:mm` Yerevan. | Use `formatAbsolute`. | S |
| M9 | React | none | No error boundary. | One widget can blank the app. | Boundaries per route + shell. | S |
| M10 | Correctness | `EndpointDetail.tsx:114,120` | Chart alert lines fixed at 90. | Disagrees with Settings. | Use thresholds. | S |
| M11 | Selection | `Endpoints.tsx:395,400` | Delete-all count includes the pinned server. | Count mismatch. | Subtract pinned; prune selection. | S |
| M12 | A11y/bug | `Endpoints.tsx:372` | Enter on a nested control navigates away. | Keyboard trap. | Ignore when target ≠ currentTarget. | S |
| M13 | React | `ui.tsx:161`, `JobParts.tsx:97` | Stale-deps suppressions. | Wrong sort after live updates. | Fix deps, add ESLint. | S |
| M14 | Retention | `MonitoringService.cs:157-159` | Job logs/jobs never purged. | DB growth. | 90-day purge, audit kept. | M |
| M16 | Robustness | `HelperJobs.cs:37-40` | BadMac reported as Failed. | Reconnect race fails targets. | Retryable status, re-queue. | S |

### Low

| ID | Category | File:line | What's wrong | Suggested fix | Effort |
|---|---|---|---|---|---|
| N11 | Robustness | `HelperSupport.cs:115`, `Program.cs` (helper) | With the all-users startup, every user's helper retries every minute forever, including users with no role (a refusal each time, WARN in the server log). | After a role-refused answer, back off for an hour or stop. | S |
| N12 | Script | `installHelper.ps1:133, :84-` | `-StartNow` passes `/sd 01/01/2099`, which depends on the target's date format; only the C: drive is supported; no check that the helper really connected. | Use a locale-free trigger; read the system drive; optionally poll Nexora's status. | S |
| N13 | Security | `OperationsControllers.cs:36` | `serverUrl` comes from the request's Host header and is pasted into a copy-paste PowerShell command. | Validate against a configured public URL. | S |
| N14 | Guards | `JobContracts.cs` (token binding) | The confirm token binds action, targets and parameters but not the reason; the reason can differ between confirm and create. | Include a reason hash. | S |
| N15 | Docs/tests | `README.md:157` (`.installHelper.ps1`, backslash lost), `ActionModel.cs:50`, `e2e/jobs.e2e.mjs`, `e2e/profiles.e2e.mjs` | Typo; stale comment; two e2e suites need `demo.*` actions (hidden by default) or the old WKS-111. | Fix; make e2e seed their own data. | S |
| N16 | Robustness | `WmiDisksAndProfiles.cs:81` | NVMe health log is read only on the PC Nexora runs on, and "local" means the host name equals the machine name. Remote NVMe drives (3 in the fleet, 2 remote) rest on the Windows counter. | Helper-side read; match by IP/FQDN too. | M |
| N17 | Diagnostics | `HttpPipeline.cs` (`HelperAttemptMiddleware`) | Unauthenticated 401s are skipped, so a helper whose Windows sign-in fails leaves no server-side trace. | Count them at debug with the IP, or log after N consecutive. | S |
| L1 | Validation | `EndpointServices.cs:120` | `$` allows a trailing newline. | `\z`. | S |
| L4 | Robustness | `JobContracts.cs:188` | Regex timeout → 500. | Catch. | S |
| L5 | Leak | `JobOrchestrator.cs:35`, `HelperJobs.cs:17` | Sets only grow. | Remove on finish. | S |
| L6 | React | `realtime.tsx:114` | Interval not cleared. | Clear on unmount. | S |
| L7 | Forms | `Settings.tsx:48` | Cannot empty a number field. | Text state. | S |
| L8 | Race | `JobService.cs:165/169` | Token burned before heavy-job check. | Reorder. | S |
| L9 | Info leak | `EndpointServices.cs:178,189,353` | Raw WMI/LDAP text returned. | Generic message. | S |
| L10 | Secrets | `scripts/nexora.ps1:263` | DB password on a command line; plain files in `run/`. | `PGPASSWORD`, tighten ACL. | S |
| L11 | Hygiene | repo root | Repo is the home directory. | Own repo for `Desktop\Nexora`. | S |
| L13 | UX | `Dashboard.tsx` | No retry, 50-row cap, offline counts. | Retry + "showing 50 of N". | S |

## 4. Build, lint, tests

- `dotnet build Nexora.sln --no-incremental`: 0 warnings, 0 errors (`TreatWarningsAsErrors`, `CA1416` suppressed by design).
- `tsc --noEmit`: clean; no `any`/`ts-ignore` in `src/`. **ESLint: not configured** (no config, no `lint` script); two `exhaustive-deps` suppressions remain.
- Tests, two consecutive runs: Application 193/193, API 212/212, vitest 73/73. No failures, no flaky tests. Playwright e2e were not run (they need real data and, for two suites, demo actions).
- Packages: `npm audit` 0. Outdated (not updated): npm `@tanstack/react-virtual` 3.14.16 → 3.14.18, `@testing-library/user-event` 14.6.7 → 14.6.8; NuGet `FluentAssertions` 6.12.2 → 8.11.0, `Microsoft.NET.Test.Sdk` 17.12.0 → 18.10.1, `Serilog.AspNetCore` 9.0.0 → 10.0.0, `xunit.runner.visualstudio` 3.0.0 → 4.0.1. No vulnerable NuGet packages.
- Runtime log (`publish/logs`, today): 334 Error lines: 106+106 database-save errors and 54 "Failed to store poll result" (N3), 30 `OnDisconnectedAsync` (N4), 38 "Failed disposing connection" (same cause).

## 5. Work since the last audit: what I verified

| Area | Verdict | Notes |
|---|---|---|
| SSD/HDD classification and serial join | **Correct** | `DriveClassifier.FromFactsWithSource` priority bus → media → spindle → Unknown; `DriveMatching.Find` (serial → PnP → disk number, ambiguous level skipped); empty serial falls back; a drive that matches nothing keeps `N/A`. Fleet: 260 drives, 0 Unknown, 0 without serial, 0 duplicate keys, 0 null health. Gap: N3 (duplicate keys in a scan). |
| One shared worst-of helper | **Correct** | `DriveClassifier.Worst` is the only implementation; table, detail and CSV read it; the browser no longer recomputes. |
| 10-column CSV | **Correct, with caveats** | BOM, CRLF, quoting, guard, blanks-not-zeros and whole numbers tested; see N7. wks-111 96/100 and wks-44 65/100 verified live. |
| Friendly names + commands | **Correct** | All 49 codes tested against the table; shown in panel, dialog, Jobs list/detail, audit. |
| Risk-based confirmation | **Correct, with gaps** | Token required for every Medium+ action; High+ needs reason and typed host; Critical Admin-only; bulk pre-flight. Gaps: N14, L8. |
| Job output popup | **Not built** | See N9. |
| Helper lifecycle | **Mostly correct** | One address, backoff, single-instance mutex, status file, connect logging, push to the banner, self-contained download. Gaps: N1, N4, N11, N17, M16. |
| Disk audit page | **Correct** | Admin only, 404 otherwise; 0 PCs flagged on the live fleet. |

## 6. Verified OK (re-checked)

- Every controller carries a policy; SuperAdmin routes answer 404; anonymous gets 401; the new `DiskAudit`, `Thresholds` and `Helper` controllers are gated correctly.
- CSRF: Origin check and anti-forgery token on every unsafe `/api` call and the browser hub. SIDs are compared by value, case-insensitively. Confirm tokens are single-use and bound to SID, action, targets, parameters, override.
- `Features:CredentialOps` has no remaining enforcement point because the credential action is gone; the catalog test proves none exists (informational).
- No secrets in logs (output redaction, token files git-ignored).

## 7. Improvement ideas

| Idea | Value | Effort |
|---|---|---|
| Show a stale/failed-poll indicator per PC (age of the last good poll, last error) | High | S |
| Server-signed jobs with a pinned key (also gives revocation of helpers) | High | M |
| Helper version in the connect handshake, with an "update available" nudge | Medium | S |
| ESLint (`react-hooks`, `jsx-a11y`) and a CI script | Medium | S |
| Dashboard cards as filter links and the output popup (N9) | Medium | M |
| Helper-side NVMe SMART read | Medium | M |
| Batched SignalR + per-PC delta | High | M |
| Sign `Nexora.Helper.exe` | Medium | S (needs a certificate) |

## 8. Open questions

1. **N2:** how sure are you that attribute 169 raw is the remaining life on all ADATA SSDs? Is it acceptable to show Warning (not Critical) until 5-10 more PCs are checked against HD Sentinel?
2. **N1:** do you accept the helper trusting the server on plain HTTP for now, or should I restore a pinned key?
3. **N7:** collect the real logon time, or rename the "Last Logon" column?
4. **Age penalty:** hours of use no longer lower any score (58 HDDs at 40,000 h or more now read 100). Intended for the whole fleet?
5. **N9:** are the Job Output popup and the dashboard filter cards still wanted?
6. **N8:** implement script running, or remove the script catalog?
7. The audit's prior decisions (500/call delete cap, 10/2-minute queue TTL, 90-day job-log retention, immediate revocation) stand unless you say otherwise.
