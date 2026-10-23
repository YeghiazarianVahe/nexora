# ADR-0006: Deterministic diagnostic engine (F5, "Diagnose")

Status: Accepted (draft approved by the product owner). **Dormant by default** (`Features:Diagnostics = false`); a shipped helper refuses every collector until the
steps in "Before this can be switched on" are done and approved.

## Context
Service-desk operators answer "Outlook freezes" and "the PC is slow" by running the same dozen checks by hand. We want Nexora to run those checks on one PC,
apply the desk's rules, and show the likely causes with evidence and suggested next steps. Hard constraints still hold: no WinRM, the work runs on the endpoint only
through the operator's own helper under the operator's token, the server never executes anything on a PC, every action is audited, roles are enforced on the server.

An LLM is deliberately **not** used. The answer must be reproducible, explainable ("rule X fired because value Y"), testable with fixtures, and must work on-prem with no
model. The result model is versioned and kept free of presentation text so that an on-prem LLM summary can later be added **next to** the findings (as an extra field of
a newer schema version) without changing what the rules decided.

## Decision

### Symptom profiles are data
`src/Nexora.Application/Diagnostics/Profiles/*.json`, embedded in the server assembly (never read from disk or a request). A profile is
`{ id, name, description, version, collectors:[{id, required, timeoutSeconds}], rules:[...] }`. Collector scripts are
`src/Nexora.Application/Diagnostics/Collectors/<id>.ps1` (PowerShell 5.1, read-only), embedded the same way; the id is the file name. Their output schemas are
documented in `docs/diagnostics/collector-schemas.md`. Profiles may contain `//` comments (used for the values someone must maintain, such as the Office build threshold).

A rule is `{ id, cause, severity: info|warning|critical, confidence: high|medium|low, when: Condition, evidence:[{label, path, format?, join?}], actions:[...] }`.
A condition is `{all:[...]}`, `{any:[...]}`, `{not: cond}` or a leaf `{path, op, value?, join?}` with the operators
`eq ne gt gte lt lte in contains exists countGt anyMatch`.

**Paths** address the collector JSON: `outlook-files.files[*].sizeBytes`. The first part is the source (a collector id, or the server-side source
`server:diskHealth`); then property names, each optionally followed by `[*]` (every element of an array) or `[field=value]` (the elements whose field equals a
literal, `files[kind=ost]`). Nothing else exists: no indexes, expressions, functions or reflection. **`join`** keeps only the elements of the path's last array step
whose `key` property equals one of the values at another path (`volumes` whose `letter` is the OST's `driveLetter`), which is how a rule relates two collectors.

**Evaluator safety.** It only walks `JsonElement`s along a parsed path. A path visits at most 2,000 elements per step (a bigger array is cut, never walked in
full); conditions are limited to depth 8 / 64 nodes, rules to 100 per profile, `in` lists to 50; numbers (JSON numbers or numeric strings) are parsed with
`CultureInfo.InvariantCulture` and NaN/infinity are refused; `anyMatch` patterns come only from the profile, are compiled once with a 50 ms match timeout, and a timeout counts as "unknown", not as a match.
Evaluation is **three-valued** (true / false / unknown): missing data, a missing property, a wrong type, or a collector that did not deliver is *unknown*, `not(unknown)` is unknown,
and only *true* fires a rule. An existing but empty array is a real "no". An unexpected exception in one rule marks that rule "not evaluated"; it never aborts a run.

**Fail fast.** All profiles are validated when the server starts (`Program` resolves them before the database is touched): unknown property, unknown collector id, a
path whose source is not a collector of that profile, unknown operator, bad path, invalid regular expression, duplicate rule id, unknown or demo catalog action code, and
catalog action parameters that the normal `ParameterValidator` would refuse all stop the start with a message that lists every problem.

### Engine and result
`IRuleEngine.Evaluate(profile, CollectorResult[]) -> DiagnosisResult { schemaVersion, profileId, profileVersion, complete, findings[], collectors[], notEvaluated[] }`.
A finding is `{ id (= rule id), cause, confidence, severity, score, evidence:[{label,value}], actions[] }`, sorted by
`score = confidenceWeight x severityWeight` (high/critical 3, medium/warning 2, low/info 1; 1-9), ties by rule id. A collector status is `ok | incomplete | failed | missing`.
`complete` is true only when every collector of the profile is `ok`. A rule that reads a **required** collector which is not `ok` is listed in `notEvaluated`
("insufficient data"); a rule that reads only optional ones is simply silent. An incomplete collector makes the run **Incomplete**, not Failed; its partial output is kept
as evidence for the operator but the rules never read it.
Evidence values are built by the server from the data (text, bytes, percent, count, number), control and bidirectional-override characters are removed, and each value is capped at 260 characters.
A recommended action is either `catalog` (an existing, non-demo catalog code with allow-listed, validated parameters) or `manual` (plain text). **The diagnosis never launches anything**: a catalog
action is started by the operator through the normal job flow (role, confirmation, reason, protected-host and audit rules apply there).

### Run flow
`POST /api/endpoints/{id}/diagnose { profileId }` (Operator policy, antiforgery like every state-changing call, rate limit "jobs", **unknown request fields are rejected with 400**)
creates one `diag.collect` job through the normal pipeline: single target from the inventory, the initiating operator's own helper, queue TTL, cancel, job audit,
denial audit (`job.denied`) for every refusal (flag off, role, unknown profile or workstation, protected workstation, no collectors, a run already in progress on that PC).
The generic `/api/jobs` endpoints keep refusing `diag.collect` (its catalog entry is not `Implemented`), so the only way to create one is this endpoint. The collector set comes from the profile; the
Exchange hosts come from the Admin setting `PlatformSettings.DiagExchangeHosts` (list of host names, at most 5, validated like the other settings, a save that omits it keeps the stored list), never from the request.
Protected workstations (the Nexora server, server OS, the protected list, unknown OS) are refused for every role; there is no override for a read-only collection.

The job row keeps only `{profileId, runId, collectorIds, exchangeHosts}`. At dispatch `HelperJobExecutor` adds `Parameters["collectors"]` = JSON
`[{id, sha256, body, timeoutSeconds}]` (the shape the helper's `diag.collect` action already expects; body at most 20,000 characters, SHA-256 of its UTF-8 bytes) to the **signed** job, replacing anything of that name.
When the target finishes, `JobService.CompletedAsync` calls `DiagnosisCompletion`, which parses the stdout lines `##NXC <id> <json>` (see below), adds the server-side `server:diskHealth` source
(the stored F4 drive health of the PC), evaluates, stores the run, audits it (`diag.complete`: operator SID/account, target, profile, status, rule ids that fired, collector statuses, failures included; no paths or add-in names)
and sends `DiagnosisCompleted(runId)` to the job's SignalR group (`HubGroups.Job(jobId)`, which the initiating operator joins with the existing `SubscribeJob`). A failed, cancelled, timed-out or skipped job is a `Failed` run.

**Output parsing is hostile-input parsing.** Only lines starting with `##NXC ` count. An id the profile did not ask for (including `server:diskHealth`) is ignored; a repeated id makes that
collector incomplete; an oversize (> 8,000 characters), truncated, non-JSON or non-object line makes its collector incomplete; JSON depth is limited to 16.

### Storage
Additive migration `AddDiagnosisRuns`: `diagnosis_runs (Id, EndpointId FK cascade, JobId, ProfileId, ProfileVersion, InitiatedBySid, Status, Complete, StartedUtc, FinishedUtc, ResultJson jsonb, EvidenceJson jsonb, SchemaVersion)`;
`EvidenceJson` holds the raw collector outputs, capped at 200 KB. Retention reuses `Jobs:RetentionDays` (90): the job purge deletes a run together with its job. The audit log is never purged.

### Read API
All Operator policy (findings contain file paths and add-in names, so a Viewer gets 403): `GET /api/diagnose/profiles`, `GET /api/diagnose/runs/{id}`, `GET /api/diagnose/runs/{id}/evidence`
(`{ collectorId: object }`), `GET /api/endpoints/{id}/diagnose/runs` (latest 50: `findingCount`, `topSeverity`). Unknown ids are a plain 404 with a generic message. The reads work with the flag off (the history is empty).

### Dormant by default, and the safety interlock
`Features:Diagnostics` (default `false`) gates the POST (501 with a clear message, audited as `job.denied`) and lets the orchestrator dispatch a `diag.collect` job at all.
**Even with the flag on, a shipped helper refuses every collector**, because the helper's compiled allow-list `DiagCollectors.Ids` is empty and the job is refused before anything is written to the PC.
That list is the interlock: filling it is a deliberate act and a test asserts that it is empty in this batch. Turning the flag on therefore changes nothing on real PCs; the full flow is exercised only against the `Fake` job executor
with canned `##NXC` lines.

### Before this can be switched on (each is a separate, approved change)
1. Fix audit finding **N19** (the helper side of collector handling) and re-review `DiagCollectAction`.
2. Add an **id -> SHA-256 pin** inside the helper (the helper must know which body it accepts for each id, not only that the signed job's body matches the signed hash). This is a change to the helper trust model:
   **human checkpoint** (CLAUDE.md), together with filling `DiagCollectors.Ids`.
3. Review the collector scripts again under the final helper (they run as SYSTEM through a one-off task) and verify them on the reference PCs.
4. Maintain the data that cannot be derived: the Office build threshold in `outlook-freeze.json` ("older than about 6 months" is a build number because the PC reports a build, not a date) and the known-slow add-in list.
5. Only then set `Features:Diagnostics = true` and deploy helpers (deployment to operator PCs is a human checkpoint).

## Consequences
- Reproducible and testable: every shipped rule has a fixture case that fires it, and the all-negative fixtures fire nothing.
- Adding a rule or a profile is a data change (plus a test case), but a new collector id also needs its script, its hash pin and a helper release.
- Some facts cannot be read from a PC and are not guessed: the number of shared mailboxes cached, the indexing backlog, the build's release date, and which physical drive holds the OST (the `server:diskHealth` rules therefore speak of "a drive in this PC").
- The cost is a small DSL to maintain. It is closed on purpose (no scripting), which is what keeps profile data safe to review.

## Alternatives considered
- Rules in C#: simpler to write, but every threshold change is a code change and the rules cannot be validated as data.
- A general expression language or embedded script engine: far more power than the rules need and a new attack surface.
- An LLM deciding causes: not reproducible, not testable, and not allowed to see operator data off-box. A local LLM may later *summarise* the deterministic result.

## Hardening after security review
- **Hostile text in collector output.** jsonb refuses U+0000 and unpaired surrogates, and reading such an escaped string throws. The `##NXC` JSON text is cleaned before parsing (NUL escapes dropped, lone surrogates become U+FFFD) and the parsed value is rebuilt the same way before it is stored as evidence. If a save still fails, the run is marked Failed with the evidence removed and every other pending change forgotten, so a run cannot stay "Running" because of its own content (test on a real PostgreSQL).
- **One run per workstation.** The 409 check ignores a Running run whose job target is final or gone, or that is older than the job timeout plus 15 minutes, and heals it to Failed (audited). A partial unique index `UX_diagnosis_runs_running` (`EndpointId` where `Status = 'Running'`, additive migration `AddDiagnosisRunningIndex`) makes the database settle a start race; the loser gets 409.
- **Insufficient data.** `network-probe` is a required collector and the Exchange rule has `"needs": ["network-probe.targets[*]"]`: an incomplete probe (reason "no targets" when no Exchange host is configured) or an empty target list makes the rule *not evaluated*, never "no problem". A rule's `needs` paths must resolve to at least one value.
- **Only implemented catalog actions** can be recommended (the load fails otherwise); `diag.collect` itself is not a recommended action.
- **Evidence reads are audited** (`diag.evidence.read`: operator, run id).
- **`exchangeHosts` is not yet delivered to the collector.** The server stores it in the job and forwards it as a job parameter, but the helper's `diag.collect` does not pass it to the script, and doing so safely needs a typed, validated value inside the signed job. That is a helper trust-model change (human checkpoint) for the next batch; until then the probe reports "no targets".
- **Collector time budget.** The scripts use their default `BudgetSeconds` (25 s) and the helper does not pass the parameter. Every profile `timeoutSeconds` is at least 30, so a script ends itself with a partial result (`incomplete`) before the helper kills it.
- **An operator can fabricate their own results.** The output comes from the operator's own helper, so that operator can make up collector data for their own diagnosis. Accepted: they gain no privilege (the result drives suggestions only, nothing runs from it) and the run is audited under their SID.
- **Names in the data.** E-mail addresses inside OST/PST file names are scrubbed to `<email>` by the collector; Windows user names stay, because operators already see accounts on the Profiles page.
