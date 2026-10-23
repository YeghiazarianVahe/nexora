# ADR-0003: Agentless WMI/CIM collection, no WinRM, no AD changes

Status: Accepted (seeded from design history — verify against code)

## Context
WinRM is restricted by GPO in the bank, and enabling it or adding delegation would require AD/GPO changes.

## Decision
Monitoring polls endpoints over WMI/CIM (DCOM) from a background `IHostedService` with bounded concurrency (`Channel<T>` + `SemaphoreSlim`, default 32) and per-host timeouts; deltas are pushed to SignalR groups. Remote execution uses WMI process creation, a SYSTEM scheduled task via the admin share, or PsExec — always via the helper (ADR-0001). A WinRM/delegation-based redesign was proposed and rejected.

## Consequences
- DCOM/RPC must be reachable from operator PCs to endpoints.
- Poll loop must tolerate slow or offline hosts without dropping other polls (see audit N3).

## Constraints honored
C3, C4.
