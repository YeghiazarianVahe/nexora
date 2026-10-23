# Nexora re-audit 2026-10-13b: Phase 2 (fixes)

Source: `nexora-audit-2026-10-13b.md`. The audit's "Verified OK" section was not edited.
Checks at the end: `scripts\ci.ps1` (backend build with warnings as errors, `tsc`, ESLint, Application tests 230, API tests 289, vitest 104, script tests) and the four Playwright suites (confirm 10/10, endpoints 25/25, profiles 20/20, jobs 28/28).

## High

| ID | Status | What was done | Tests |
|---|---|---|---|
| N3 | Fixed | Duplicate `DiskKey` suffixed per scan, store is an upsert with one retry on 23505, failed scan recorded on the PC (`DiskScanError`) and not retried every cycle. | `DriveScanStoreTests` |
| N1 | Fixed | ECDSA P-256 server key (DPAPI, `data\server-trust.key`); public key stamped into the helper exe (`TrustStamp`), jobs signed by the server, helper verifies, nonce replay check. Session-key MAC removed. | `JobTrustTests` (forged/unknown key rejected), `ServerTrustTests` |
| N2 | Fixed | ADATA 169 only for ADATA, behind `DiskHealth:Adata169Enabled`, capped at Warning. Fleet Critical 36 -> 1. Weighted model with reasons and separate temperature in the same change. | `HealthModelTests`, `HealthReferenceTests` |
| H2 | Fixed | Queue TTL 10 min (2 min Power/Critical) from the last progress; role, protection, `Implemented` and inventory re-checked at dispatch. The end-to-end run found a bug in this fix (the SuperAdmin has no users row, so was skipped); fixed. | `QueueTtlTests` |
| H3 | Fixed | `script.run`: script body + SHA-256 in the signed job, helper re-checks the hash, one-off SYSTEM task over the admin share and remote Task Scheduler, UTF-8 output file streamed back, cancel, cleanup. No WinRM. Verified live against this PC (SYSTEM, UTF-8, exit code). | `ScriptRunTests`, opt-in `ScriptRunLiveTests` |
| H4 | Fixed | DTO build queries only the polled ids; `EndpointsUpdated` batched at most once a second as a list; dashboard counts are SQL aggregates; one cache write on the client. | `PollScalingTests` |
| H5 | Fixed | One transaction, chunks of 500, 500 explicit ids per call, `mode` + SHA-256 of sorted ids in the audit, `ExecuteDelete` for single delete. | `DeleteSafetyTests` |

## Medium

| ID | Status | Note |
|---|---|---|
| N4 | Fixed | SID/account captured at connect; the "helper dropped" push is sent first. `RevocationTests` |
| N5 | Fixed | One definition (`Mapping.HasDriveHealthAlert`: online and weakest drive Warning/Critical) for the card, the filter and the export. `DriveAlertDefinitionTests`, `Endpoints.test.tsx` |
| N6 | Fixed | `start.ps1`/`stop.ps1` only touch the helper of the current session. `tests\scripts\Test-Scripts.ps1` (two-helper case) |
| N7 | Fixed | `Last Logon` -> `Last Seen Online`; `PC Name` is the host name. `CsvHonestyTests` |
| N8 | Fixed | Same as H3; the script catalog stays. |
| N9 | Fixed | Job Output window and clickable dashboard cards (details in the report). `JobOutputDialog.test.tsx`, `jobLog.test.ts`, `Dashboard.test.tsx` |
| N10 | Fixed | At most 25 full drive scans per cycle (`Monitoring:MaxDiskScansPerCycle`). |
| M1 | Fixed | Live connections of a changed/revoked role are aborted; they reconnect with the new role or are refused. `RevocationTests` |
| M2 | Fixed | A failed account lookup is never cached; unresolved SIDs are resolved on the target (`Win32_SID`). `AccountResolutionTests` (no live test) |
| M3 | Fixed | Protected by FQDN, short name, display name, IP, protected list, server OS and unknown OS. `ProtectedHostTests` |
| M4 | Fixed | Delete-all needs a preview token for the exact count; changed inventory removes nothing. `DeleteSafetyTests` |
| M5 | Fixed | Profile deletion checked against what the PC reports; account name typed; default/public/system/loaded/own refused; size in the audit (client-reported). `ProfileGuardTests` |
| M6 | Fixed | Hard per-host deadline; a slow PC is abandoned, not awaited. The abandoned WMI call finishes in the background. `WithDeadline` test |
| M7 | Fixed | Resubscribe and re-read after reconnect; lines merged by seq. Unit-tested, not tested with a real network drop. |
| M8 | Fixed | `fmtDate` and charts use `dd.MM.yyyy HH:mm` Asia/Yerevan. `lib.test.ts` |
| M9 | Fixed | Error boundary around every route and around the app. `ErrorBoundary.test.tsx` |
| M10 | Fixed | Chart alert lines from Settings. |
| M11 | Fixed | Selection pruned; delete-all count from the server. `prune` test |
| M12 | Fixed | Enter on a control inside a row no longer opens the PC. `Endpoints.test.tsx` |
| M13 | Fixed | `useSort` follows live keys; both `eslint-disable` removed; ESLint added. `lib.test.ts` |
| M14 | Fixed | Finished jobs older than 90 days purged with their output; audit kept. `RetentionAndResilienceTests` |
| M16 | Fixed by design | The MAC bound to the connection no longer exists (N1). A signature failure is permanent, so it is reported as a failure with the fix, not retried. |

## Low

N11, N13, N14, N15, N17, L1, L4, L5, L6, L7, L8, L9, L10, L11, L13: Fixed (tests listed in the report). N12: Fixed (locale-free XML task, real system drive, optional connect check). N16: Partial (local match by IP/FQDN; remote NVMe cannot be read without an agent).

## Helper rollout (not an audit finding)

`scripts\deploy-helpers.ps1`, `scripts\verify-helpers.ps1`, Admin page **Helpers** (`Nexora:OperatorPcs`), windowless `WinExe` helper, `installHelper.ps1` as a thin wrapper.
