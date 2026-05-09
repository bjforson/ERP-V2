# Sprint 60 — Proposal (research + 3 candidates)

> **Status:** draft proposal. No commitment until user picks one.
> **Author:** research pass against `claude/optimistic-lederberg-4d9890`
> at `ca8d18f2`, 2026-05-09. Reads `ROADMAP.md`, `PLAN.md §22.7`,
> `docs/sprint-progress.json`, runbook 14 family, `git log -50`.
>
> **Companion docs:**
> - `ROADMAP.md` §3 (status), §4 (what's next), §6 (out-of-scope rules).
> - `PLAN.md` §22.7 (saturation milestone — most recent durable record).
> - `docs/sprint-progress.json` `prePilotProgress.operatorActions[]`.

---

## 1. State recap

Pre-pilot saturated at **48/27-41 sprints (117-178%)** at 2026-05-06
(`PLAN.md §22.7`; `sprint-progress.json:10`). All seven workstreams
α-ζ closed (`ROADMAP.md §4.1`). Sprints 53-59 added pilot acceptance
E2E, runbook 14 (1684 lines), Phase V perf live, cross-tenant audit
dashboard, license triage, NickPerf homegrown runner (closed SEC-DEP-3
P0), ScannerThresholdResolver RLS opt-in fix. Pilot is gated only by
**operator-side** work: 32 staged migrations, standby box, pgbackrest,
alerts, site lock, Phase V execution (`sprint-progress.json:27-35`).
Engineering at zero open decisions (`decisionsNeeded: []`). Tests
1128/1129 (1 pre-existing flake). Post-pilot scope deferred per
`ROADMAP.md §4.4` + §6 — ML arc (~30+) and v1-clone fold (~12-22).

---

## 2. Three candidate sprints

| # | Candidate | Value | Effort | Risk | Pilot-blocking? |
|---|---|---|---|---|---|
| C1 | **Engineering-side cutover dry-run** — exercise runbook 14 against a clean Windows host; smoke-pack the 32 staged migrations; productize the Phase V audit-execution playbook so operator can start same-day on site-lock | 5 | 2 | 2 | **Accelerates** — converts "operator reads runbook 14" into "operator runs scripted dry-run on day 1" |
| C2 | **Queueing platform graduation** — give the Sprint 14 / B-queues substrate (landed 2026-05-07, four commits `30a9d4c8`..`ca8d18f2`) a real production consumer beyond `SplitDetectionConsumer`; add ops dashboard on `queueing.queue_metrics`; run a soak-test under perf-seed load | 4 | 3 | 3 | No — but graduates a fresh platform before pilot traffic finds the rough edges |
| C3 | **NickFinance v1-clone fold-into-G2 — kickoff slice** | 3 | 4 | 4 | No — first chunk of the post-pilot ~12-22 sprint arc |

> Rejected candidates (kept here for traceability):
> - **§6.2 anomaly detection (DINOv2 + PatchCore)** — `ROADMAP.md` §6.4 explicitly defers post-pilot; needs GPU box (`operatorActions.gpu-box-availability`); shipping it pre-pilot violates the locked decision in `_resolvedThisSession[ocr-baseline-0pct-followups]`. Score: V5 / E7 / R5.
> - **NickHR v2-native refactor kickoff** — same post-pilot rule; v1-clone is good enough for pilot co-deploy (`ROADMAP.md` row 109). Score: V3 / E5 / R4.
> - **Observability / SLO sprint on `image.serve_ms`** — `image.serve_ms` foundation isn't the pilot's load-bearing telemetry; pilot acceptance gates already cover `gate.scanner.adapter` etc. (`docs/runbooks/14-pilot-site-standup.md` §1.1). Marginal. Score: V2 / E2 / R2.

### C1 — Engineering-side cutover dry-run *(rationale)*

Runbook 14 is operator-prose (1457 lines); no engineer has executed
it end-to-end on a clean machine. The 32 staged migrations
(`operatorActions.live-deploy-staged-migrations`) are disk-only.
SEC-DEP-3 is closed (Sprint 58 NickPerf) so Phase V perf is
unblocked, but the audit-checklist's ~89 SEC-* items are flat doc
with no runner. C1 turns the runbook into a rehearsal: a
`tools/cutover-dryrun/` script provisions a clean Postgres, applies
all migrations, runs `dotnet test --filter PilotAcceptance`, and
produces the `audit-{site}-{date}.md` artifact shape Phase V will
produce on-site. When the site is locked, the operator runs the same
script against production. Pure tooling — no domain code, no schema.

### C2 — Queueing platform graduation *(rationale)*

`NickERP.Platform.Queueing` landed in the four most recent commits
(`30a9d4c8`..`ca8d18f2`). One consumer in tree
(`SplitDetectionConsumer`), zero production traffic. ARCHITECTURE
§15.6 declares `queueing.queue_metrics` but the ops UI is absent.
C2 adds a second real consumer (audit-review queue —
`ReviewQueue.PostClearance` rows, Sprint 34), an `/admin/queues`
Razor page on `queue_metrics`, and a soak under `perf-seed` traffic.
Lands the sole-writer state machine at production-shape before
pilot traffic finds the edge cases (the v1 problem motivating the
substrate per ARCHITECTURE §15.1). Doesn't accelerate pilot launch.

### C3 — NickFinance v1-clone fold-into-G2 kickoff *(rationale)*

`ROADMAP.md` §4.4 names this as the likely first post-pilot sprint;
§3.2 row 108 confirms G2 pathfinder (Sprint 10) is the proven fold
template. C3 is the first slice of a 6-10 sprint arc: port one
read-mostly v1-clone surface to v2-native against G2 contracts.
Skipped because pilot-site lock is open — starting a 6-10 sprint
refactor before pilot proves stable doubles cutover surface.

---

## 3. Recommendation: C1 — engineering-side cutover dry-run

Pre-pilot is saturated (`PLAN.md §22.7`); shipping more features
risks adding unverified surface to a pilot not yet deployed. Marginal
engineering value right now is **reducing operator risk on cutover
day**, not new capability. A scripted, idempotent rehearsal of
runbook 14 is the highest-leverage thing between today and site
lock.

C1 de-risks the exact gates currently blocking pilot
(`operatorActions[]` lines 28-34): `live-deploy-staged-migrations`,
`provision-standby-box`, `install-pgbackrest`, `wire-backup-cadence`,
`wire-ha-alerts`. Each becomes a tested step instead of a runbook
paragraph. The audit-checklist (`docs/security/audit-checklist-2026.md`,
~89 SEC-* items) gets a runner instead of an "operator reads 89
items" cold start. Trade accepted: queueing platform stays at one
consumer (C2 deferred); v1-clone fold unstarted (C3 deferred). Both
are post-pilot — waiting a sprint is fine.

**Explicitly NOT doing:** no application features, no schema changes,
no RLS policies, no module work. C1 lives in `tools/`,
`docs/runbooks/`, `docs/security/`. Findings from the dry-run land as
separate fix-forwards — **not** scope creep into Sprint 60.

---

## 4. Draft sprint shape (C1)

**Sprint goal.** Convert runbook 14 + Phase V execution from
operator-prose-with-shell-fragments into a scripted, idempotent
rehearsal that the operator can run against the pilot site on day 1.

### Phases

```
A: tools/cutover-dryrun/ — clean Postgres provision + 32-migration apply
   │
   ├── B1: Phase V audit-checklist runner (markdown → ticked-artifact pipeline)
   │
   └── B2: Phase V perf-execution wrapper (NickPerf scenarios → site-scoped report)
                  │
                  ▼
   C: rehearsal smoke against scratch host + sign-off doc updates
                  │
                  ▼
   D: track commit + sprint-progress.json reconciliation
```

A is sequential predecessor for B1+B2 (which run parallel). C is the
soak run. D is the standard close-out shape.

### Work items

| Item | Effort | Branch | Deliverable |
|---|---|---|---|
| **A** Clean-host migration runner | 1.0 d | `sprint-60/phase-a-cutover-dryrun` | `tools/cutover-dryrun/run.ps1` provisions throwaway Postgres (PG17, locked per runbook 11), applies migrations from all three DBs in order, produces `dryrun-{date}-migration-report.md` |
| **B1** Audit-checklist runner | 0.75 d | `sprint-60/phase-b1-audit-runner` | `tools/security-scan/run-audit.ps1` walks `docs/security/audit-checklist-2026.md`, executes per-SEC-* commands where automatable, ticks the checklist, emits `audit-{site}-{date}.md` matching the §4.3 output shape |
| **B2** Phase V perf-execution wrapper | 0.75 d | `sprint-60/phase-b2-perf-runner` | `tools/perf/run-phase-v.ps1` runs NickPerf scenarios (Health + CaseCreate + EdgeReplay + 24h-backlog) against a target URL, produces `perf-{site}-{date}.md` matching the test-plan output shape |
| **C** Rehearsal smoke + runbook 14 amendments | 0.5 d | `sprint-60/phase-c-smoke` | Run A→B1→B2 on scratch host; capture artifacts; amend runbook 14 §4-§10 with "**operator runs `tools/cutover-dryrun/run.ps1`**" wherever a hand-stepped block currently lives |
| **D** Track + sprint-progress.json | 0.25 d | `sprint-60/phase-d-track` | Standard close-out: commit message `track: Sprint 60 shipped — pilot cutover scripted (rehearsal artifact pattern)`; reconcile sprint-progress.json |

Total: ~3.25 days wall-clock. Parallelism on B1/B2 brings it to ~2.5
days.

### Parallelization plan

A blocks B1 + B2 (both need a clean DB to verify against). B1 and B2
are orthogonal (different tools, different docs). C blocks D. Two
agents on B1+B2 in parallel; A and C are single-agent serial.

### Acceptance criteria

- `tools/cutover-dryrun/run.ps1` runs green from a clean PG17 install
  to all 32 migrations applied with the same 1099/1099 test pass on
  a live DB target.
- `tools/security-scan/run-audit.ps1` ticks every automatable SEC-*
  item; non-automatable items emit a `[manual]` line in the artifact.
- `tools/perf/run-phase-v.ps1` produces a perf artifact whose shape
  matches the existing Sprint 55 perf reports (`docs/perf/`).
- Runbook 14 §4-§10 references the new tools wherever a hand-stepped
  command block exists; no operator-prose deletions, only additions.
- `dotnet build` clean; `dotnet test` 1128/1129 (no new flakes).
- `git log` shows phase A→B1+B2→C→D commit chain matching the
  `sprint-NN phase X` convention (e.g. `Sprint 50 phase E:`).

---

## 5. Open questions for the user

1. **Pilot site lock status.** `sprint-progress.json operatorActions.pilot-site-selection` is still open. Does the user have an ETA, or should Sprint 60 assume a vendor-neutral target (matching runbook 14's vendor-neutrality)?
2. **HA box hostname / target environment.** The dry-run script needs a target — is there a staging HA pair to rehearse against, or should it use a Docker scratch Postgres?
3. **Pre-pilot vs. post-pilot accounting.** Sprint 60 lands after saturation. Does it count against the 27-41 estimate band (currently 48/27-41 = 117-178%), or does it open a new "pilot-readiness Sprint X" track? Suggest the latter, but defer to user.
4. **Operator availability for the rehearsal.** C-phase smoke benefits from the operator running it on their target hardware. Acceptable to defer the rehearsal to operator pickup, or should engineering own the first run end-to-end?
5. **Deferred candidate priority.** If C1 lands in &lt;3 days as estimated, should the remainder of the sprint flex into C2 (queueing platform graduation) as a stretch goal, or stay strict-scope to leave bandwidth for Phase V findings?
