---
description: Full Nexora pipeline for a feature or change — design, implement, test, security review
argument-hint: <what to build or change>
---

Deliver this Nexora change end to end: **$ARGUMENTS**

1. **Locate** — `Explore` agent (medium breadth): which files/projects are involved. Summary only.
2. **Design** — `nexora-architect` with the Explore summary. If it reports a constraint conflict, stop and show me the conflict and the compliant alternative.
3. **Approve** — show the plan (≤20 lines). Wait for my approval unless the change is a single-file fix with no authz/identity impact.
4. **Implement** — dispatch plan steps to `dotnet-dev` / `frontend-dev` / `ps-scripter`; steps on disjoint files run in parallel.
5. **Test** — `test-runner` on affected projects. On failure, send the failure summary back to the owning agent; max 2 fix rounds, then stop and report.
6. **Review** — `security-reviewer`. On NO-SHIP, fix blockers via the owning agent and re-review once.
7. **ADR** — if the architect drafted one, write it to `docs/adr/`.
8. **Report** (≤6 lines): what changed, tests, review verdict, pending human checkpoint (if any). Do not commit unless I asked.
