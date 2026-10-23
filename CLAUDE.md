# Nexora | IT Operations Platform

Internal endpoint monitoring + remote operations for Contoso IT Service Desk (9 operators, ~2,700 Windows PCs, head office). Pilot: `http://wks-111:5080`.

## Stack
.NET 10 / ASP.NET Core on HTTP.sys · PostgreSQL · SignalR · React + TypeScript strict · `Nexora.Helper` (.NET 10 operator-session helper) · `scripts/nexora.ps1` (PS 5.1).

## Hard constraints — never violate, never "temporarily" relax
1. **HTTP only.** No HTTPS redirect, no HSTS, no certificates.
2. **Windows Integrated Auth** (Negotiate via HTTP.sys). No login form, no app-handled passwords.
3. **No AD changes.** No service account, SPN, delegation, GPO, or AD writes.
4. **No WinRM / PS remoting.** Collection = agentless WMI/CIM. Actions = WMI process, SYSTEM scheduled task, or PsExec — dispatched through the helper.
5. **Operator identity.** Every remote action runs under the initiating operator's own token via `Nexora.Helper` and is audited as that operator. The server identity never performs privileged endpoint actions.
6. **Roles enforced server-side**: Viewer < Operator < Admin < SuperAdmin. SuperAdmin matched by SID, never by name. Access requests only for AD group "IT Service Desk".
7. **`Features:CredentialOps = false`** until HTTPS — enforced server-side.
8. **Endpoint scripts**: PowerShell 5.1-compatible, no external modules.

If a request conflicts with a constraint: stop, name the constraint, propose a compliant alternative. A hook enforces 1, 3, 4, 7, 8 mechanically.

## Delegation (overrides global defaults in this repo)
| Need | Agent |
|---|---|
| Locate code | `Explore` |
| Design, cross-module change, new dependency, ADR | `nexora-architect` |
| C# server, helper, data layer | `dotnet-dev` |
| React UI | `frontend-dev` |
| `.ps1` ops and endpoint scripts | `ps-scripter` |
| Builds, tests, logs | `test-runner` |
| **Any diff** touching auth, roles, helper, job dispatch, remote actions, audit log | `security-reviewer` (mandatory) |

Independent work on disjoint files → run agents in parallel.

## Human checkpoints — always stop and ask
- Helper trust model: signing, pinned server key, job signature/nonce verification.
- Deploying to live operator PCs, or touching any `\\wks*` share other than wks-111.
- DB migrations that drop or rename columns/tables.

Everything else may run unattended.

## Context sources (read on demand, not up front)
- Decisions: `docs/adr/` — read the relevant ADR before redesigning anything.
- Repo map: `docs/REPO_MAP.md` — regenerate with `/repo-map`.
- Open findings: newest file in `docs/audits/`.
- Operator PCs: `docs/operator-pcs.txt`.
- Drive-health reference PCs: wks-111 (SSD 96% / HDD 100%), wks-44 (SSD 65% / HDD 100%).

## Conventions
- C#: nullable on, async end-to-end, `CancellationToken` on all I/O, structured `ILogger`, Options pattern, no static mutable state, `CultureInfo.InvariantCulture` for parse/format (fleet runs mixed locales).
- WMI polling: bounded concurrency (`Channel<T>` + `SemaphoreSlim`), per-host timeout; one slow host must never stall the loop.
- Hardware identity: join drives by serial number, never enumeration order.
- UI: dark only — `#171717` / `#69BA31` / `#FFFFFF`.
- Tests: xUnit + FluentAssertions. Every security boundary has a negative test.

## Compact instructions
Keep: active task, constraint decisions, files touched, failing tests, open audit IDs. Drop: file contents, tool output, dead ends.
