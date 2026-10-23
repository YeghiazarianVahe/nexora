---
description: Prepare a Nexora.Helper rollout to operator PCs — stops at a human checkpoint before touching any live PC
argument-hint: [pc list | all]
---

Prepare a `Nexora.Helper` rollout to: **$ARGUMENTS** (if empty: all) (hosts from `docs/operator-pcs.txt`).

1. `test-runner`: build `Nexora.Helper` in Release and run its tests.
2. Verify the artifact matches the current helper trust model (see `docs/adr/0001-*`): signature/pinned key as the ADR requires. Report exactly what was checked.
3. Produce the rollout plan: version, file hash, target hosts in order — **wks-111 first**, smoke test, then the rest — install method (per-user silent auto-start), rollback steps.
4. **STOP.** Show the plan and wait for my explicit "deploy". Do not copy to any `\\wks*` share, run PsExec, or touch any live PC before that.
5. After approval: deploy wks-111 → verify the helper connects and runs one low-risk job → ask before continuing to the remaining hosts.
