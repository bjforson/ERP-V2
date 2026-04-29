# Runbook 01 — Deploying a new build to live

> **Scope.** Shipping a green build of `plan/main` from CI to the live
> ERP V2 host. The supervised target is the same Windows host as v1
> NSCIM, with NSSM-managed services on the `:5410` (Inspection Web) and
> `:5400` (Portal) ports — see
> [`docs/product-calls-2026-04-29.md` §3.1](../product-calls-2026-04-29.md#31-fu-deploy--deployps1-for-erp-v2)
> for the binding spec, and [`Deploy.ps1`](../../Deploy.ps1) at the
> repo root for the deploy automation.
>
> Future modules (`NickERP_NickFinance_*` on the `:5420` range,
> `NickERP_NickHR_*` on the `:5430` range) follow the same pattern; the
> service catalogue in `Deploy.ps1` is the place to add them.
>
> **Sister docs:** [`02-secret-rotation.md`](02-secret-rotation.md) for
> credential rotations,
> [`docs/MIGRATIONS.md`](../MIGRATIONS.md) for the EF migration env-var
> quirk that bites every deploy with schema changes.

---

## 1. Symptom

You're deploying. There is no symptom — this runbook is the **planned
change** path. Use it when:

- CI on `plan/main` (or a hotfix branch) is green and the change is
  ready for prod.
- A schema migration is bundled in the build and the host needs to
  apply it on startup.
- An incident response calls for "redeploy with the previous build" —
  follow the same pattern, just with an older artifact.

If you're chasing an alert, this is probably the wrong runbook. Start
at [`README.md`](README.md)'s decision tree.

## 2. Severity

| Mode | Severity | Response window |
|---|---|---|
| Routine deploy (planned) | n/a — operator-initiated | as scheduled |
| Hotfix deploy (incident-response, narrow patch) | P2 | inside 30 min |
| Rollback (revert to previous artifact) | P1 | inside 10 min |

A rollback that requires a **down-migration** is no faster than a
forward-fix and should be triaged as a P1 incident in its own right —
EF Core's `Down` methods are best-effort and most of v2's migrations
have intentionally-empty downs (see
[`docs/MIGRATIONS.md`](../MIGRATIONS.md)).

## 3. Quick triage (60 seconds)

Before you start, answer:

- **Did anything else change?** Postgres password, plugin DLLs, env
  vars, `appsettings.{env}.json`. If yes, deploy the artifact change
  *first* against unchanged config, observe one healthy `/healthz/ready`,
  *then* roll the config in a separate step. Two changes at once is
  the single biggest cause of "the deploy broke X" because both could
  be the cause and you have to bisect.
- **Is the current host healthy?** `curl http://127.0.0.1:5410/healthz/ready`
  before touching anything. If it's already 503 you're walking into an
  incident, not a deploy — pick the right runbook.
- **Is there a schema migration?** `git log --oneline <last-deployed>..HEAD --
  '*Database/Migrations/*' '*platform/*Database/Migrations/*'` will tell
  you. If yes, plan for the EF child-process env-var quirk
  ([`MIGRATIONS.md`](../MIGRATIONS.md)) — script-and-pipe is the
  recommended path.

## 4. Diagnostic commands

All commands assume bash (Git Bash or WSL on Windows). PowerShell
equivalents are noted where they differ.

### 4.1 Confirm current host state

```bash
curl -s http://127.0.0.1:5410/healthz/live  | jq .
curl -s http://127.0.0.1:5410/healthz/ready | jq .
```

A healthy `/healthz/ready` returns `{"status":"Healthy", ...}` with
five entries: `postgres-platform-identity`, `postgres-platform-audit`,
`postgres-inspection`, `plugin-registry`, `imaging-storage`. Anything
`Unhealthy` here means the live host has a pre-existing problem; do
not paper over it with a fresh deploy.

### 4.2 Confirm the artifact you intend to ship

The build artifact is the publish output of
`modules/inspection/src/NickERP.Inspection.Web/NickERP.Inspection.Web.csproj`.

```bash
cd "C:/Shared/erp-v2-p1"
git log -1 --format="%H %s" plan/main
dotnet build NickERP.Tests.slnx -c Release --nologo
dotnet test  NickERP.Tests.slnx -c Release --no-build --nologo
```

Expected: 0 errors, all tests pass (≥51 as of Sprint 7). Do **not**
deploy on a build with skipped or failing tests.

### 4.3 Inspect the migrations the build will apply

```bash
psql -U postgres -d nickerp_inspection \
  -c 'SELECT "MigrationId" FROM inspection."__EFMigrationsHistory"
       ORDER BY "MigrationId" DESC LIMIT 5;'
psql -U postgres -d nickerp_platform \
  -c 'SELECT "MigrationId" FROM audit."__EFMigrationsHistory"
       ORDER BY "MigrationId" DESC LIMIT 5;'
psql -U postgres -d nickerp_platform \
  -c 'SELECT "MigrationId" FROM identity."__EFMigrationsHistory"
       ORDER BY "MigrationId" DESC LIMIT 5;'
psql -U postgres -d nickerp_platform \
  -c 'SELECT "MigrationId" FROM tenancy."__EFMigrationsHistory"
       ORDER BY "MigrationId" DESC LIMIT 5;'
```

Compare against the migration files in the source tree:

```bash
git ls-tree -r --name-only HEAD \
  modules/inspection/src/NickERP.Inspection.Database/Migrations \
  platform/NickERP.Platform.Audit.Database/Migrations \
  platform/NickERP.Platform.Identity.Database/Migrations \
  platform/NickERP.Platform.Tenancy.Database/Migrations \
  | grep -v Designer | grep '\.cs$' | sort
```

Anything in the source tree that is **not** in the per-context
`__EFMigrationsHistory` will be applied on the next `Database.Migrate()`
call.

## 5. Resolution — the deploy

> **Deploy target (defined Sprint 9 / FU-deploy).** ERP V2 ships via
> [`Deploy.ps1`](../../Deploy.ps1) at the repo root, modelled on v1's
> `Deploy.ps1`. Default mode does Inspection Web (`:5410`) + Portal
> (`:5400`); flags narrow that down (see §5.0 below). The script
> assumes the prod box is the same Windows host the worktree builds
> on — `dotnet publish` writes directly into
> `C:\Shared\ERP V2\publish\<service>\`, which is the canonical path
> each NSSM service's `AppDirectory` points at. Cross-host robocopy
> (separate dev box) is documented as a future variant in
> `Deploy.ps1`'s header docstring; it is not wired in v0.
>
> Spec is binding in
> [`docs/product-calls-2026-04-29.md` §3.1](../product-calls-2026-04-29.md#31-fu-deploy--deployps1-for-erp-v2).

The following sections cover both the supervised path (§5.0
`Deploy.ps1`) and the operator-driven `dotnet publish` + manual host
start fallback (§5.1–§5.6) for cases where you need to deploy without
NSSM (e.g. a fresh checkout on a brand-new dev box).

### 5.0 Supervised deploy (recommended)

```powershell
cd "C:\Shared\erp-v2-fu-deploy"   # or whichever worktree is the deploy source
.\Deploy.ps1 -DryRun              # print plan, do nothing
.\Deploy.ps1                      # default: Inspection Web + Portal
.\Deploy.ps1 -ApiOnly             # Inspection Web only
.\Deploy.ps1 -WebAppOnly          # Portal only
.\Deploy.ps1 -SkipBuild           # skip dotnet publish; just restart + probe
```

The script runs six phases: **stop → publish → verify binaries →
start → /healthz/ready probe → per-service summary**. It fails fast
on the first phase that errors and surfaces the failing service
name. NSSM service install commands (one-time, ops-only) live in the
header docstring of `Deploy.ps1`.

`NICKSCAN_DB_PASSWORD` must be set on each NSSM service's
`AppEnvironmentExtra` (the `nscim_app` Postgres password); the
script does not write secrets. When FU-userid lands, the
`app.user_id` plumbing will appear in the same env-var stanza —
tracked as a TODO comment in `Deploy.ps1`.

### 5.1 Pre-flight

1. Confirm `plan/main` is green in CI.
2. Pull and verify locally:
   ```bash
   cd "C:/Shared/erp-v2-p1"
   git fetch origin && git checkout plan/main && git pull --ff-only
   git log -1 --format="%H %s"
   dotnet build NickERP.Tests.slnx -c Release --nologo
   dotnet test  NickERP.Tests.slnx -c Release --no-build --nologo
   ```
3. Snapshot the migration history (see §4.3) — keep the output for
   the postmortem template in §7 if anything goes wrong.

### 5.2 Apply migrations (if any)

If §4.3 showed migrations to apply, prefer the **script-and-pipe**
flow per [`docs/MIGRATIONS.md`](../MIGRATIONS.md). Skip this section
on a no-schema-change deploy.

```bash
cd "C:/Shared/erp-v2-p1/platform/NickERP.Platform.Audit.Database"
LAST_AUDIT=$(psql -U postgres -d nickerp_platform -t -A \
  -c 'SELECT "MigrationId" FROM audit."__EFMigrationsHistory"
       ORDER BY "MigrationId" DESC LIMIT 1;')
dotnet ef migrations script "$LAST_AUDIT" --idempotent \
  --output /tmp/migrate-audit.sql --context AuditDbContext

PGPASSWORD="$NICKSCAN_DB_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
  -U nscim_app -d nickerp_platform -f /tmp/migrate-audit.sql
```

Repeat for `IdentityDbContext`, `TenancyDbContext`, `InspectionDbContext`
as appropriate. Apply in dependency order: **identity → tenancy →
audit → inspection** (inspection FKs nothing across DBs, but tenancy
seeds tenant rows that the inspection RLS context expects).

> **Why `nscim_app`, not `postgres`.** `nscim_app` is the production
> posture — `LOGIN NOSUPERUSER NOBYPASSRLS`. Running migrations as
> `nscim_app` proves the role's grants are still sufficient. If a
> migration needs `postgres`, you have a regression to fix before
> shipping (see "Restore minimal-privilege state" below).

### 5.3 Publish

```bash
cd "C:/Shared/erp-v2-p1"
dotnet publish modules/inspection/src/NickERP.Inspection.Web/NickERP.Inspection.Web.csproj \
  -c Release \
  -o ./publish/inspection-web \
  --nologo
```

Verify the publish artifact looks sane:

```bash
ls -la ./publish/inspection-web/NickERP.Inspection.Web.{dll,exe} 2>&1
ls -la ./publish/inspection-web/plugins/*.dll 2>&1
```

The `plugins/` subfolder must exist with at least the bundled
adapters (`NickERP.Inspection.ExternalSystems.IcumsGh.dll`,
`NickERP.Inspection.Authorities.CustomsGh.dll`,
`NickERP.Inspection.Scanners.FS6000.dll`, plus any `Mock` variants
present in source). An empty `plugins/` folder will fail
`/healthz/ready` on first boot
(see [`04-plugin-load-failure.md`](04-plugin-load-failure.md)).

### 5.4 Stop the running host

If a host is currently running on `:5410`:

```bash
# Graceful — send Ctrl+C if attached; or:
powershell.exe -NoProfile -Command \
  "Get-NetTCPConnection -LocalPort 5410 -State Listen \
   | ForEach-Object { Stop-Process -Id \$_.OwningProcess -Force }"
```

For an NSSM-supervised host (Sprint 9 / FU-deploy and later), the
canonical idiom is:

```powershell
# Use Deploy.ps1 — it stops, publishes, restarts, and probes.
.\Deploy.ps1 -ApiOnly       # Inspection Web only
# Or manually:
Stop-Service NickERP_Inspection_Web
```

### 5.5 Swap the artifact and start

The supervised path is `Deploy.ps1 -ApiOnly` (or default for
both services); see §5.0. The script publishes directly into
`C:\Shared\ERP V2\publish\<service>\` (same path each NSSM service's
`AppDirectory` points at), so the "robocopy from a separate publish
dir" step that v1 carried is collapsed into the publish step itself.

For a one-off without the script (e.g. an unsupervised dev / staging
host that runs from the worktree directly), run the entry-point
directly:

```bash
cd "C:/Shared/erp-v2-p1/modules/inspection/src/NickERP.Inspection.Web"
ASPNETCORE_ENVIRONMENT=Production \
  ConnectionStrings__Platform="Host=localhost;Port=5432;Database=nickerp_platform;Username=nscim_app;Password=$NICKSCAN_DB_PASSWORD" \
  ConnectionStrings__Inspection="Host=localhost;Port=5432;Database=nickerp_inspection;Username=nscim_app;Password=$NICKSCAN_DB_PASSWORD" \
  NickErp__Inspection__Imaging__StorageRoot="C:\Shared\ERP V2\.imaging-store" \
  dotnet run --no-build -c Release
```

The host's startup log line — once Migrations are applied — looks
like:

```
info: Startup.Migrations[0]
      Migrations applied for Identity, Audit, Tenancy, Inspection.
```

A migration failure logs:

```
crit: Startup.Migrations[0]
      Migration at startup failed; aborting host bootstrap.
```

…and the process exits non-zero. **The host will not stay up with a
half-applied migration.** This is by design (Program.cs §F5).

### 5.6 Restore minimal-privilege state

If a deploy step required elevated credentials (e.g. `postgres` for a
migration `nscim_app` couldn't run), confirm at the end:

- `nscim_app` is still `LOGIN NOSUPERUSER NOBYPASSRLS`:
  ```bash
  psql -U postgres -d postgres \
    -c "SELECT rolname, rolsuper, rolbypassrls
        FROM pg_roles WHERE rolname = 'nscim_app';"
  ```
- The running host process is connected as `nscim_app`, not `postgres`:
  ```bash
  psql -U postgres -d nickerp_inspection \
    -c "SELECT pid, usename, application_name
        FROM pg_stat_activity
        WHERE datname = 'nickerp_inspection' AND state = 'active';"
  ```
  Every row should show `usename = nscim_app`. If you see `postgres`,
  the host is running with the dev-shortcut connection string — fix
  before declaring the deploy done.

## 6. Verification

After §5.5, in this order:

1. **Liveness.** `curl -s http://127.0.0.1:5410/healthz/live` returns
   `Healthy` (HTTP 200) within 5 s of process start.

2. **Readiness.** `curl -s http://127.0.0.1:5410/healthz/ready | jq .`
   returns `Healthy` for all five checks. If any check is `Unhealthy`,
   pivot immediately:
   - `postgres-*` → check connection string + DB reachability.
   - `plugin-registry` Unhealthy with `0 plugin(s) loaded` →
     [`04-plugin-load-failure.md`](04-plugin-load-failure.md).
   - `imaging-storage` Unhealthy → permissions on
     `NickErp:Inspection:Imaging:StorageRoot`.

3. **Migration applied.** Re-run the §4.3 commands; new `MigrationId`
   rows should be present.

4. **Smoke a known case.** Hit a real endpoint that exercises the
   tenancy + auth + DB path. With CF Access in front, this requires a
   browser (or a service-token request). For dev / staging:
   ```bash
   curl -s -H "X-Dev-User: dev@nickscan.com" \
     http://127.0.0.1:5410/cases | head -c 500
   ```
   (works only if `Identity:CfAccess:DevBypass:Enabled=true`, i.e.
   non-prod). In prod, use the browser flow through CF Access.

5. **Logs.** Tail Seq (`http://localhost:5341`) filtered to the new
   process — look for any `Error` or `Critical` events in the first 30 s.
   The default file fallback is `C:\Shared\Logs\NickERP.Inspection.Web*.log`.

6. **PreRender drain.** If the build touched the image pipeline,
   confirm the worker is still draining:
   ```bash
   psql -U postgres -d nickerp_inspection -c \
     'SELECT COUNT(*) AS unrendered
      FROM inspection.scan_artifacts a
      WHERE NOT EXISTS (
        SELECT 1 FROM inspection.scan_render_artifacts r
        WHERE r."ScanArtifactId" = a."Id" AND r."Kind" = '"'"'thumbnail'"'"'
      );'
   ```
   The number should be steady or trending toward zero. If it climbs,
   see [`03-prerender-stalled.md`](03-prerender-stalled.md).

## 7. Aftermath

### 7.1 Postmortem template (only required for failed / rolled-back deploys)

```
## Deploy: <YYYY-MM-DD HH:MM> — <commit SHA>
- Outcome: success | rollback | hotfix-needed
- Pre-deploy /healthz/ready: <healthy | unhealthy:<which>>
- Migrations applied: <list of MigrationIds, or "none">
- Time from "stop old host" to "Healthy /healthz/ready": <seconds>
- What broke (if anything):
- What we'd do differently:
- Followups filed: <CHANGELOG.md / open-issue links>
```

### 7.2 Who to notify

Single-engineer system today: capture the deploy in `CHANGELOG.md`
under a new dated bullet, and update any open issue the deploy
addresses. If the deploy fixed an incident, also update that
incident's runbook with whatever you wish you'd known.

## 8. References

- [`docs/MIGRATIONS.md`](../MIGRATIONS.md) — EF env-var child-process
  quirk, `nscim_app` posture, script-and-pipe flow.
- [`docs/RUNBOOK.md`](../RUNBOOK.md) — sister runbook with one-off
  migration cleanups (FU-6).
- [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md) §7.7 — image pipeline,
  context for the readiness check shape.
- [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md) §7.6 — migration
  readiness posture.
- [`Deploy.ps1`](../../Deploy.ps1) — the ERP V2 deploy script
  (Sprint 9 / FU-deploy). Stop / publish / verify / start / probe
  pipeline, mirroring v1's shape.
- [`docs/product-calls-2026-04-29.md` §3.1](../product-calls-2026-04-29.md#31-fu-deploy--deployps1-for-erp-v2)
  — binding spec for the ERP V2 deploy target (ports, NSSM service
  names, publish dir).
- v1 reference — `C:\Shared\NSCIM_PRODUCTION\Deploy.ps1` (read-only;
  v1 is currently the live system) is the original pattern that ERP
  V2's `Deploy.ps1` derived from.
- [`02-secret-rotation.md`](02-secret-rotation.md) — for the secret
  side of any deploy that includes a credential rotation.
- [`PLAN.md`](../../PLAN.md) §18 — Sprint 7 / P1 origin of this
  runbook.
