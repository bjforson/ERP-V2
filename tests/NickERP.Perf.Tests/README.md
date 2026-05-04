# NickERP.Perf.Tests

Phase V perf-test harness for NickERP v2. Built on **NBomber** (.NET-native load tester, MIT-licensed) per the decision in `docs/perf/test-plan.md` §6.

## What this is NOT

- A unit-test project. `dotnet test NickERP.Tests.slnx` discovers ZERO tests here.
- A pre-deployment gate. Perf testing runs as part of Phase V (post-pilot-site-lock), not on every CI build.

## What this IS

A NBomber harness with three scenarios:

| Scenario | Status | Endpoint | Phase V acceptance gate (p99 @ 1x) |
|---|---|---|---|
| `health` | live | `GET /healthz` | warn at 100 ms |
| `case-create` | STUB | `POST /api/inspection/cases` | 1000 ms (BLOCK at 2000 ms) |
| `edge-replay` | STUB | `POST /api/edge/replay` | 500 ms (BLOCK at 1500 ms) |

Stubs become live during Phase V execution when test fixtures (test tenant, test analyst JWT, test edge identity, buffer payloads) are provisioned.

## Run locally

```powershell
# build first (one time)
dotnet build tests/NickERP.Perf.Tests/NickERP.Perf.Tests.csproj -c Release

# smoke against running portal at localhost:5400
dotnet run --project tests/NickERP.Perf.Tests -c Release -- health

# smoke at higher rate
dotnet run --project tests/NickERP.Perf.Tests -c Release -- health --profile 5x

# stubs (will print Phase V prerequisites instead of running)
dotnet run --project tests/NickERP.Perf.Tests -c Release -- case-create
dotnet run --project tests/NickERP.Perf.Tests -c Release -- edge-replay
```

## Reports

Each run writes to `tests/NickERP.Perf.Tests/bin/<config>/<tfm>/reports/{date}/{scenario}/` per `Program.GetReportFolder()`:

- `report.html` — interactive NBomber HTML
- `report.md` — markdown summary
- `report.txt` — plain text

The Phase V auditor copies the relevant runs into `docs/perf/runs/{date}-{site}/` for the audit trail.

## Configuration

`appsettings.json` holds default targets. Override via:

- Environment variables prefixed `NICKERP_PERF_` (e.g. `NICKERP_PERF_TargetBaseUrl=https://staging.example.com`)
- Per-run command-line `--profile <1x|5x|10x>`

## Adding a new scenario

1. Add a `ScenariosFooScenario.cs` file in `Scenarios/` returning `ScenarioProps`.
2. Add a dispatch case in `Program.Main`.
3. Document the endpoint + acceptance gate in `docs/perf/test-plan.md` §2 + §3.
4. Add the scenario to the table in this README.

## What's deferred

Per `docs/perf/test-plan.md` §8 — chaos engineering, cross-region DR, multi-tenant noisy-neighbor, ML inference perf, long-haul (multi-day) sustained load are all post-pilot.

## License

Same as the parent NickERP repo. NBomber is MIT-licensed (Pragmatic Flow / Anton Moldovan).
