# NickERP.Perf.Tests

Phase V perf-test harness for NickERP v2. **Sprint 58** rebuilt the harness on the **homegrown NickPerf in-tree runner** (`Runner/`); Sprint 55 ran on NBomber 6.1.0, which Sprint 57's license audit revealed ships under a paid commercial subscription (SEC-DEP-3 P0). The scenarios + payload generators + JWT mock are unchanged; the rate-based scheduler + p50/p95/p99 stats + markdown reports are now first-party code.

## What this is NOT

- A unit-test project. `dotnet test NickERP.Tests.slnx` discovers ZERO tests here (`IsTestProject=false`).
- A pre-deployment gate. Perf testing runs as part of Phase V (post-pilot-site-lock), not on every CI build.

## What this IS

A NickPerf harness with five scenarios:

| Scenario | Status | Endpoint | Phase V acceptance gate (p99 @ 1x) |
|---|---|---|---|
| `health` | live | `GET /healthz/live` | warn at 100 ms |
| `case-create` | live (Sprint 55) | `POST /api/inspection/cases` | 1000 ms (BLOCK at 2000 ms) |
| `edge-replay` | live (Sprint 55) | `POST /api/edge/replay` | 500 ms (BLOCK at 1500 ms) |
| `edge-replay-backlog` | live (Sprint 55) | `POST /api/edge/replay` | informative — verifies SEC-EDGE-7 rate-limit |
| `selftest` | live (Sprint 55 / 58) | n/a — unit tests for the helpers + runner | n/a |

## Run locally

```powershell
# build first (one time)
dotnet build tests/NickERP.Perf.Tests/NickERP.Perf.Tests.csproj -c Release

# smoke against running portal at localhost:5400
dotnet run --project tests/NickERP.Perf.Tests -c Release -- health

# smoke at higher rate
dotnet run --project tests/NickERP.Perf.Tests -c Release -- health --profile 5x

# unit tests for the scenario helpers + NickPerf runner internals
dotnet run --project tests/NickERP.Perf.Tests -c Release -- selftest
```

## Run live scenarios

`case-create` requires either a real CF Access JWT (env var) or the Mock-JWT seam wired (default appsettings):

```powershell
# Real CF Access spot-check path:
$env:NICKERP_PERF_BEARER_TOKEN = "<token-from-out-of-band-CF-Access-login>"
dotnet run --project tests/NickERP.Perf.Tests -c Release -- case-create

# Mock-JWT rep-volume path (preferred for hot-path RPS measurement):
Remove-Item Env:\NICKERP_PERF_BEARER_TOKEN -ErrorAction SilentlyContinue
dotnet run --project tests/NickERP.Perf.Tests -c Release -- case-create --profile 5x
```

`edge-replay` + `edge-replay-backlog` need the per-edge HMAC API key:

```powershell
$env:NICKERP_PERF_EDGE_HMAC_KEY = "<per-edge-key-from-admin-flow>"
dotnet run --project tests/NickERP.Perf.Tests -c Release -- edge-replay
dotnet run --project tests/NickERP.Perf.Tests -c Release -- edge-replay-backlog
```

## Run against staging / pilot

Override the target via env var (`NICKERP_PERF_` prefix is stripped by `IConfiguration`; double-underscore separates nested keys):

```powershell
$env:NICKERP_PERF_TargetBaseUrl = "https://staging.example.com"
$env:NICKERP_PERF_Endpoints__InspectionWebBaseUrl = "https://api.staging.example.com"
dotnet run --project tests/NickERP.Perf.Tests -c Release -- case-create --profile 1x
```

Per-scenario overrides:

```powershell
$env:NICKERP_PERF_CaseCreate__TargetBaseUrl = "https://api.staging.example.com"
$env:NICKERP_PERF_EdgeReplay__TargetBaseUrl = "https://api.staging.example.com"
$env:NICKERP_PERF_EdgeReplay__EdgeNodeId = "edge-takoradi-01"
$env:NICKERP_PERF_EdgeReplay__TenantId = "42"
```

## Skip-on-misconfigured behaviour

Each live scenario inspects required config + auth and gracefully exits 0 if missing. CI green-on-noop is the design — no broken pipelines from environments that don't have the perf rig wired:

| Scenario | Skip trigger | Exit code | Log line |
| --- | --- | --- | --- |
| `case-create` | no target URL OR no bearer source | 0 | `case-create: skipping — <reason>` |
| `edge-replay` | no target URL OR `NICKERP_PERF_EDGE_HMAC_KEY` unset | 0 | `edge-replay: skipping — <reason>` |
| `edge-replay-backlog` | same as `edge-replay` | 0 | `edge-replay-backlog: skipping — <reason>` |

## Acceptance gates

Each scenario's `CheckAcceptanceGate` method asserts p99 latency against the §3.1 thresholds. If a scenario completes with `0 OK` requests, the dispatcher fails (the gate cannot evaluate). The exit code maps:

| Exit code | Meaning |
| --- | --- |
| 0 | Scenario passed (or skipped on missing config). |
| 1 | Acceptance gate breached, OR all requests failed, OR unknown scenario name. |
| 2 | FATAL — uncaught exception. |

## Reports

Each run writes a single markdown report to `tests/NickERP.Perf.Tests/bin/<config>/<tfm>/reports/{date}/{scenario}/report.md`. Sprint 55's NBomber HTML / TXT bundle is gone (markdown is enough for the audit trail; downstream tooling reads markdown verbatim).

The Phase V auditor copies the relevant runs into `docs/perf/runs/{date}-{site}/` for the audit trail.

The Sprint 55 baseline (first run against the dev portal) is at `docs/perf/baseline-2026-05-06.md`.

## Configuration

`appsettings.json` holds default targets. Override via environment variables prefixed `NICKERP_PERF_`. Per-scenario keys live under their own section: `CaseCreate.*`, `EdgeReplay.*`, `Auth.MockJwt.*`. Per-run command-line `--profile <1x|5x|10x>`.

## Adding a new scenario

1. Add a `FooScenario.cs` file in `Scenarios/` returning `NickPerfScenario?` (null = skip).
2. Implement `ShouldSkip` + `CheckAcceptanceGate` per-scenario.
3. Add a dispatch case in `Program.Main` + an exit-code handler.
4. Document the endpoint + acceptance gate in `docs/perf/test-plan.md` §2 + §3.
5. Add the scenario to the table at the top of this README.
6. (Optional) add unit tests in `Scenarios/Helpers/HelperUnitTests.cs` for any new helpers.

## NickPerf runner internals (Sprint 58)

`Runner/` contains the homegrown rate-based load runner. Files:

- `Runner/NickPerfScenario.cs` — typed scenario shape (`Name`, `RunStep`, `LoadProfile`, `MaxConcurrent`).
- `Runner/NickPerfRunner.cs` — `PeriodicTimer`-driven scheduler with a `SemaphoreSlim` concurrency cap; per-step latency captured via `Stopwatch`.
- `Runner/NickPerfStats.cs` — latency buffer + nearest-rank percentile math (`Array.Sort` + index lookup).
- `Runner/NickPerfReport.cs` — markdown report writer.
- `Runner/Http/NickPerfHttp.cs` — direct `HttpClient` helpers (`GetAsync`, `PostJsonAsync`, `SendAsync`).

The runner is intentionally small (~400 LoC). The selftest under `dotnet run -- selftest` covers the percentile math, rate scheduling, exception → fail capture, and report file writes.

## What's deferred

Per `docs/perf/test-plan.md` §8 — chaos engineering, cross-region DR, multi-tenant noisy-neighbor, ML inference perf, long-haul (multi-day) sustained load are all post-pilot.

## License

Same as the parent NickERP repo. NickPerf runner is first-party code; no third-party perf-test runtime dependency. (Sprint 55's NBomber 6.1.0 / NBomber.Http 6.0.0 were removed in Sprint 58 — they shipped under a commercial subscription; see `tools/security-scan/license-allowlist-rationale.md` §2 for the audit trail.)
