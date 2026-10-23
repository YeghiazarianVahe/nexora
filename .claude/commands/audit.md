---
description: Read-only professional audit of Nexora — bugs and security only, no fixes, no features
argument-hint: [scope: all | server | helper | frontend | scripts]
---

Run a read-only audit of Nexora. Scope: **$ARGUMENTS** (if empty: all). Change no source files.

1. Read the newest report in `docs/audits/` (if any) to know prior finding IDs and their status.
2. Run `security-reviewer` agents **in parallel**, one per area in scope — server (API, auth, hubs, pollers, data), helper (trust, job execution), frontend, scripts. Instruct each to review the area's code (not a diff), apply its full checklist, and return findings only.
3. Run `test-runner` once for a baseline (build + tests).
4. Merge and dedupe. Write `docs/audits/<YYYY-MM-DD>.md`:
   - Baseline: build/test result
   - Table: `ID | Sev (Critical/High/Medium/Low) | Area | path:line | Finding | Evidence | Proposed fix | Status`
   - New IDs continue the prior sequence; prior findings get `Resolved` / `Still open` / `Regressed`.
   - Section **Requires decision** for anything needing a design choice rather than a patch.
5. Reply with ≤8 lines: counts by severity, top 3 findings, new vs resolved since last audit.
