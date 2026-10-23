# Nexora: full guide

Internal web application for real-time endpoint monitoring, remote control and IT operations, built for the
Contoso IT Service Desk (about 9 operators, designed to scale to 500-1000 Windows PCs).

* **HTTP only, no certificates.** Plain HTTP on a single Windows PC, no IIS.
* **Windows Integrated Authentication** (Kerberos, NTLM fallback) in Edge, Chrome and Firefox. No login form, no passwords.
* **No AD changes**: no service account, no new SPN, no delegation, no GPO.
* **Every operator action is authorized and audited as the initiating AD identity.** Fixed maintenance/script jobs may run as SYSTEM through an operator-authorized task; interactive operations use the operator's helper token. Collection uses the server identity or the fixed local telemetry task.
* Password reset and any credential feature are **off** (`Features:CredentialOps=false`, enforced server side).

```
 Browser (React SPA) ──HTTP, Windows SSO──►  HTTP.sys ──► Nexora.Api (ASP.NET Core, .NET 10)
        ▲  SignalR (live)                                   │  controllers · SignalR hubs · monitoring service
        │                                                   ├─► PostgreSQL (users, requests, audit, endpoints, jobs)
 Operator PC                                                ├─► WMI/CIM read-only polling of endpoints
 Nexora.Helper ◄── signed job (SignalR, operator's own SSO) ┘
   └─ runs the action with the operator's own token (CIM / PowerShell remoting / mstsc)
```

## Solution layout

| Project | Purpose |
|---|---|
| `Nexora.Domain` | Entities and enums (`Role`, `AccessRequestStatus`, ...). No dependencies. |
| `Nexora.Contracts` | Job protocol shared with the helper: `JobPayload`, `JobEnvelope`, `JobMac` (HMAC with a per-connection session key), `ProtectedNames`. |
| `Nexora.Application` | Use cases, authorization rules, DTOs, interfaces (`IRemoteActionExecutor`, `IEndpointCollector`, `IDirectoryLookup`). |
| `Nexora.Infrastructure` | EF Core + Npgsql (migrations checked in), WMI collector, read-only AD lookup. |
| `Nexora.Api` | HTTP.sys host, controllers, SignalR hubs, auth wiring, monitoring service, static hosting of the SPA. |
| `Nexora.Web` | React + TypeScript + Vite + TanStack Query + SignalR client. Builds into `Nexora.Api/wwwroot`. |
| `Nexora.Helper` | Console app that runs in an operator's own session and runs jobs for that operator with that operator's token. |
| `tests/` | `Nexora.Application.Tests`, `Nexora.Api.Tests` (authorization boundaries, fake Windows auth). |
| `scripts/` | `nexora.ps1` (operations), `configure-firefox.ps1`. |

## Quick start on an approved installation host

Requirements: Windows 11 domain-joined PC, .NET 10 SDK, Node 20+ (for the UI), PostgreSQL 16+ binaries (only `initdb`, `pg_ctl`, `psql` are used).

```powershell
cd Nexora
.\scripts\nexora.ps1 -Action Build              # builds the UI, publishes the API and the helper into .\publish
.\scripts\nexora.ps1 -Action Install            # ONE TIME, elevated: URL ACL + firewall rule (Domain profile)
.\scripts\nexora.ps1 -Action DbInit             # ONE TIME: private PostgreSQL cluster in .\run\pgdata (port 5434)
.\scripts\nexora.ps1 -Action Start
.\scripts\nexora.ps1 -Action Status
```

Install also configures the fixed local SMART task; Status reports its health and Uninstall removes only owned telemetry components. Test installation/removal on a disposable VM before release. See [operations](docs/operations-v1.md).

Then open **`http://wks-111:5090`** (the short host name, see [Browser setup](#browser-sso-setup)).

`nexora.ps1` actions: `Install`, `Uninstall`, `Build`, `DbInit`, `DbStart`, `DbStop`, `Start`, `Stop`, `Restart`, `Status`, `Logs [-Tail n]`.
Options: `-Port` (default 5090), `-InstallPath` (default `<repo>\publish`), `-DbPort` (default 5434), `-PgBin`.
Exit codes: `0` ok, `1` error. Paths are relative to the script. `Stop` asks the app to shut down through a loopback-only,
token-protected endpoint and falls back to killing the process after 15 s.

> **Default port 5090** (the app, the scripts and the helper default in `HelperDefaults.Server`). `Start` refuses to run on an occupied port and says who owns it.
> The same `-Port` must be given to `Install`/`Start`/`Stop`/`Status`.

### The database

`DbInit` creates a **private** PostgreSQL cluster inside `.\run\pgdata` (listening on `127.0.0.1:5434`, roles
`nexora_pg`/`nexora_app`, database `nexora`). It refuses to run if the folder already exists and never touches any other
PostgreSQL server on the machine. Generated passwords are in `run\db.secret` / `run\db.conn` (git-ignored). To use another
server instead, skip `DbInit` and set `ConnectionStrings__Nexora` (Npgsql format). Migrations are applied at startup
(`Nexora:Database:AutoMigrate`). The audit table has database triggers that reject `UPDATE`, `DELETE` and `TRUNCATE`.

### Configuration (`appsettings.json`, override with `Nexora__Section__Key` environment variables)

| Key | Default | Meaning |
|---|---|---|
| `Nexora:Url` | `http://+:5090/` | HTTP.sys URL prefix (the script sets it from `-Port`). |
| `Nexora:SuperAdmin:Sid` | your SID | **Authoritative** SuperAdmin identity, matched by SID. |
| `Nexora:SuperAdmin:Account` | `CONTOSO\demo.admin` | Label and sanity check only; a mismatch logs a warning. |
| `Nexora:AccessGroup` | `IT Service Desk` | Only members may request access (nested membership counts). |
| `Nexora:AccessGroupSid` | empty | Optional: the group's SID instead of its name. |
| `Nexora:Monitoring:Enabled` | `true` | Background WMI polling. |
| `Nexora:Actions:PermitPerMinute` | `20` | HTTP requests/minute per SID on endpoints using the jobs policy, including rejected submissions. Valid range 1–10000. Passive reads and SignalR are excluded. |
| `Nexora:DataPath` | `<app>\data` | Holds the DPAPI-protected job-signing key. |
| `Features:CredentialOps` | `false` | Keep `false` while serving plain HTTP. |

Thresholds, polling interval, concurrency (default 32) and the script catalog are edited in the UI under **Settings** (Admin and above).

## Roles

| Role | Can |
|---|---|
| Viewer | Dashboard, inventory, live status, own audit entries. |
| Operator | Viewer + authorized remote actions, diagnostics, approved scripts, remote sessions and group tasks. Profile deletion requires Admin. |
| Admin | Operator + settings, script catalog, add/remove endpoints, all audit entries. |
| SuperAdmin | Admin + **Role Management**. Exactly one, configured by SID, not assignable, cannot be demoted or revoked. |

First login: a member of "IT Service Desk" with no role gets an access request and a *Waiting for approval* page. The SuperAdmin sees a live
badge on **Role Management**, approves (with a role) or rejects (with a reason); the waiting page switches to the app by itself over SignalR.
Non-members see a static *Access denied* page and nothing is stored. Role Management endpoints answer **404** to everyone except the SuperAdmin.

## Workstations, groups, history and drive health

**Adding a workstation.** *Workstations > Add workstation* takes a **name** and an **IP address or host name** (or several lines of
`name, address`; or *Import from AD*). The address is what Nexora connects to; the name is just a label (it defaults to the address).
The PC's own reported name is shown too, which is handy when you added it by IP. Admins can rename, re-address, regroup or remove a
workstation from its page.

**Groups.** A group is a set of workstations for a specific job (a branch, a role, a pilot ring). A workstation can be in several.
Operators can **restart a service on a whole group** (a normal job on its online PCs: confirmation, reason and protected-PC checks like any other job; at most 500 PCs). Process kills, profile deletion and remote
sessions are one-PC-at-a-time.
sessions are deliberately one-PC-at-a-time.

**CPU and RAM history.** Every poll stores one sample per PC (kept 7 days by default, *Settings > Keep CPU/RAM history*). The workstation
page shows CPU and RAM charts for 1 hour, 6 hours, 24 hours or 7 days with a hover readout (and arrow keys), the 90% alert line, and a
*Table* view. The dashboard shows the fleet-wide average over time.

**Drive health (HD Sentinel style).** Every few minutes (*Settings > Drive health check every*, default 10) Nexora reads each physical
drive over WMI, read-only: Windows storage health, reliability counters (temperature, wear, power-on hours, error counts) and the raw
SMART table for ATA drives. It produces:

| Output | How |
|---|---|
| Health % and status | Model 7 separates condition percentage from operational fault severity. HDD smart-raw uses published multiplicative SMART weights; independent scoring remains selectable. Explicit uncorrectable/I/O faults remain visible and prevent OK. Existing critical-sector severity is independently configurable. Temperature, age and CRC do not reduce condition percentage. |
| Findings | Plain-language list, for example "Temperature 62 °C is critical (limit 60 °C)". |
| Temperature | Separate measured and last-known values. Default warning/critical limits: HDD 50/55 C, SSD 60/70 C, NVMe 70/80 C; valid drive limits take precedence. |
| Lifetime left | No failure-date promise. The UI shows rated endurance where supported; an older extrapolation field remains for API compatibility. |

A Warning or Critical drive raises a *Drive health* alert on the dashboard and the workstation list. Use *Read now* on the workstation page
to refresh immediately. Some drives or drivers expose less (for example NVMe has no ATA SMART table); the card shows what is available and
"No health data could be read" if the polling account lacks rights.

**User profiles (System Properties > Advanced > User Profiles).** The *User profiles* tab lists the profiles on a PC (account, folder, last use,
signed-in/system/roaming flags). **Delete** removes the profile folder and its registration, as the Windows dialog does. It is an action like any other:
Admin and above, typed confirmation, executed through the initiating operator's authorized helper, audited. Safeguards: system profiles and profiles
of signed-in users are refused, the SID *and* the folder must match, only user-account SIDs under `X:\Users` are accepted, and the helper re-checks all of
it. Deleted profiles cannot be recovered.

## Remote actions and the operator helper

The server never acts on endpoints with its own identity. An action works like this:

1. The operator confirms the action in the UI; the API validates it (role, anti-forgery token, rate limit, parameter allow-lists, protected workstations, single-use confirm token) and writes an audit entry ().
2. The server MACs a short-lived job (HMAC-SHA256 with a key that exists only for that helper connection, 2 min expiry, bound to the operator's SID) and pushes it **only** to the helper connection of that same operator. If the helper is offline the job waits; it never runs under another identity.
3. `Nexora.Helper`, running in the operator's Windows session and authenticated with the operator's Windows identity, verifies MAC, expiry, SID, replay and the action code, re-validates every parameter, then runs the action with the operator's own token (CIM/WMI over DCOM, admin share). Only codes registered in the helper can run.
4. The helper streams output and reports the result; only the initiating operator's helper is accepted. The server writes `job.target` to the audit log. WinRM is never used.

**Getting the helper onto an operator's PC.** Each operator needs the helper on their own PC. When it is missing, the *Jobs* page and every actions panel show a banner with a **Download helper** button (the server hands out a self-contained `Nexora.Helper.exe`; no .NET install is needed) and a **Copy install command** button. The command is one paste into PowerShell:

```powershell
$d = "$env:LOCALAPPDATA\Nexora"; New-Item -ItemType Directory -Force $d | Out-Null; iwr http://wks-111:5090/api/helper/download -UseDefaultCredentials -OutFile "$d\Nexora.Helper.exe"; & "$d\Nexora.Helper.exe" --server http://wks-111:5090 --install
```

`--install` registers the helper to start at every sign-in of **that user only** (an HKCU `Run` value; no administrator rights, nothing machine-wide) and starts it now. On the Nexora PC itself, `start.ps1 -InstallHelper` does the same for your user and `start.ps1 -UninstallHelper` removes it.

```powershell
Nexora.Helper.exe [--server http://wks-111:5090] [--hidden]   # run (the server defaults to the pilot; NEXORA_SERVER also works)
Nexora.Helper.exe --install [--server URL] [--no-run]          # autostart for this user
Nexora.Helper.exe --uninstall                                  # remove the autostart
Nexora.Helper.exe --status                                     # what the helper last told its operator
```

**Putting the helper on other PCs from yours: `scripts\deploy-helpers.ps1`.** Run `.\scripts\deploy-helpers.ps1` (the 11 operator PCs) or `-Computer WKS-44` (or `-Computer A,B,C`). Through the admin share and the remote Task Scheduler, with your own rights (no WinRM, no AD or GPO change), it downloads the helper from Nexora once (that copy carries the server key the helper checks every job against), copies it to `C:\ProgramData\Nexora\Helper` on each PC (read/execute for ordinary users, so nobody can swap the file), and sets up ONE autostart: a logon task for the Users group, or where Windows refuses that remotely, the all-users Startup shortcut. Either way the helper starts in the session of whoever signs in, with that person's own token: never yours, never SYSTEM. Nexora accepts only Operator and above, so anyone else's helper is refused. RunEx starts existing signed-in sessions where permitted; otherwise the helper starts at the next sign-in. Running it again updates the file (a running helper is renamed aside, not overwritten) and never duplicates the autostart. `-WhatIf` shows what would happen. `.\scripts\verify-helpers.ps1` then shows per PC: online, autostart, helper running, connected to Nexora, last seen, and the account it runs as, and flags any helper running as the wrong identity. The Admin page **Helpers** shows the same coverage live; the PC list is `Nexora:OperatorPcs` in `appsettings.json`. The older `.\installHelper.ps1 -Computer WKS-44` (Startup shortcut only) still works.

* **One address, one default.** The helper's server comes from `--server`, else `NEXORA_SERVER`, else `http://wks-111:5090`.
* **It never gives up.** The helper retries with growing delays (2 s up to a minute) and reconnects by itself after a network drop or a server restart; the banner and the top-bar indicator clear within seconds, without a page refresh (the server pushes the change).
* **When it cannot connect it says why**, in `%LOCALAPPDATA%\Nexora\helper.log` and `helper-status.json` (`--status` prints it): "Can't reach the Nexora server at <url>", "something else answered there (wrong port)", "Windows sign-in refused", or "your account has no Operator role".
* **The server logs every attempt** (`Helper connection attempt accepted|refused for <account> from <ip>: <reason>`). A request that never reaches the server (wrong port, no network) cannot appear there, so no log line for a PC that is supposed to connect means the helper is not started or points at the wrong address.
* **One helper per signed-in user**: a second start exits quietly.

* **Code signing:** provision the public trust stamp before signing; preserve signed bytes on download. See [signing workflow](docs/security-release-v1.md). Candidate helpers remain unsigned until authorized signing and endpoint-policy validation.
* The operator's account needs WMI/DCOM remote rights on the endpoints (WinRM is not used), exactly as for any admin tooling. Nexora does not grant them.

Ordinary WMI reads use the Nexora process identity. Native queries are read-only but create temporary protected directories/files, run target-local PowerShell, and check directory ACLs. Remote native reads use the authenticated DCOM caller; local privileged reads use the fixed SYSTEM task. See [storage model](docs/storage-health-v5.md), [operations](docs/operations-v1.md), and [release security](docs/security-release-v1.md).

## Browser SSO setup

Always browse to the **short host name**: `http://wks-111:5090`.

* **Not an IP address**: an IP forces NTLM (no Kerberos, and browsers will not auto-login).
* **Not an FQDN** (`wks-111.contoso.local`) unless that name is in the *Local intranet* zone.

**Edge / Chrome**: single-label host names are in the Local intranet zone by default, so SSO is automatic. Optional policy: `AuthServerAllowlist = wks-111`.

**Firefox**: add the host to `network.negotiate-auth.trusted-uris` and `network.automatic-ntlm-auth.trusted-uris` (about:config), or run for the current user (close Firefox first):

```powershell
.\scripts\configure-firefox.ps1                    # writes user.js in each profile; -HostName x, -Remove supported
```

or deploy `policies.json` (`distribution\policies.json` in the Firefox folder):

```json
{ "policies": { "Authentication": {
    "SPNEGO": ["wks-111"], "NTLM": ["wks-111"], "Delegated": [], "AllowNonFQDN": { "SPNEGO": true, "NTLM": true } } } }
```

## Security notes

* **HTTP.sys with `AllowAnonymous = true`.** The spec asks for `AllowAnonymous = false` *and* an anonymous `/health`; those conflict (the kernel rejects anonymous requests before the app runs). Authentication is therefore enforced by the app: every endpoint requires a signed-in user with a role except `/health`, `/api/me`, the access-request and anti-forgery endpoints, and the SPA shell (sign-in required). Unauthenticated requests still get the Negotiate/NTLM challenge.
* **CSRF** (cookie-less Windows auth is still CSRF-prone): same-origin only (no CORS), an `Origin` check on every state-changing request and WebSocket upgrade, and anti-forgery tokens on all `/api` writes and the browser hub.
* Authorization is enforced server side on every endpoint and hub method; hiding menu items is cosmetic.
* Security headers (CSP, `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy`), per-operator rate limit on actions, input validation and allow-lists on all action parameters, parameterized queries only (EF Core), RFC 4515 escaping on every LDAP filter value.
* **Traffic is not encrypted.** Anyone on the network path can read or alter it. The startup banner says so. Introduce HTTPS before enabling `Features:CredentialOps`.
* The antiforgery cookie is not `Secure` for the same reason.

## Troubleshooting

**Browser prompts for a password, or shows "Cannot sign you in".** The host name is not trusted for SSO: use the short name (above). Check the log: `.\scripts\nexora.ps1 -Action Logs` prints `Login ... authenticated via Kerberos|NTLM` once per user and scheme.

**Kerberos vs NTLM.** The log records which one each user got (also visible per user in *Role Management > Users*). NTLM works but means Kerberos failed. Checks, from the user's PC:

```powershell
klist get HTTP/wks-111        # should return a ticket; "KDC_ERR_S_PRINCIPAL_UNKNOWN" = SPN problem
setspn -L WKS-111             # the computer account normally lists HOST/wks-111, which also covers HTTP/
```

HTTP.sys authenticates in kernel mode as the machine account, so the machine's existing `HOST/` SPN is enough; no SPN or account is needed for Nexora. Duplicate SPNs (`setspn -X`) and browsing by IP or FQDN are the usual causes of NTLM.

**`Install`: "Access is denied" / error 5.** Run PowerShell as administrator. **Error 183 / "URL already reserved"**: another reservation exists for the port (`netsh http show urlacl`); remove it or pick another `-Port`.

**`Start`: "Port ... already in use".** HTTP.sys sockets show as PID 4 (`System`), so the owner can be another web app or a URL reservation. `netsh http show servicestate` lists them. Use `-Port` for a free one.

**Nexora exits at startup.** `.\scripts\nexora.ps1 -Action Logs`. Typical: `ConnectionStrings:Nexora is not configured` (run `DbInit`), database not running (`-Action DbStart`), URL ACL missing (`Install`).

**`Your Nexora helper is not running`.** Start `Nexora.Helper.exe` in your own Windows session; *Actions* shows its connection state.

**Endpoint shows offline with "RPC server unavailable" / "Access is denied".** WMI needs the *Windows Management Instrumentation (DCOM-In)* and *WMI-In* firewall rules, and the polling identity needs remote WMI rights on the endpoint.

**A user is told "Access denied" although they belong to IT Service Desk.** Membership is read from AD (direct or nested, including the Exchange distribution group) and cached for 5 minutes; sign-in again after the change has replicated. The log line `... is not a member of 'IT Service Desk'` confirms what the server saw.

## Tests

```powershell
dotnet test                                   # 150+ tests, no network or AD needed
$env:NEXORA_LIVE_AD = "1"; dotnet test        # also runs the read-only live AD checks (domain-joined PC)
```

The API tests replace Windows auth with a fake handler (SID and group claims from headers) and use an in-memory database. They cover
the Role Management 404 boundary, SuperAdmin immutability, role-less users, non-members creating no request, Operator vs Viewer actions,
audit entries for role changes, CSRF/origin checks, rate limiting, credential-ops gating and job signing.

## Known limits

* One SuperAdmin, one server. No high availability.
* Monitoring uses WMI/DCOM (agentless): good for hundreds of PCs on a LAN, not for WAN or heavy-latency links.
* Installed software and event-log queries are read live when an endpoint page opens, so they can take several seconds.
* `ResetPassword` exists only as a refused action: disabled by the flag, and "not implemented" even when enabled.

## Removing workstations

*Workstations* has a checkbox column for Admins: **Delete selected (n)** or **Delete all**, each behind a typed confirmation. API:
`POST /api/endpoints/remove` with `{ "ids": [...] }` or `{ "all": true }`. It deletes the workstations with their CPU/RAM and drive history and group
memberships; groups, scripts and the audit log stay, and the PCs themselves are untouched. One audit entry records it.

## Filters, sorting, export and bulk updates

* **Filters** on *Workstations*: manufacturer, model (narrowed by manufacturer), OU, group, status, drive health, and free-text search. Manufacturer and model come from WMI (`Win32_ComputerSystem`), so they fill in when a PC is first polled.
* **OU** is read from AD (one paged read-only query, refreshed hourly for PCs without one, and set immediately by *Import from AD*). It is shown root-first without the domain, for example `Branch A/Workstations`.
* **Sorting**: click any column header (again to reverse). Empty values always sort last. Also on group members, services, processes, software and profiles.
* **Export CSV**: everything shown (filters applied), or only the ticked rows. UTF-8 with BOM for Excel; cells that could run as formulas are neutralized.
* **Bulk updates** (Admin): tick rows, then *Add to group*, *Remove from group* or *Delete selected* (audited, typed confirmation for deletes).
* **Drive health calibration**: the score matches HD Sentinel on the reference PCs (WKS-111 96%, WKS-50 92%). A PC's drive health is its weakest drive.

## Workstations table, selection and export

* One line per row (46 px), fixed column widths, virtualized (TanStack Virtual) so thousands of rows stay fast. Select / Status / Endpoint stay pinned while the table scrolls sideways; the page itself never does. *Columns* hides optional columns (remembered in the browser).
* OU shows the last two segments (`BranchA › Workstations`), full path on hover, click to filter. Last seen is relative (amber when an online PC has been silent for 15+ min) with the exact `dd.MM.yyyy HH:mm:ss` (Asia/Yerevan) as tooltip. CPU/RAM/Disk bars are green below 70%, amber 70-89%, red from 90%.
* Selection is by endpoint id, so it survives sorting, filtering and live updates. The header checkbox selects the filtered view (indeterminate when partial); if the server has more matches than are loaded, the banner offers to select all of them.
* **Export CSV** goes through `POST /api/endpoints/export` with either `{ "ids": [...] }` or `{ "filter": {...} }` (exactly one; at most 5000 ids), plus an optional `sort`. `?preview=true` returns `{ mode, count }` without a file. Viewer and above; every export is audited (`endpoint.export` with mode and row count). The file is UTF-8 with BOM, CRLF, RFC 4180 quoting, text cells protected against spreadsheet formulas (a cell starting with = + - @, tab, CR, LF or a space gets a leading apostrophe), streamed as an attachment named `nexora-endpoints-{selected|filtered|all}-yyyyMMdd-HHmm.csv`.

Columns, in this order: `PC Name`, `IP Address`, `Logged-in User`, `Last Seen Online`, `CPU %`, `RAM %`, `SSD Free / Total`, `SSD Health %`, `HDD Free / Total`, `HDD Health %`. CPU and RAM are whole numbers (a snapshot at export time, blank for a PC that is offline or never polled); health is the worst SSD / HDD (NVMe counts as SSD) from the same classification and serial-number join as the table, blank when the PC has none of that type; space is free / total with one decimal in GB or TB (1 GB = 2^30 bytes, as Windows shows it), for example `316.5 / 475.1 GB`. **When a PC has several drives of one type, their volumes are added together.** `Last Seen Online` is `dd.MM.yyyy HH:mm` in Asia/Yerevan time: the last time Nexora saw the PC online (the poll does not record a logon time, so the column is not called one). `PC Name` is the host name the PC reported about itself (else the name Nexora reaches it by), not the display name. Nothing unknown is ever written as 0.
* Tests: `dotnet test` (export API), `npm test` in `src/Nexora.Web` (formatters, thresholds, selection), `npm run e2e` (Edge against the running server; screenshots go to `docs/ui/`).

## Remote actions as jobs (in progress)

Every remote action is a **job** that runs under, and is audited as, the initiating operator, through that operator's own helper. There is no WinRM: the planned executors use only WMI/DCOM, the remote Task Scheduler API (a one-time task as SYSTEM), admin shares and local tools. Targets always come from the inventory (`endpointIds`); the API has no host or IP field. Nothing is free-form: every action is a typed definition in code (`Nexora.Application/Jobs/ActionCatalog.cs`).

| Piece | What it does |
|---|---|
| `GET /api/jobs/catalog` | The catalog with `allowed` for the caller's role and `implemented`. |
| `POST /api/jobs/confirm` | For Medium+ actions: validates the request (and, for High/Critical, the reason and the typed hostname or target count) and returns a 5-minute, single-use token bound to the user, action, targets and parameters. |
| `POST /api/jobs` | Creates the job. Checks role, token, targets, parameter schema, protected endpoints and the one-heavy-job-per-target rule. |
| `GET /api/jobs`, `/{id}`, `/{id}/targets/{t}/log`, `/output`, `POST /{id}/cancel` | Viewers and Operators see their own jobs, Admins everyone's. |
| SignalR (`/hubs/nexora`) | `JobChanged`, `JobTargetChanged`, `JobLog`, `JobFinished` (toast). `SubscribeJob(id)` is allowed only for the initiator or an Admin. |
| Helper channel (`/hubs/helper`) | The server signs each target with an HMAC key created for that helper connection. The helper refuses bad MACs, expired payloads, other operators' jobs and action codes it does not know. Over plain HTTP the key is sent in clear text at connect, so this protects against forged jobs from other clients, not against someone who can sniff the connection; HTTPS closes that. |

Risk levels: Low runs at once; Medium asks for confirmation; High needs a reason and the typed hostname (or the target count for bulk); Critical is High plus the Admin role. Power actions and Critical actions are blocked on **protected** workstations (the Nexora server, server operating systems, and the *Protected hosts* list) unless an Admin overrides with a reason; the override is recorded on the job. Per-target output is stored line by line (obvious secrets such as `password=...` are masked first), hashed with SHA-256 at the end, and every job and every target result is written to the audit log.

Settings (Admin): *Targets of one bulk job at the same time* (default 10) and *Protected hosts*. Configuration: `Nexora:Jobs:Executor` (`Helper`, or `Fake` for tests/demos), `Features:DemoActions` (default off; when on, also lists the simulated `demo.*` actions and the planned catalog entries that cannot run yet), `TimeoutScale`, `TickMs`.

**Status:** the job model, API, SignalR channel, helper protocol and Jobs UI are in. Runnable today: services (start/stop/restart), end process, delete user profile, Remote Desktop/Assistance and profile sizes. The rest of the catalog is defined (friendly name, command, risk, role, parameters, timeout) but returns "not available yet" until its executor ships, and is hidden unless `Features:DemoActions` is on.

### Verifying step 1 on WKS-111

The demo actions run inside your own helper and change nothing on WKS-111.

1. `.\start.ps1`, open `http://wks-111:5090`, **Workstations** > **WKS-111**. *Remote actions* lists four demo actions with Low / Medium / High / Critical badges.
2. *Simulate a job (demo)*, steps 5, delay 700 ms, **Run**: the Jobs page opens with *Running*, a progress bar and live output; it ends *Succeeded* and a toast appears.
3. *Simulate a High-risk power job*: **Run** stays disabled until you give a reason and type `WKS-111`.
4. Run the demo with outcome `fail` (job *Failed*, **Re-run on 1 failed** appears) and with outcome `hang`, then **Cancel job** (ends *Cancelled*).
5. Stop the helper (`stop.ps1` stops it too), start a demo job: *Waiting for helper*; start the helper again and it runs.
6. **Jobs** shows all of them with filters; *Audit* has `job.create`, `job.target`, `job.cancel`.

Automated: `dotnet test` (job API, authorization, confirmation, concurrency, lifecycle, SignalR), `npm run e2e:jobs` in `src/Nexora.Web` (27 browser checks, screenshots in `docs/ui/jobs-*.png`).

## User profiles screen

*Profiles* (Operator and above) shows the profiles of any workstation, read over WMI (`Win32_UserProfile`): account (resolved read-only, otherwise `unknown account (deleted)`), SID, folder, **size**, last use (relative, exact time on hover), state (in use, system, roaming, corrupted) and the owning workstation. The **Nexora server is pinned**: it is always in the inventory (re-added at every start if it was ever deleted), listed first, cannot be removed by delete, bulk delete or delete-all, and always offered in the selector whatever the search says. (It used to disappear because the workstation list was capped at 2000 rows and sorted by display name; the cap is now 5000 and pinned PCs always come first.)

Sizes are measured on demand through the authorized helper without following links. Profile deletion and deletion batches are Admin-only, with typed confirmation, audit logging and server/helper safety checks for loaded, system, own and default profiles.

**Auditing the drive mapping.** Set `Serilog__MinimumLevel__Override__Nexora.Infrastructure.Wmi=Debug` (environment variable) and run "Read now" on a workstation: the log gets one `Disk mapping` line per drive with serial, bus, MediaType, SpindleSpeed, the chosen class, how the records were matched (serial, PnP id or disk number) and where the health came from.
