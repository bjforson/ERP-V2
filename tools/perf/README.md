# `tools/perf/` — Phase V perf-execution wrapper

Sprint 60 Phase B2 deliverable. Wraps the NickPerf homegrown runner
(`tests/NickERP.Perf.Tests/`) into a single PowerShell entry-point the
operator runs on day 1 of the pilot stand-up. See `PLAN.md §23.2` work
item B2 for the canonical spec, `docs/perf/test-plan.md` for the perf
plan-of-record, and `docs/perf/baseline-2026-05-06.md` for the output
shape this wrapper mirrors.

## What this is

The four Phase V scenarios — `health` + `case-create` + `edge-replay`
+ `edge-replay-backlog` — were individually invokable since Sprint 55
(`dotnet run --project tests/NickERP.Perf.Tests -- <scenario>`) and
re-licensed onto NickPerf in Sprint 58. They produced one
`report.md` per scenario in `tests/NickERP.Perf.Tests/bin/...`.

`run-phase-v.ps1` adds the missing piece for a Phase V execution day:

- Orders the scenarios (Health first as warm-up, 24h-backlog last as
  the heavy run).
- Pre-flight probes `/healthz/live` so unreachable targets fail fast.
- Reads each scenario's emitted `report.md`, lifts the latency stats,
  and aggregates to `tools/perf/reports/perf-{site}-{date}.md` —
  one artifact per Phase V run, easy to share / diff / sign-off on.
- Maps each scenario's exit code to a verdict (`PASS` /
  `PASS-with-warn` / `SKIP` / `BLOCK` / `FAIL` / `FATAL` /
  `RUNNER-FAIL`) and exits non-zero if any non-informative scenario
  breaches its p99 gate per `docs/perf/test-plan.md` §3.1.

## Invocation

```powershell
# Default — runs all four scenarios at 1x against the localhost dev
# portal (appsettings.json: localhost:5400).
.\tools\perf\run-phase-v.ps1

# Phase V — pilot site, explicit target.
.\tools\perf\run-phase-v.ps1 -TargetUri https://pilot.example.com -Site kotoka -Profile 1x

# Tema-shaped projection (5x).
.\tools\perf\run-phase-v.ps1 -TargetUri https://staging.example.com -Site staging -Profile 5x

# Single-scenario spot-check (e.g. re-running just the failing one).
.\tools\perf\run-phase-v.ps1 -TargetUri http://localhost:5400 -Filter health

# Plan-only (no commands executed; prints scenario list + thresholds).
.\tools\perf\run-phase-v.ps1 -TargetUri https://pilot.example.com -Site kotoka -DryRun
```

### Parameters

| Parameter | Default | Notes |
|---|---|---|
| `-TargetUri` | _(empty — uses `appsettings.json` localhost target)_ | Sets `NICKERP_PERF_TargetBaseUrl` for each scenario invocation. |
| `-Profile` | `1x` | One of `1x` / `5x` / `10x` per `docs/perf/test-plan.md` §1.3. |
| `-Site` | _(derived from target host)_ | Used in the report filename: `perf-{site}-{date}.md`. |
| `-Filter` | `all` | One of `all` / `health` / `case-create` / `edge-replay` / `edge-replay-backlog`. |
| `-Configuration` | `Release` | `Debug` / `Release`. The wrapper resolves the bin-reports path off this. |
| `-Tfm` | `net10.0` | Target framework moniker (matches the NickPerf project). |
| `-DryRun` | _(off)_ | Print the plan; execute nothing. |

## Output

Per-run artifact lands at:

```
tools/perf/reports/perf-{site}-{yyyy-MM-dd-HHmmss}.md
```

The `reports/` directory is `.gitkeep`-tracked; individual
`perf-*.md` artifacts are gitignored (per-run engineering output, not
source-tracked — same posture as Phase A's `dryrun-*.md` reports).

The report shape mirrors `docs/perf/baseline-2026-05-06.md`:

- **Run summary** — start / finish UTC, target URL, profile, exit code.
- **Scenario summary** — one row per scenario with verdict, p50/p95/p99,
  error rate, throughput, acceptance gate.
- **Detailed results** — per-scenario block with the same NickPerf
  stats line the baseline doc uses (Total/OK/Fail/RPS/elapsed +
  latency min/mean/max + percentiles).
- **Acceptance verdict** — single PASS / DO-NOT-SHIP statement
  referencing `docs/perf/test-plan.md` §7.
- **How to reproduce** — copy-pasteable commands (full re-run + per-scenario).

## Acceptance gates

Per `docs/perf/test-plan.md` §3.1 (the wrapper does not edit these —
each scenario's `CheckAcceptanceGate` is the source of truth):

| Scenario | p99 acceptance | p99 BLOCK threshold (1x) | Notes |
|---|---|---|---|
| `health` | n/a (informative) | warn @ 100 ms | `/healthz/live` should be sub-100 ms; the dev box's `MockJwtBearerHandler` warm-up adds a few-second tail on the first scenario, which is why we run health first. |
| `case-create` | 1000 ms | **2000 ms = BLOCK** | `CaseCreateScenario.Pilot1xP99BlockMs`. ×1.5 at 5x. |
| `edge-replay` | 500 ms | **1500 ms = BLOCK** | `EdgeReplayScenario.Pilot1xP99BlockMs`. ×1.5 at 5x. |
| `edge-replay-backlog` | informative — verifies SEC-EDGE-7 rate-limit | n/a | Expected shape: ~50% of the 28.8 RPS scheduled batches rejected with HTTP 429 within 60s. |

## Skip-on-misconfigured

Per `docs/perf/test-plan.md` §12.4, each live scenario inspects its
required configuration (target URL, bearer source, edge HMAC key) and
exits 0 with a `<scenario>: skipping — <reason>` log line if anything
required is missing. The wrapper detects the skip line and records
`SKIP` in the artifact. CI green-on-noop is the design — Phase V runs
against the pilot host wire all the prerequisites; dev-machine
ad-hoc smoke runs don't, and that's fine.

Required config to actually exercise each scenario:

| Scenario | Required env | Notes |
|---|---|---|
| `health` | _(none — always runs)_ | Hits `/healthz/live` unauthenticated. |
| `case-create` | `NICKERP_PERF_BEARER_TOKEN` (real path) OR _(default — mock JWT signer)_ | `MockJwtBearerHandler` produces signed-but-CF-Access-shaped JWTs against a per-run RSA-2048 key pair. JWKS-mock on the API host is a Phase V kickoff prerequisite (per `docs/perf/baseline-2026-05-06.md`). |
| `edge-replay` | `NICKERP_PERF_EDGE_HMAC_KEY` | Per-edge HMAC key issued by the admin flow. |
| `edge-replay-backlog` | same as `edge-replay` | Same prerequisites. |

## Exit codes

| Exit | Meaning | Operator action |
|---|---|---|
| 0 | All non-informative scenarios within p99 acceptance. | Sign off; attach the report to the runbook 14 acceptance checklist. |
| 1 | Pre-flight failure (dotnet missing, target unreachable). | Fix the target host + re-run. |
| 2 | At least one scenario breached its p99 acceptance gate. | Investigate the listed scenario; do NOT sign off. |
| 3 | At least one scenario exited fatal (uncaught exception). | Investigate the listed scenario; check the bin-reports `report.md` for traceback. |
| 4 | Runner invocation failed (couldn't find emitted `report.md`). | Check `dotnet run` worked; rebuild if the bin path is stale. |

The wrapper is CI-ready: a non-zero exit fails the deploy gate, the
report still lands so the operator has the artifact regardless.

## Files

- `run-phase-v.ps1` — the wrapper.
- `README.md` — this file.
- `reports/.gitkeep` — keeps the artifact directory present after a
  fresh checkout.
- `reports/perf-*.md` — gitignored per-run artifacts.

## Related

- `tests/NickERP.Perf.Tests/` — the homegrown NickPerf runner this
  wrapper consumes.
- `docs/perf/test-plan.md` — Phase V plan-of-record.
- `docs/perf/baseline-2026-05-06.md` — Sprint 55 / 58 first baseline
  (output-shape reference).
- `tools/cutover-dryrun/run.ps1` — Phase A migration runner; sibling
  Phase V tooling.
- `tools/security-scan/run-audit.ps1` — Phase B1 audit runner; sibling
  Phase V tooling.

## Out of scope

- Authoring new perf scenarios (out of scope per PLAN.md §23.2 B2).
- Tuning SLO thresholds (read from each scenario's `CheckAcceptanceGate`).
- Replacing the NickPerf runner — the wrapper consumes it as-is.
- Cross-platform (Windows + PowerShell only — matches Phase A posture).
