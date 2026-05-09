# `tools/cutover-dryrun/` — engineering rehearsal of runbook 14 §4-§10

Sprint 60 Phase A (`PLAN.md §23.2` work item A) — converts the operator-prose
migration block in [`docs/runbooks/14-pilot-site-standup.md`](../../docs/runbooks/14-pilot-site-standup.md)
§4-§10 plus the `live-deploy-staged-migrations` operator action into a
scripted, idempotent rehearsal an engineer (or the operator on day 1)
can run against either a Docker scratch Postgres or a real staging host.

## What `run.ps1` does

1. Pre-flight: `dotnet`, `dotnet ef`, `psql.exe` (and `docker` in container mode).
2. Provisions a clean PostgreSQL 17 target.
   - **Container mode** (default): starts `postgres:17` on port `55432`,
     waits for `pg_isready`, leaves it running for inspection.
   - **Real-host mode** (`-TargetUri postgres://user:pw@host:port/`):
     uses an existing PG17 server; does not start or stop any process.
3. Creates the three canonical databases (`nickerp_platform`,
   `nickerp_inspection`, `nickerp_nickfinance`) with `OWNER postgres`,
   plus the `nscim_app` role (`LOGIN NOSUPERUSER NOBYPASSRLS`).
4. Generates idempotent SQL via
   `dotnet ef migrations script --idempotent --context <T>` for each of
   the six DbContexts (Identity → Tenancy → Audit → Queueing →
   Inspection → NickFinance) and applies each via `psql -f`. This is the
   FU-5 / [`docs/MIGRATIONS.md`](../../docs/MIGRATIONS.md) §worked-example
   pattern that sidesteps the `dotnet ef database update` Windows
   env-var quirk.
5. Optionally runs `dotnet test --filter Category=PilotAcceptance` against
   the provisioned target.
6. Emits a markdown report at
   `tools/cutover-dryrun/reports/dryrun-{date}-migration-report.md`
   capturing target version, per-context applied/expected counts,
   `__EFMigrationsHistory` final state, test summary, and reproduction
   steps for any failure.

The script is **idempotent**: re-running against an already-migrated
target produces a report with `Newly applied = 0` for every context.

## Prerequisites

- Windows + PowerShell 7+.
- `dotnet` (10.0+) and `dotnet ef` global tool (`dotnet tool install -g dotnet-ef`).
- `psql.exe` (16+ — psql is forwards-compatible with PG17 servers).
  Auto-discovered under `C:\Program Files\PostgreSQL\<ver>\bin\` or PATH.
- Container mode: Docker Desktop or Docker Engine.
- Real-host mode: an admin (typically `postgres` superuser) connection
  string for an empty PG17 instance.

## Usage

```powershell
# Dev default — Docker scratch PG17 on port 55432, kept running after.
.\tools\cutover-dryrun\run.ps1

# Same, then drop the container.
.\tools\cutover-dryrun\run.ps1 -Cleanup

# Skip the dotnet test phase (migration-only run; ~30s on a clean target).
.\tools\cutover-dryrun\run.ps1 -SkipTests

# Real host — operator's pilot staging cluster.
.\tools\cutover-dryrun\run.ps1 -TargetUri "postgres://postgres:secret@10.0.0.5:5432/"

# Plan output only, no work.
.\tools\cutover-dryrun\run.ps1 -DryRun
```

### Parameters

| Param | Default | Notes |
|---|---|---|
| `-TargetUri` | (empty → container mode) | `postgres://user:pw@host:port/` against an existing PG17. |
| `-Port` | `55432` | Container mode only. Caller picks a non-conflicting port. |
| `-ContainerName` | `nickerp-cutover-dryrun-pg17` | Container mode only. |
| `-Image` | `postgres:17` | Container mode only. Runbook 11 locks PG17. |
| `-NscimPassword` | _(generated)_ | App-role password for `nscim_app`. |
| `-AdminPassword` | _(generated)_ | Admin / `postgres` superuser password (container mode); read from `-TargetUri` in real-host mode. |
| `-Cleanup` | `false` | Container mode: drop the container at the end. |
| `-SkipTests` | `false` | Skip the `dotnet test --filter Category=PilotAcceptance` phase. |
| `-DryRun` | `false` | Print plan + exit. |

## Output

- Per-run report at `tools/cutover-dryrun/reports/dryrun-{yyyy-MM-dd-HHmmss}-migration-report.md`.
- Per-context idempotent SQL + dotnet test log under `$env:TEMP`.

Reports are **gitignored** (`.gitignore` rule `tools/cutover-dryrun/reports/dryrun-*.md`)
— treat each run's report as ephemeral. The `tools/cutover-dryrun/reports/.gitkeep`
file ensures the directory exists in a fresh checkout.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Clean run; report written |
| 1 | Pre-flight failure (missing `dotnet` / `psql` / `docker`) |
| 2 | Postgres provision failure |
| 3 | Migration apply failure (report names the failing migration + reproduction step) |
| 4 | `dotnet test --filter PilotAcceptance` failure |
| 5 | Repo / project misconfiguration (e.g. expected DbContext path absent) |

## Relationship to other Sprint 60 phases

- **Phase B1** (`tools/security-scan/run-audit.ps1`, separate sprint
  branch) — runs the audit checklist against a clean target. B1 needs A
  to have applied the migrations so RLS-policy presence checks have
  something to find.
- **Phase B2** (`tools/perf/run-phase-v.ps1`, separate sprint branch) —
  runs NickPerf scenarios; needs A so seed data / perf-seed has tables
  to populate.
- **Phase C** (rehearsal smoke) — runs A → B1 → B2 against a single
  scratch host and amends runbook 14 with the runner invocations.

This script is **engineering tooling only.** No application code change,
no schema change, no RLS-policy authorship — see `PLAN.md §23.2` "out of
scope" for the explicit boundary.
