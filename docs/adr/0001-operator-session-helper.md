# ADR-0001: Remote actions run through an operator-session helper

Status: Accepted (seeded from design history — verify against code)

## Context
Every remote action must run with, and be audited as, the initiating operator's own AD identity. AD changes (service accounts, delegation) are not allowed.

## Decision
`Nexora.Helper` (.NET 10) runs in each operator's own Windows session with per-user silent auto-start, connects to Nexora over SignalR with Windows SSO, receives jobs from the server and executes them with the operator's token (WMI/CIM, SYSTEM scheduled task, or PsExec per action). The server never executes on endpoints. Interface: `IRemoteActionExecutor` + a defined job protocol.

## Consequences
- An action only works when the operator's helper is connected.
- Helper trust is the critical boundary: jobs must be authenticated (pinned server key), bound to the operator, time-limited, and replay-protected. A previous audit (N1) flagged a trust gap after signing was removed — changes here are a human checkpoint.

## Constraints honored
C3, C4, C5.
