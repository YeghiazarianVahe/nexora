---
description: Fix specific Nexora audit findings with regression tests
argument-hint: <finding IDs, e.g. N3 N7>
---

Fix audit finding(s) **$ARGUMENTS** from the newest report in `docs/audits/`.

For each ID, in severity order:
1. Read only that finding's row and the cited code.
2. If it is under **Requires decision** or touches helper trust → stop and ask me.
3. Owning agent (`dotnet-dev` / `frontend-dev` / `ps-scripter`): write a regression test that fails first, then the minimal fix.
4. `test-runner` on the affected project.
5. `security-reviewer` if the fix touches auth, roles, helper, dispatch, remote actions or audit.
6. Update the finding's Status to `Fixed (<short reason>)` in the audit file.

Report ≤2 lines per finding. Do not commit unless I asked.
