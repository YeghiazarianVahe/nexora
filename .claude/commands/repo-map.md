---
description: Generate or refresh docs/REPO_MAP.md so agents stop re-exploring the repo
---

Use the `Explore` agent (very thorough) to map the Nexora repository, then write `docs/REPO_MAP.md` (≤80 lines):

- Solution layout: each project → one-line responsibility
- Where things live (`path` for each): authentication setup, role/policy definitions, access-request flow, SignalR hubs, WMI pollers, remote-action catalog, helper job protocol and dispatch, audit log, EF/DB migrations, `nexora.ps1`, frontend routes/pages, shared UI components
- Test projects and what each covers
- Build/test/run commands
- Config keys that matter (`Features:*`, polling, thresholds)

Paths and one-liners only — no code. Reply with one line when done.
