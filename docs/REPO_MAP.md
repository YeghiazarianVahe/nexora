# Nexora repo map (generated 2026-10-14; regenerate with /repo-map)

## Solution (Nexora.sln)
- `src/Nexora.Api` — ASP.NET Core on HTTP.sys: controllers, hubs, auth, middleware, JobRunner + MonitoringService; serves SPA from wwwroot
- `src/Nexora.Application` — business services, DTOs, options, action catalog, job orchestrator, abstractions
- `src/Nexora.Domain` — entities and Role enum
- `src/Nexora.Infrastructure` — EF Core/Npgsql, migrations, agentless WMI collectors, AD lookup
- `src/Nexora.Contracts` — types shared by server and helper (job protocol, defaults, protected names)
- `src/Nexora.Helper` — operator-session helper; runs signed jobs under the operator token
- `src/Nexora.Web` — React + TS SPA (Vite); not in .sln
- `tests/Nexora.Api.Tests`, `tests/Nexora.Application.Tests`, `tests/scripts/Test-Scripts.ps1`

## Where things live
- Authentication: `src/Nexora.Api/Program.cs` (HTTP.sys scheme), `Api/Auth/NexoraClaimsTransformation.cs`, `Api/Auth/AccessGroupService.cs`
- Roles/policies: `Api/Auth/Authorization.cs`, `Domain/Role.cs`, `Application/SuperAdminChecker.cs` + `SidRules.cs` (SID match), `Application/Services/UserAccessCache.cs`
- Access requests: `Application/Services/AccessRequestService.cs`, `RoleManagementService.cs`, `Application/MeService.cs`, `Api/Controllers/AccountControllers.cs`, UI `Web/src/pages/RoleManagement.tsx`
- SignalR hubs: `Api/Hubs/Hubs.cs` (NexoraHub /hubs/nexora, HelperHub /hubs/helper); client `Web/src/realtime.tsx`, `Helper/HelperJobs.cs`
- HTTP pipeline (CSRF, headers, helper attempts): `Api/Http/HttpPipeline.cs`
- WMI pollers: `Api/Services/MonitoringService.cs` (loop); `Infrastructure/Wmi/` (`WmiEndpointCollector`, `WmiDisksAndProfiles`, `WmiHardwareInventory`, `NvmeSmart`); scoring `Application/Services/DiskScoring.cs`, `DiskHealth.cs`, `DiskAudit.cs`
- Remote-action catalog: `Application/Jobs/ActionCatalog.cs`, `ActionModel.cs`; client `Web/src/jobs/catalog.ts`; protected hosts `Contracts/ProtectedNames.cs`
- Helper job protocol/dispatch: `Contracts/JobProtocol.cs`; `Api/Services/ServerTrust.cs` (signing key); `Application/Jobs/JobOrchestrator.cs`, `JobService.cs`, `JobContracts.cs`; `Api/Services/JobsRuntime.cs`; `Api/Controllers/JobsController.cs`, `OperationsControllers.cs`; helper side `Helper/JobExecutor.cs`, `HelperSupport.cs`, `Helper/Actions/*`; ADR `docs/adr/0001-operator-session-helper.md`
- Audit log: `Domain/Entities.cs` (AuditEntry), `Application/Services/AuditService.cs`, AuditController in `AccountControllers.cs`, UI `Web/src/pages/Audit.tsx`
- EF/DB: `Infrastructure/Persistence/NexoraDbContext.cs`, `Persistence/Migrations/`, `Infrastructure/DependencyInjection.cs`
- `scripts/nexora.ps1` (Install/Build/Db*/Start/Stop/Restart/Status/Logs); also `scripts/ci.ps1`, `deploy-helpers.ps1`, `verify-helpers.ps1`, `configure-firefox.ps1`; root `start.ps1`, `stop.ps1`, `restart.ps1`, `installHelper.ps1`, `nexora.config.ps1`
- Frontend routes: `Web/src/App.tsx` (dashboard, endpoints[/:id/:tab], groups, profiles, jobs[/:id], audit, settings, admin/disk-audit, admin/helpers, roles)
- Frontend pages: `Web/src/pages/` (+ `pages/endpoint/` tabs), `Web/src/jobs/` (job pages, dialogs, output parsers)
- Shared UI: `Web/src/ui.tsx`, `toasts.tsx`, `charts.tsx`, `api.ts`, `types.ts`, `styles.css`, `components/` (EndpointParts, EndpointPicker, ErrorBoundary, DriveHealth, DisksOverview), `lib/`

## Tests
- `tests/Nexora.Api.Tests` — integration (NexoraFactory): access/roles/revocation, trust + job guards, queue TTL, delete safety, exports, disks, polling scaling, retention, migrations on isolated Postgres
- `tests/Nexora.Application.Tests` — unit: disk health/scoring, job + helper actions, script run, protected hosts, SuperAdmin checker
- `tests/scripts/Test-Scripts.ps1` — PowerShell script tests
- `src/Nexora.Web/src/**/*.test.ts(x)` — vitest; `src/Nexora.Web/e2e/*.mjs` — Playwright suites

## Commands
- Full gate: `powershell -File scripts\ci.ps1` (build warnings-as-errors, tsc, eslint, both dotnet tests, vitest, script tests)
- Backend: `dotnet build Nexora.sln`; `dotnet test tests\Nexora.Application.Tests`; `dotnet test tests\Nexora.Api.Tests`
- Frontend (in `src/Nexora.Web`): `npm run dev|build|typecheck|lint|test|check|e2e`
- Run: `.\start.ps1` (`-InstallHelper`/`-UninstallHelper`), `.\stop.ps1`, `.\restart.ps1`; helper rollout via deploy-helper skill (human checkpoint)

## Config
- Files: `src/Nexora.Api/appsettings.json` (only one; env override `Nexora__Section__Key`); defaults in `Application/NexoraOptions.cs`; helper defaults `Contracts/HelperDefaults.cs`; operator PCs `docs/operator-pcs.txt`
- `Features:CredentialOps` (must stay false), `Features:DemoActions` (default false)
- `Nexora:Url|PublicUrl|AccessGroup|AccessGroupSid|SuperAdmin:Sid|OperatorPcs|Database:AutoMigrate`
- `Nexora:Monitoring:*` — ConnectTimeoutSeconds, MaxDiskScansPerCycle, InventoryTtlMinutes
- `Nexora:Jobs:*` (JobsOptions in `JobOrchestrator.cs`) — Executor, QueueTtlMinutes, PowerQueueTtlMinutes, RetentionDays, CancelGraceSeconds
- `DiskHealth:*` — HealthWeights in `DiskScoring.cs` (Adata169Enabled, ReallocatedTiers)
- DB-stored, editable in Settings UI (`Application/Dtos.cs`): PollIntervalSeconds, MaxConcurrency, DiskPollMinutes, JobParallelism, ProtectedHosts, CPU/RAM/Disk alert + health thresholds

## Docs / known drift
- ADRs `docs/adr/`; audits are in `docs/audit/` (CLAUDE.md points to empty `docs/audits/`)
- Port is 5090 (`nexora.config.ps1`, appsettings); CLAUDE.md still says 5080
