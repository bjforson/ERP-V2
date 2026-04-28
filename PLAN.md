# PLAN.md — NickERP v2 execution plan

> **Purpose.** Operational source of truth across sprints. Each work item is self-contained: an agent reading just its card has everything needed to execute. **`ROADMAP.md` says *what* and *why*; this document says *how*, *who*, and *in what order*.**
>
> **Active sprint.** Sprint 2 — Production Hardening + Analyst Polish (§14). Sprint 1 (Foundation + Demo, §5–§13) shipped 9 of 9 scoped items at main `b101c7b`; preserved here as historical record.
>
> **Last updated:** 2026-04-28 (Sprint 2 drafted).

---

## 1. The vision (verbatim, anchor-locked)

From `ROADMAP.md` §1 — these statements drive every prioritization call below:

- **Federation by location** — 20 scanners across 5 locations; users assigned to locations, not flat.
- **Per-location setup** — scanners + external systems bind to locations; shared bindings allowed.
- **Generic nomenclature** — no `ICUMS`/`FS6000` strings inside `modules/inspection/src/Core/*` or `Database/*`. Vendor names live only in plugins.
- **ERP context** — Inspection is the first of N modules; Finance / HR / Comms come next on the same platform.
- **Online-first with edge for backup** — central Postgres; edge node replays events on reconnect.
- **Multi-tenant from day 1** — RLS enforced at the DB layer, not just app-stamped.
- **In-house plugins** — vendors are adapters, not features.

If a work item below regresses any vision invariant, stop and re-plan.

---

## 2. Why this plan exists (and replaced the old one)

The previous plan was "march down ROADMAP §4 in feature order." The 2026-04-27 audit (seven parallel scans of the codebase) found that approach was building features on top of foundations that didn't hold:

1. **Zero RLS policies in any v2 migration.** `TenantConnectionInterceptor` registered in DI but **never attached to any DbContext** — `app.tenant_id` is never SET. The "multi-tenant from day 1" claim is currently false.
2. **`IScannerAdapter.StreamAsync` is dead code.** Only manual `SimulateScanAsync` button calls it. The end-to-end demo is **one `BackgroundService` away**.
3. **Zero tests exist.** No `*.Tests*.csproj` anywhere. The FS6000 decoder is a verbatim port from v1 with no parity assertion.
4. **Plugin contract drift unenforced.** We changed `IAuthorityRulesProvider.InspectionCaseData` mid-session; nothing prevents a stale plugin DLL from `MissingMethodException`-ing at first call.
5. **Production-readiness gaps** — no health endpoints, no migrations-at-startup, no eviction on the image source store, error-UI leaks `ex.Message`.

The new plan is **harden + demo + then expand**: pay down foundation debt, ship the end-to-end demo, lock the result down with tests, then continue feature work (rules persistence → viewer → multi-location → NickFinance) on a stable base.

---

## 3. Phases

| Phase | Goal | Ships | Predecessors |
|---|---|---|---|
| **F — Foundation** | RLS, tests, plugin versioning, prod minimum | F1–F5 | — |
| **D — Demo** | Auto-ingest worker; rules auto-fire; end-to-end smoke | D1–D4 | F1, F4 (logically) |
| **V — Validation** | Instrumentation, multi-location proof, RuleEvaluation persistence, analyst viewer | V1–V4 | F + D |
| **G — Generalization** | Platform tightening + NickFinance Petty Cash pathfinder | G1–G2 | V (mostly) |
| **P — Production prep** | Runbooks, edge node, audit projection | P1–P3 | G |

**Current sprint scope is F + D only.** V/G/P are scoped here for context; they do not execute this sprint.

---

## 4. Conventions

These apply to every work item.

### Branches & worktrees

- One branch per work item. Naming: `plan/<phase><id>-<kebab-name>` — e.g. `plan/f1-rls-interceptors`, `plan/d2-scanner-ingestion-worker`.
- Parallel agents must use **git worktrees** to avoid stomping each other:
  ```bash
  git worktree add ../erp-v2-f1-rls plan/f1-rls-interceptors
  ```
- The worktree dies when the branch merges; `git worktree prune` cleans up.

### Commits

- One commit per work item where possible; squash before merge if the agent needed multiple iterations.
- Conventional commit format already used in repo: `feat(inspection): ...`, `fix(platform): ...`, `chore(docs): ...`.
- Body must include the work-item id (e.g. `Closes F1.`) and the acceptance-criteria evidence (build green, AC checks passed, etc.).
- Sign-off line: `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`.

### PR titles

`[F1] RLS + tenant interceptor wiring` — phase id in brackets, then the work-item title.

### Status field

Each work item below has a **Status** field. Values: `pending`, `in-progress`, `blocked`, `done`. Update it in this file when state changes; commit the update with the same PR.

### Standing rules (apply to every agent)

1. **v1/v2 separation rule.** `C:\Shared\NSCIM_PRODUCTION\` is **read-only**. No commits, no doc edits there. Need something? Copy as a point-in-time port into v2.
2. **No skipping git hooks** (`--no-verify`, `--no-gpg-sign`). If a hook fails, fix the issue.
3. **No weakening of security posture without explicit user confirmation.** Adding `[AllowAnonymous]`, loosening CORS/cookies, lowering hashing cost, etc. are all out of scope unless the work item requires it explicitly.
4. **Build must be green** before commit (`dotnet build` from repo root: 0 errors). New warnings require a justification in the commit body.
5. **No mocking around the work item's scope.** If a work item says "do X", do X. If you find Y also needs doing, surface it as a follow-up note in the PR body — don't expand scope silently.

---

## 5. Phase F — Foundation

### F1 — RLS + Tenant Interceptor Wiring

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none |
| **Parallel-safe with** | F2, F3 (different files); tread carefully with F4/F5 (Program.cs hotspot) |
| **Effort** | ~1 day |
| **Branch** | `plan/f1-rls-interceptors` |

**Why this matters.** Vision §6 ("multi-tenant from day 1") is currently an unenforced claim. Audit found zero `CREATE POLICY` SQL in any v2 migration; `TenantConnectionInterceptor` is registered in DI but never attached to any DbContext. Defense-in-depth doesn't exist at the DB layer. Application-layer stamping in `CaseWorkflowService` is the only thing keeping rows tenant-tagged, and there's a `SetTenant(1)` fallback that silently coerces unresolved tenants.

**Deliverable.**
1. New migrations on `nickerp_inspection`, `nickerp_platform` (Identity / Tenancy / Audit schemas) that emit:
   - `ALTER TABLE <schema>.<table> ENABLE ROW LEVEL SECURITY`
   - `ALTER TABLE <schema>.<table> FORCE ROW LEVEL SECURITY`
   - `CREATE POLICY tenant_isolation_<table> ON <schema>.<table> USING (tenant_id = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK (...)`
   - Apply to every table with a `tenant_id` column.
2. Every `AddDbContext<...>(...)` call in the host wires both interceptors:
   ```csharp
   options.AddInterceptors(
     sp.GetRequiredService<TenantConnectionInterceptor>(),
     sp.GetRequiredService<TenantOwnedEntityInterceptor>());
   ```
   (Use the `(IServiceProvider, DbContextOptionsBuilder)` overload of `AddDbContext`.)
3. Remove the `if (!_tenant.IsResolved) _tenant.SetTenant(1);` fallback in `CaseWorkflowService.CurrentActorAsync` (line ~61). Let it throw.
4. `LocationAssignments.razor:103` filters `Identity.Users` by `_tenant.TenantId` (or, better, `IdentityDbContext.OnModelCreating` adds `HasQueryFilter` on `IdentityUser`).

**Files in scope.**
- `modules/inspection/src/NickERP.Inspection.Database/Migrations/<new>_AddRlsPolicies.cs`
- `platform/NickERP.Platform.Identity.Database/Migrations/<new>_AddRlsPolicies.cs`
- `platform/NickERP.Platform.Tenancy.Database/Migrations/<new>_AddRlsPolicies.cs`
- `platform/NickERP.Platform.Audit.Database/Migrations/<new>_AddRlsPolicies.cs`
- `modules/inspection/src/NickERP.Inspection.Web/Program.cs` (DbContext registration only)
- `platform/NickERP.Platform.Identity.Database/IdentityDatabaseServiceCollectionExtensions.cs` (DbContext registration)
- `platform/NickERP.Platform.Tenancy.Database/TenancyDatabaseServiceCollectionExtensions.cs` (if it exists)
- `platform/NickERP.Platform.Audit.Database/AuditDatabaseServiceCollectionExtensions.cs` (DbContext registration)
- `modules/inspection/src/NickERP.Inspection.Web/Services/CaseWorkflowService.cs` (line ~61)
- `modules/inspection/src/NickERP.Inspection.Web/Components/Pages/LocationAssignments.razor` (line ~103)
- `platform/NickERP.Platform.Identity.Database/IdentityDbContext.cs` (`OnModelCreating`)

**Acceptance criteria.**
- [ ] `psql -d nickerp_inspection -c "SELECT count(*) FROM inspection.cases;"` (no `app.tenant_id` set) → returns **0**.
- [ ] Same query with `SET app.tenant_id = '1'` → returns only tenant-1 rows.
- [ ] `dotnet build` returns 0 errors, 0 new warnings.
- [ ] Inspection v2 starts on :5410 with the dev-bypass header → `/cases` renders 200, plugins load.
- [ ] All five existing plugins still load on startup (verify via `/plugins` endpoint shows 5).
- [ ] A code path that doesn't run the tenancy middleware throws `InvalidOperationException` from `CaseWorkflowService` instead of silently writing to tenant 1.

**Verification.**
```bash
PSQL="/c/Program Files/PostgreSQL/18/bin/psql.exe"
PGPASSWORD="$NICKSCAN_DB_PASSWORD" "$PSQL" -h localhost -U postgres -d nickerp_inspection -c "
  SELECT schemaname, tablename, policyname FROM pg_policies WHERE schemaname = 'inspection';"
# Expect: one tenant_isolation_<table> per tenanted table
PGPASSWORD="$NICKSCAN_DB_PASSWORD" "$PSQL" -h localhost -U postgres -d nickerp_inspection -c "
  SELECT count(*) FROM inspection.cases;"
# Expect: 0 (no app.tenant_id)
PGPASSWORD="$NICKSCAN_DB_PASSWORD" "$PSQL" -h localhost -U postgres -d nickerp_inspection -c "
  SET app.tenant_id = '1'; SELECT count(*) FROM inspection.cases;"
# Expect: actual tenant-1 row count
```

**Out of scope.** Health checks (F5), `RunMigrationsOnStartup` flag (F5), tenant-aware plugin configs (F4).

---

### F2 — Test Foundation

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none |
| **Parallel-safe with** | F1, F3, F4, F5 (touches only new `tests/` tree) |
| **Effort** | ~2 days |
| **Branch** | `plan/f2-test-foundation` |

**Why this matters.** Zero test projects exist. The FS6000 decoder is a verbatim port from v1 with no parity test — a future tweak silently desyncs from production. The Razor `[SupplyParameterFromForm]` bug fixed earlier this session would have been caught pre-emptively by a 50ms WebApplicationFactory test.

**Deliverable.** A `tests/` peer to `apps/`, `modules/`, `platform/`, with:
- xUnit v2 + `Testcontainers.PostgreSQL` + `bunit` + `Microsoft.AspNetCore.Mvc.Testing` + `FluentAssertions`.
- Layout: `tests/NickERP.Inspection.<Component>.Tests/` per testable component.
- A `Directory.Build.props` for shared test config.
- A `PostgresFixture : IAsyncLifetime` (one container per test assembly via `[CollectionDefinition]`).
- `tests/TestData/` with binary fixtures (FS6000 sample triplet, ICUMS sample batch JSON).
- **Five first-wave tests**, each a real assertion catching a real regression:
  1. **FS6000 byte-pattern parity** — decode a fixture triplet; assert SHA-256 of `decoded.High[]`/`Low[]`/`Material[]` matches recorded constants.
  2. **ICUMS schema-drift snapshot** — load fixture batch JSON; assert `(ContainerNumber → DeclarationNumber, HouseBl, RegimeCode)` tuples match expected.
  3. **gh-customs port-match** — scan at Tema + BOE `WTTKD2MPS3` → exactly one `GH-PORT-MATCH` Error.
  4. **gh-customs CMR→IM upgrade** — case with one CMR + one BOE for same container → exactly one `promote_cmr_to_im` mutation.
  5. **Razor SSR form-binding regression** — `bunit` test of `NewCase.razor` and `LocationAssignments.razor` rendering without `[SupplyParameterFromForm]` NRE.

**Files in scope.** Everything new under `tests/`. **No production-code changes.**

**Acceptance criteria.**
- [ ] `dotnet test` from repo root passes all 5 tests.
- [ ] Total test runtime < 60 seconds on cold cache, < 10 seconds on warm.
- [ ] CI runnable: `dotnet test --filter Category!=Integration` runs the unit tests in < 5s without Postgres.
- [ ] Each test has a one-line comment naming the regression it prevents.

**Out of scope.** End-to-end demo test (D4 owns that). Coverage of every component (this is wave 1).

---

### F3 — Plugin Contract Versioning

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none |
| **Parallel-safe with** | F1, F2, F4 (PT bumps versions on the same Abstractions assemblies — coordinate, but no real conflict) |
| **Effort** | ~0.5 day |
| **Branch** | `plan/f3-plugin-contract-versions` |

**Why this matters.** We changed `IAuthorityRulesProvider.InspectionCaseData` mid-session (added the `Scans` list). A plugin DLL built against the old shape would `MissingMethodException` at first call. Today nothing prevents that.

**Deliverable.**
1. New `[ContractVersion("X.Y")]` assembly attribute on every `*.Abstractions` assembly. Initial value `"1.0"`; bump per breaking change.
2. `PluginManifest.MinHostContractVersion` (string, optional). Plugins declare the minimum host contract version they require.
3. `PluginLoader` reads each plugin's manifest, looks up the host's `[ContractVersion]` for the contract type, refuses to register if `host < min`. Clear error log: `"Plugin 'foo' requires X.Y of <Contract>; host has A.B; not loaded."`
4. Update existing `plugin.json`s for the three real plugins to declare `MinHostContractVersion: "1.0"`.

**Files in scope.**
- `platform/NickERP.Platform.Plugins/` — `PluginManifest.cs`, `PluginLoader.cs`, new `ContractVersionAttribute.cs`.
- `modules/inspection/src/NickERP.Inspection.Scanners.Abstractions/AssemblyInfo.cs` (or csproj `<AssemblyAttribute>` entry).
- Same for `ExternalSystems.Abstractions`, `Authorities.Abstractions`.
- `modules/inspection/plugins/*/plugin.json` (three real plugins; mocks optional).

**Acceptance criteria.**
- [ ] `dotnet build` 0 errors.
- [ ] Inspection v2 boots; all five plugins load.
- [ ] Manually editing one `plugin.json` to require `"99.0"` → that plugin is **not** loaded; clear error in startup log; other plugins unaffected.
- [ ] `[ContractVersion]` is visible at runtime via `Assembly.GetCustomAttribute<ContractVersionAttribute>()`.

**Out of scope.** Strong-name signing of plugin DLLs (later hardening). NuGet package version-range checks (separate concern from the runtime contract version).

---

### F4 — TenantId on Plugin Configs

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | F3 (logically — bumps Abstractions contract versions) |
| **Parallel-safe with** | F1, F2 |
| **Effort** | ~0.5 day |
| **Branch** | `plan/f4-tenant-on-plugin-configs` |

**Why this matters.** `ScannerDeviceConfig` and `ExternalSystemConfig` carry `Guid` ids and `string ConfigJson` but no `TenantId`. `IcumsGhAdapter._indexes` is a `static ConcurrentDictionary` keyed by `instanceId|path|ttl` — two tenants with instances pointing at the same physical drop folder would each hold separate cache entries containing all documents in that folder, with no per-tenant filter on the returned data. Plugin-side state must be tenant-aware.

**Deliverable.**
1. `ScannerDeviceConfig` gains `long TenantId` field. Bump `Scanners.Abstractions` `[ContractVersion]` from `1.0` → `1.1`.
2. `ExternalSystemConfig` gains `long TenantId` field. Bump `ExternalSystems.Abstractions` `[ContractVersion]` from `1.0` → `1.1`.
3. Host call sites pass `_tenant.TenantId` when constructing each config.
4. `IcumsGhAdapter._indexes` keyed by `$"{tenantId}|{instanceId}|{path}|{ttl}"`.
5. Update plugin.json manifests to declare `MinHostContractVersion: "1.1"` for the three plugins that bind these contracts.

**Files in scope.**
- `modules/inspection/src/NickERP.Inspection.Scanners.Abstractions/IScannerAdapter.cs`
- `modules/inspection/src/NickERP.Inspection.ExternalSystems.Abstractions/IExternalSystemAdapter.cs`
- `modules/inspection/src/NickERP.Inspection.Web/Services/CaseWorkflowService.cs` (every config construction site)
- `modules/inspection/plugins/NickERP.Inspection.ExternalSystems.IcumsGh/IcumsGhAdapter.cs` (cache key)
- `modules/inspection/plugins/NickERP.Inspection.Scanners.Mock/MockScannerAdapter.cs` (signature only)
- `modules/inspection/plugins/NickERP.Inspection.Scanners.FS6000/FS6000ScannerAdapter.cs` (signature only)
- `modules/inspection/plugins/NickERP.Inspection.ExternalSystems.Mock/MockExternalSystemAdapter.cs` (signature only)
- `modules/inspection/plugins/*.plugin.json` (three real plugins)

**Acceptance criteria.**
- [ ] `dotnet build` 0 errors.
- [ ] All plugins load.
- [ ] `IcumsGhAdapter._indexes` cache miss when querying same instance from a different tenant context.

**Out of scope.** Static-cache eviction on tenant deletion (architectural follow-up).

---

### F5 — Production Minimum

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | F1 (interceptor wiring) for clean health-check semantics |
| **Parallel-safe with** | F2, F3, F4 (different files within Program.cs; coordinate edits) |
| **Effort** | ~1 day |
| **Branch** | `plan/f5-prod-minimum` |

**Why this matters.** Today there are no health endpoints, no migrations-at-startup, no eviction on the image source store, error UI leaks `ex.Message`, and `PreRenderWorker` retries poison messages forever. Each gap is a real production incident waiting to happen.

**Deliverable.**
1. **Health endpoints.** `MapHealthChecks("/healthz/live")` (process is up), `/healthz/ready` (Postgres reachable on both DBs, plugin registry populated, image storage path writable, PreRenderWorker not stuck). Use `AspNetCore.HealthChecks.NpgSql` for Postgres.
2. **Migrations at startup.** Behind a flag `RunMigrationsOnStartup` (default `true` in dev, `false` in prod), `db.Database.Migrate()` runs for `InspectionDbContext` + the three platform contexts during host bootstrap.
3. **Image source eviction.** A `BackgroundService` (`SourceJanitorWorker`) runs on a 1-hour timer, finds `ScanArtifact` rows older than `ImagingOptions.SourceRetentionDays` (default 30) whose source blob is no longer referenced by any case in `Open|Validated|Assigned|Reviewed` state, and deletes the blob.
4. **`UserFacingError(string code, string safeMessage)` helper.** Razor `catch` blocks call this instead of assigning `_message = ex.Message`. Helper logs the full exception with a correlation id (via `ILogger`); UI renders `safeMessage + " (" + correlationId + ")"`.
5. **PreRenderWorker AttemptCount.** New column `AttemptCount int NOT NULL DEFAULT 0` on `scan_render_artifacts` (or a sibling table). After N attempts (default 5), mark `PermanentlyFailed=true` and stop retrying. Surface a count on `/healthz/ready`.

**Files in scope.**
- `modules/inspection/src/NickERP.Inspection.Web/Program.cs` (health + migrations)
- New `modules/inspection/src/NickERP.Inspection.Web/HealthChecks/PreRenderWorkerHealth.cs`
- New `modules/inspection/src/NickERP.Inspection.Imaging/SourceJanitorWorker.cs`
- `modules/inspection/src/NickERP.Inspection.Imaging/PreRenderWorker.cs` (attempt count)
- New migration `<timestamp>_AddPreRenderAttemptCount.cs`
- New `modules/inspection/src/NickERP.Inspection.Web/Services/UserFacingError.cs`
- All Razor pages with `catch` blocks: `Cases.razor`, `CaseDetail.razor`, `NewCase.razor`, `LocationAssignments.razor`, `Scanners.razor`, `Stations.razor`, `Locations.razor`, `ExternalSystems.razor`.
- `apps/portal/Program.cs` (health endpoints — same pattern).

**Acceptance criteria.**
- [ ] `curl /healthz/ready` returns 200 with all components healthy when system is up.
- [ ] Stop Postgres → `/healthz/ready` returns 503 within 10s naming Postgres as the failing component.
- [ ] Cold-boot host → migrations apply automatically (verify by deleting a migration record + restart, check it re-applies).
- [ ] A scan whose source bytes deliberately can't be parsed (write garbage to `<storage>/source/AB/AB....png`) → `PreRenderWorker` retries 5 times, then marks `PermanentlyFailed=true`. No log spam.
- [ ] Throwing in `OpenCaseAsync` → UI shows `safeMessage (correlationId)`, log shows full stack with the same correlationId.
- [ ] `dotnet build` 0 errors.

**Out of scope.** Acceptance-bar instrumentation (V1). Multi-host worker coordination (later — single host assumed for now).

---

## 6. Phase D — Demo

### D1 — Extract `IngestArtifactAsync` Helper

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | F4 (`ScannerDeviceConfig.TenantId` exists) |
| **Parallel-safe with** | F1, F2, F3, F5 |
| **Effort** | ~0.5 day |
| **Branch** | `plan/d1-ingest-helper` |

**Why this matters.** Both the existing `SimulateScanAsync` and the new auto-ingest worker need the same logic: parse adapter output, save source bytes, insert `Scan` + `ScanArtifact`, emit `nickerp.inspection.scan_recorded`. Today that code lives only in `SimulateScanAsync`. Refactoring it into a shared private helper is the precondition for D2.

**Deliverable.** A private `Task<Scan> IngestArtifactAsync(Guid caseId, Guid deviceId, RawScanArtifact raw, CancellationToken ct)` method on `CaseWorkflowService`. `SimulateScanAsync` now calls it after the `await foreach` that grabs one artifact. No behaviour change.

**Files in scope.** `modules/inspection/src/NickERP.Inspection.Web/Services/CaseWorkflowService.cs`.

**Acceptance criteria.**
- [ ] `dotnet build` 0 errors.
- [ ] `SimulateScanAsync` button on `/cases/{id}` works exactly as before (smoke test).
- [ ] Method signature matches what D2 will call.

**Out of scope.** D2's worker. D3's auto-rule-fire.

---

### D2 — `ScannerIngestionWorker` BackgroundService

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | D1, F4 |
| **Parallel-safe with** | D3 (D3 touches `FetchDocumentsAsync` and `CaseDetail.razor`) |
| **Effort** | ~1 day |
| **Branch** | `plan/d2-scanner-ingestion-worker` |

**Why this matters.** `IScannerAdapter.StreamAsync` exists in the FS6000 plugin but nothing calls it. This is the single missing piece that turns "pieces that exist" into "demo runs end-to-end without a button click."

**Deliverable.** New `ScannerIngestionWorker : BackgroundService` that:
1. On a 30s timer, enumerates active `ScannerDeviceInstance` rows (per tenant — the worker is process-wide; runs the loop inside a tenancy-aware scope per instance).
2. For each instance, resolves the `IScannerAdapter` via `IPluginRegistry`, builds `ScannerDeviceConfig` with the instance's `TenantId`, calls `StreamAsync` with a per-cycle `CancellationTokenSource` that times out after 25s.
3. For each emitted `RawScanArtifact`: open or reuse a case (find existing `Open` case for this `LocationId`+`SubjectIdentifier`= filename stem; otherwise `OpenCaseAsync`), call `IngestArtifactAsync`.
4. Idempotent across worker restarts: `Scan.IdempotencyKey` is content-addressed (`raw.Bytes` SHA-256 prefix). Duplicate keys in the same case → no-op.

Register via `builder.Services.AddHostedService<ScannerIngestionWorker>();` in `Program.cs`.

**Files in scope.**
- New `modules/inspection/src/NickERP.Inspection.Web/Services/ScannerIngestionWorker.cs`
- `modules/inspection/src/NickERP.Inspection.Web/Program.cs` (registration only)

**Acceptance criteria.**
- [ ] Drop a real FS6000 triplet (`{stem}high.img + {stem}low.img + {stem}material.img`) into the FS6000 instance's `WatchPath`. Within 30s: a new case appears on `/cases` with thumbnail rendered.
- [ ] Re-drop the same triplet → no duplicate case.
- [ ] Stop the host → triplet stays in the watch folder. Restart → case appears.
- [ ] Tenant context is correct: case's `TenantId` matches the device instance's `TenantId`.
- [ ] `dotnet build` 0 errors.

**Out of scope.** External-document auto-fetch (analyst still clicks "Fetch documents" — auto-fetch comes after the ICUMS endpoint shape is firmed up).

---

### D3 — Auto-Fire Authority Rules After FetchDocuments

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none structural; depends on the existing `EvaluateAuthorityRulesAsync` shipped earlier |
| **Parallel-safe with** | D1, D2 |
| **Effort** | ~0.25 day |
| **Branch** | `plan/d3-auto-fire-rules` |

**Why this matters.** Today the analyst has to click "Run authority checks" after fetching documents. The rules pack we shipped should run automatically — that's why we built it.

**Deliverable.** `CaseWorkflowService.FetchDocumentsAsync` calls `EvaluateAuthorityRulesAsync(caseId, ct)` at the end, **after** the `case_validated` event emits. Best-effort: an exception from one provider is logged, doesn't fail the fetch.

`CaseDetail.razor` reads the persisted result on render (today the result lives in component state — once V3 lands the result lives in the DB; until then, the auto-fire updates the component state during the page's `Reload` cycle).

**Files in scope.**
- `modules/inspection/src/NickERP.Inspection.Web/Services/CaseWorkflowService.cs` (`FetchDocumentsAsync`)
- `modules/inspection/src/NickERP.Inspection.Web/Components/Pages/CaseDetail.razor` (auto-display result)

**Acceptance criteria.**
- [ ] After `FetchDocumentsAsync` succeeds, the authority-checks panel is populated without a separate click.
- [ ] A throwing rules provider doesn't break document fetch.
- [ ] `dotnet build` 0 errors.

**Out of scope.** Persistence of evaluation results (V3).

---

### D4 — End-to-End Demo Smoke Test

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | D1, D2, D3, F1, F4, F5 |
| **Parallel-safe with** | none (this is the integration gate) |
| **Effort** | ~0.5 day |
| **Branch** | `plan/d4-e2e-smoke` |

**Why this matters.** Integration is where assumptions break. Until a single human-driven scenario walks through the whole pipeline, "code complete" is a lie.

**Deliverable.** A scripted scenario (in `tests/e2e/` or as a `tests/NickERP.Inspection.E2E.Tests/` project that uses `WebApplicationFactory` + a real Postgres via Testcontainers) that:
1. Configures one Tema location, one FS6000 instance with a watch folder, one ICUMS-Gh instance with a drop folder.
2. Drops a real FS6000 triplet + a sample ICUMS batch JSON.
3. Waits up to 60s for the case to appear.
4. Asserts: case exists, thumbnail rendered, BOE fetched, rules evaluated with expected violations, verdict submitted, outbox JSON file written, ≥8 `nickerp.inspection.*` events on `audit.events`.

Plus a markdown runbook at `docs/runbooks/demo-walkthrough.md` documenting the manual version of this for analyst training.

**Files in scope.**
- New `tests/NickERP.Inspection.E2E.Tests/`
- New `docs/runbooks/demo-walkthrough.md`
- Updates to `TESTING.md` pointing at the new demo flow.

**Acceptance criteria.**
- [ ] `dotnet test --filter Category=E2E` passes.
- [ ] Manual walkthrough per `demo-walkthrough.md` succeeds in under 5 minutes.
- [ ] All 8+ DomainEvents visible on Portal `/audit`.

**Out of scope.** Performance benchmarking against ARCHITECTURE.md acceptance bars (V1).

---

## 7. Phase V — Validation (deferred to next sprint)

Tracked here for context. **Do not execute this sprint.**

| ID | Title | Predecessors | Effort |
|---|---|---|---|
| V1 | Acceptance-bar instrumentation (`image.serve_ms{kind,tier}`, `prerender.render_ms`, `case.state_transitions_total`, `/perf` admin page) | F+D | ~1 day |
| V2 | Multi-location proof — Tema + Kotoka with two users, RLS canary | F1, D | ~1 day |
| V3 | Persist `RuleEvaluation` rows (analyst sees historical checks on case open + queryable from `/audit`) | D3 | ~1 day |
| V4 | Analyst viewer Razor — W/L sliders, 16-bit decode, ROI inspector, pixel probe | F+D+V1 | ~3-5 days |

---

## 8. Phase G — Generalization (deferred)

| ID | Title | Predecessors | Effort |
|---|---|---|---|
| G1 | Platform tightening — decimal-as-string in `DomainEvent` payload, audit channel routing accepts globs, `ITenantContext.SetSystemContext`, plugin module/namespace partitioning, scope-claim regex | V | ~2 days |
| G2 | NickFinance Petty Cash pathfinder — first non-Inspection consumer; stress-tests platform | G1 | ~5-7 days |

---

## 9. Phase P — Production prep (deferred)

| ID | Title | Predecessors | Effort |
|---|---|---|---|
| P1 | Operations runbooks — deploy, secret rotation, pre-render-stuck, plugin-load-failure, ICUMS outbox backlog | G | ~1 day |
| P2 | Edge node — SQLite buffer + replay on reconnect | G | ~5-7 days |
| P3 | Audit projection + notifications inbox | G | ~2-3 days |

---

## 10. Out of scope (explicit)

These were mentioned in earlier sessions and should NOT land this sprint:

- **§4.3.c (RuleEvaluation persistence)** — moved to V3.
- **§4.3.d (analyst viewer)** — moved to V4.
- **§4.3.e (SignalR `AssetReady` push)** — defer until V4.
- **§4.5 multi-location** — moved to V2.
- **§4.6 NickFinance** — moved to G2.
- **§4.7 audit projections** — moved to P3.
- **§4.8 edge node** — moved to P2.

---

## 11. Deeper-dive team briefs (supplementary)

Four work items have **detailed dispatch briefs** at `docs/sprint/team-*.md` with full code snippets, commit-message templates, and edge-case notes. Use these when handing the work to a fresh agent that wants more context than the work-item card provides.

| Work item | Detailed brief |
|---|---|
| F1 | [`docs/sprint/team-tenant-safety.md`](docs/sprint/team-tenant-safety.md) |
| F3 | [`docs/sprint/team-plugin-contracts.md`](docs/sprint/team-plugin-contracts.md) |
| F4 | [`docs/sprint/team-plugin-tenancy.md`](docs/sprint/team-plugin-tenancy.md) |
| D1 + D2 + D3 | [`docs/sprint/team-demo-path.md`](docs/sprint/team-demo-path.md) |

F2 (test foundation), F5 (production minimum), and D4 (e2e smoke) don't have separate briefs yet — their cards in §5/§6 are sufficient.

## 12. How to launch parallel agents

To run F1, F2, F3, F4 concurrently (the safe parallel set):

1. From the parent shell:
   ```bash
   cd "C:\Shared\ERP V2"
   git worktree add ../erp-v2-f1 -b plan/f1-rls-interceptors
   git worktree add ../erp-v2-f2 -b plan/f2-test-foundation
   git worktree add ../erp-v2-f3 -b plan/f3-plugin-contract-versions
   git worktree add ../erp-v2-f4 -b plan/f4-tenant-on-plugin-configs
   ```
2. Spawn one Claude Agent per worktree, feeding each agent the corresponding section of this `PLAN.md` (e.g., F1's full work-item card) plus the **standing rules** from §4.
3. Each agent works in its own worktree to completion (acceptance criteria pass), commits, and pushes. They do **not** merge — that's the human's job, in the order F1 → F3 → F4 → F2 → F5 → D1 → D3 → D2 → D4.
4. Merge conflicts arise mostly on `Program.cs` and `CaseWorkflowService.cs` — resolve with the rule "tenant safety wins, then plugin contracts, then tenant configs, then demo wiring."

After F merges, repeat for D1+D3 in parallel, then D2 (which depends on D1), then D4 (which depends on everything).

---

## 13. Status snapshot

Update this table as items move. Source of truth for "where are we?"

| ID | Status | Branch | Merge commit |
|---|---|---|---|
| F1 | **done** | merged + cleaned | `90f0d67` |
| F2 | **done** | merged + cleaned | `2dde6db` |
| F3 | **done** | merged + cleaned | `f941f71` |
| F4 | **done** | merged + cleaned | `aa9abb9` |
| F5 (slices 1+2) | **done** | merged + cleaned | `91cb390` |
| F5 (slice 3) | **deferred** — needs F6 first | `plan/f5-prod-minimum` (commit `8b29407` kept on origin) | — |
| D1 | **done** | merged + cleaned | `6b16c23` |
| D2 | **done** | merged + cleaned | `749f273` |
| D3 | **done** | merged + cleaned | `3dee869` |
| D4 | **done** | merged + cleaned | `b101c7b` |

**Sprint Foundation+Demo: 9 of 9 scoped items shipped.** Two follow-ups discovered during execution and parked:

- **F6** — Identity-Tenancy Interlock (must land before F5 slice 3 can merge).
- **F7** — Background-worker tenant resolution (PreRenderWorker + SourceJanitorWorker silently fail on the live host because they don't `SetTenant(...)` per cycle. D4's e2e test masks the bug via a single-tenant stub `ITenantContext` in the WebApplicationFactory; the live host has the bug). **High priority** — blocks the actual demo working on :5410 even though F1..F5 + D1..D4 say "done."

### F5 slice 3 deferral note (added 2026-04-27)

F5 slice 3 (commit `8b29407` on `plan/f5-prod-minimum`) creates a non-superuser `nscim_app` Postgres role with `LOGIN NOSUPERUSER NOBYPASSRLS`, grants per-schema CRUD, and updates `appsettings.json` to connect as `nscim_app`. The role + grants migrations are correct; verified manually that under `nscim_app`, RLS actually enforces (where as `postgres` — which has `BYPASSRLS` — silently nullified the policies F1 added).

**Why deferred:** the appsettings switch breaks `DbIdentityResolver.FindUserByEmailAsync`. Auth runs *before* the tenancy middleware sets `app.tenant_id`, so under a `NOBYPASSRLS` connection the user-lookup query returns 0 rows → 401 → demo dies. The fix is architectural: either bypass tenancy on the user-lookup path (`SET app.tenant_id = '0'` for that one query, since `identity.users` is the root that establishes the tenant), or mark `IdentityUser` reads as `SECURITY DEFINER` callable. Needs design, not a hot-fix.

**New work item to schedule before merging slice 3:**

### F6 — Identity-Tenancy Interlock (NEW, deferred)

| | |
|---|---|
| **Status** | pending (blocking F5 slice 3) |
| **Predecessors** | F1 (interceptors must be in place — done) |
| **Effort** | ~0.5–1 day |
| **Branch** | `plan/f6-identity-tenancy-interlock` |

**Deliverable.** Teach `DbIdentityResolver` (and the `NickErpAuthenticationHandler` that calls it) to perform the user-lookup hop with tenancy disabled — either by injecting an `ITenantContext.SetSystemContext()` for that one query, by using a connection-string variant that opts out of the interceptor, or by making the `identity.users` read SECURITY DEFINER on the Postgres side. Pick the least-invasive option after reviewing the auth handler's lifecycle. After this, slice 3 can merge — and `nscim_app` becomes the production posture.

**Acceptance.** Switch the dev host's connection string to `nscim_app`; `/cases` still renders 200; `/healthz/ready` still returns 200; `psql -U nscim_app -d nickerp_inspection -c "SELECT count(*) FROM inspection.cases;"` (no `app.tenant_id`) still returns 0.

### F7 — Background-Worker Tenant Resolution (NEW, high priority)

| | |
|---|---|
| **Status** | pending (live-system bug) |
| **Predecessors** | F1 (interceptor wiring — done), D2 (worker reference pattern — done) |
| **Effort** | ~0.5 day |
| **Branch** | `plan/f7-worker-tenant-resolution` |

**Why this matters.** Surfaced by D4's e2e test. F1's `TenantOwnedEntityInterceptor` throws on `SaveChanges` when `_tenant.IsResolved == false`. `D2.ScannerIngestionWorker` calls `tenant.SetTenant(instance.TenantId)` per cycle and works correctly. **`PreRenderWorker` (F5 slice 2) and `SourceJanitorWorker` (F5 slice 2) do not** — both quietly throw and log a warning, then move on, on every cycle. Symptom on the live host: scans ingest, source bytes write to disk, but `scan_render_artifacts` rows never land — analyst sees no thumbnail. The `/healthz/ready` endpoint still returns 200 because the workers are *running*, not *succeeding*.

**Deliverable.** Fix both workers to set tenant context per artifact (or per row) before any `SaveChanges`. The artifact already carries `TenantId`; mirror `ScannerIngestionWorker`'s approach:
1. Inside the per-cycle scope, after loading the artifact (or row of work), call `_tenant.SetTenant(artifact.TenantId)`.
2. Then proceed with the read/write that triggers `SaveChanges`.

For `PreRenderWorker.DrainOnceAsync`: each `ScanArtifact` row in the batch carries `TenantId`. Set per-row tenant before the render+persist step. Worker is single-threaded per cycle; loop variable is fine.

For `SourceJanitorWorker`: same pattern — for each candidate row, set tenant before the eviction-eligibility query and the optional cleanup write.

D4's `E2EWebApplicationFactory` stub `ITenantContext` should be removed once this lands; the e2e test then exercises the real wiring.

**Acceptance.**
- `PreRenderWorker` produces `scan_render_artifacts` rows under the live host (no stub) within 30s of a scan ingest.
- `SourceJanitorWorker` runs without exceptions in the log.
- D4's e2e test passes without the `ITenantContext` stub override.
- `dotnet build` 0 errors; existing tests still pass.

**Out of scope.** Multi-host worker leases. Don't refactor the discovery-loop topology.

---

*Last updated: 2026-04-27. When this plan changes substantively, bump the date and note the rationale at the top.*

### Change log
- 2026-04-27: Initial sprint draft. Wave 1 (F1+F2+F3) dispatched, merged at `2dde6db`. Wave 2 (F4+F5+D3) dispatched, merged at `3dee869`. F5 slice 3 deferred pending F6 (identity-tenancy interlock); branch + commit preserved on origin.
- 2026-04-27: Wave 3 — D1 dispatched + merged at `6b16c23`; D2 dispatched + merged at `749f273`; D4 dispatched + merged at `b101c7b`. **Sprint Foundation+Demo: 9 of 9 scoped items shipped.** D4's e2e test surfaced F7 (background-worker tenant resolution) — high-priority follow-up because the live host silently fails to render thumbnails. F7 added above; queued for the next dispatch.
- 2026-04-28: Sprint 2 drafted (§14). Bundles F6 + F7 + F5 slice 3 + V1 + V2 + V3. V4 (analyst viewer) explicitly out of scope — owns its own sprint.
- 2026-04-28: Sprint 2 Wave 1 (H1+H2+A1+A2) dispatched, merged at `9bc66d9`. Wave 2 (H3) dispatched: first attempt rejected for `GRANT CREATE ON SCHEMA public` posture-weakening shortcut; re-dispatched with the cleaner alternative — relocate `__EFMigrationsHistory` per-context schemas — merged at `8fe01ef`. **Live host flipped to `nscim_app`; "multi-tenant from day 1" now DB-side enforced.** Wave 3 (E1) dispatched, merged at `21a548b`. **Sprint 2: 6 of 6 scoped items shipped.** E1 surfaced an in-scope production bug (`CaseWorkflowService.CurrentActorAsync` throwing from non-Razor scopes) — fixed in the same PR. Six followups logged in §14.6.

---

## 14. Sprint 2 — Production Hardening + Analyst Polish

### 14.0 Goal

Close the gap between merged code and observable production behaviour. After Sprint 2, the live host on `:5410` actually delivers what `ARCHITECTURE.md` promised: RLS enforces under a non-superuser role, every background worker handles tenant context the same way, analysts see persistent rule history with measurable acceptance bars, and a multi-location federation demo proves the "federation by location" + "multi-tenant from day 1" claims hold end-to-end.

### 14.1 Why this sprint

Sprint 1 closed all 9 scoped items but left three known gaps and surfaced a fourth bug:

1. **F5 slice 3 (`nscim_app` non-superuser role)** — coded on `plan/f5-prod-minimum`, intentionally not merged because it would break auth.
2. **F6 (identity-tenancy interlock)** — designed but unbuilt; F5 slice 3 depends on it.
3. **F7 (worker tenant resolution)** — discovered by D4's e2e test. `PreRenderWorker` + `SourceJanitorWorker` silently fail on the live host because they don't `SetTenant(...)` per cycle. **The live demo's thumbnails don't render today** even though every Sprint 1 item is technically "done."
4. **V-track items** — `ROADMAP.md` lists them post-demo. Three are now ready: V1 (instrument what we built), V3 (persist what we evaluate), V2 (prove federation).

The deeper lesson from Sprint 1: F1's "RLS forced everywhere" pattern means *every* DB-touching code path now has to remember to set tenant context. `ScannerIngestionWorker` got it right; `PreRenderWorker` didn't. Sprint 2 establishes the canonical pattern so future workers/controllers/hosted-services have a single example to copy.

### 14.2 Phases

```
        ┌── H1 (F7) ──┐
        │             │
        ├── H2 (F6) ──┼── all parallel-safe (Wave 1)
WAVE 1  │             │
        ├── A1 (V3) ──┤
        │             │
        └── A2 (V1) ──┘
                            │
                            ▼
WAVE 2              H3 (F5 slice 3 — verify nscim_app boots; merge parked branch)
                            │
                            ▼
WAVE 3              E1 (V2 multi-location proof — integration gate)
```

**Wall clock:** ~2 days (waves 1+3) + 0.25 day for H3. Total work ~4.75 days across 6 items.

### 14.3 Out of scope (explicit, with rationale)

- **V4 — Analyst viewer.** 3-5 day item; deserves its own sprint with focus. Cramming it into Sprint 2 would re-create the feature-factory dynamic that the new plan is structured to prevent.
- **G1, G2 — NickFinance prep.** Wait until V4 ships. Finance stresses the platform differently from Inspection; stressing a half-finished platform wastes both efforts.
- **P1 — Operations runbooks (standalone).** Folded into V2's integration gate — the multi-location proof IS the deploy runbook in concrete form. Standalone runbooks return in Sprint 4 (or whenever production deploy is imminent).

### 14.4 Conventions

Inherits from §4. Specifically:

- Branch: `plan/<phase><id>-<kebab-name>` (e.g., `plan/h1-worker-tenant-resolution`, `plan/a2-acceptance-bars`).
- Standing rules unchanged (v1 read-only, no skipping hooks, no security weakening, build-must-be-green, stay in scope, don't update Status table).
- Worktrees off latest main; agents commit + push their branch; merging is the human's job.

### 14.5 Work items

#### H1 — Background-Worker Tenant Resolution (was F7)

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none (F1's interceptor wiring already in main) |
| **Parallel-safe with** | H2, A1, A2 |
| **Effort** | ~0.5 day |
| **Branch** | `plan/h1-worker-tenant-resolution` |

**Why this matters.** The live host's `PreRenderWorker` (and `SourceJanitorWorker`) fail silently on every cycle because F1's `TenantOwnedEntityInterceptor` throws `InvalidOperationException` when `_tenant.IsResolved == false`. Thumbnails write to disk; rows never land. `D2.ScannerIngestionWorker` already shows the correct pattern: iterate `tenancy.tenants` (the only RLS-exempt table — root context establisher), call `_tenant.SetTenant(tenant.Id)` per iteration, then run the workload.

**Deliverable.**

1. `PreRenderWorker.DrainOnceAsync` discovery: change from "select all unrendered ScanArtifacts" to "for each active tenant, set context, query that tenant's unrendered rows, render them." Mirror `ScannerIngestionWorker`'s tenant loop. The artifact's `TenantId` is what `SetTenant(...)` receives.
2. `SourceJanitorWorker.RunOnceAsync` (or whatever the cycle method is named): same pattern.
3. After the fix, **D4's e2e test must pass without the `ITenantContext` stub** in `E2EWebApplicationFactory`. Remove the stub (or leave a one-line note explaining why it's no longer needed).
4. Add a small unit-style test (in `tests/NickERP.Inspection.Web.Tests/`) that asserts `PreRenderWorker.DrainOnceAsync` succeeds in a multi-tenant scenario and produces `scan_render_artifacts` rows under both tenants without a stub.

**Files in scope.**
- `modules/inspection/src/NickERP.Inspection.Imaging/PreRenderWorker.cs`
- `modules/inspection/src/NickERP.Inspection.Imaging/SourceJanitorWorker.cs`
- `tests/NickERP.Inspection.E2E.Tests/E2EWebApplicationFactory.cs` (remove the stub)
- `tests/NickERP.Inspection.Web.Tests/` (new multi-tenant prerender test)

**Acceptance criteria.**
- `dotnet build` 0 errors, 0 new warnings.
- `dotnet test` (all categories) all tests pass; the e2e test passes **without the ITenantContext stub**.
- Live host on `:5410` (after merge + restart): drop a synthetic FS6000 triplet → within 30s a `scan_render_artifacts` row appears for `kind=thumbnail`. Verify via `psql`. (No need for the full FS6000 byte format here; reuse the byte-synth helper from F2's tests if needed.)
- Worker logs no `InvalidOperationException` from `TenantOwnedEntityInterceptor` over a 5-minute idle window.

**Out of scope.** Multi-host worker leases. Sub-minute discovery cadence (the 60-second cycle is fine).

---

#### H2 — Identity-Tenancy Interlock (was F6)

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | F1 (in main) |
| **Parallel-safe with** | H1, A1, A2 |
| **Effort** | ~1 day |
| **Branch** | `plan/h2-identity-tenancy-interlock` |

**Why this matters.** `DbIdentityResolver.FindUserByEmailAsync` is the chicken-and-egg of the auth flow: it must read `identity.users` to determine the principal's tenant, but the tenancy middleware sets `app.tenant_id` AFTER auth resolves. Today the host runs as `postgres` (BYPASSRLS) so the read works; flipping to `nscim_app` (NOBYPASSRLS, the production posture) makes the read return zero rows → 401 → demo dies.

**The chosen design (cleanest of four options surveyed):** `identity.users` is the table that *establishes* tenant context, so it sits at the root of the dependency graph. RLS on this table is fundamentally circular. Carve it out from `FORCE ROW LEVEL SECURITY` while leaving every other tenant-owned table (locations, cases, scans, assignments, etc.) protected. The `users` table still has a `TenantId` column; consumers that join through `users` to other tables hit RLS on those tables. Defense-in-depth holds for the data; the user-discovery hop is the single intentional carve-out.

**Deliverable.**

1. New migration on `nickerp_platform.identity` that runs:
   ```sql
   ALTER TABLE identity.users NO FORCE ROW LEVEL SECURITY;
   DROP POLICY tenant_isolation_users ON identity.users;
   ```
   Document the carve-out in the migration's XML doc-comment with the rationale above.
2. Add a regression assertion in `DbIdentityResolver` (or its tests): if the table ever has RLS re-enabled in a future migration, an integration-style check should fail loudly. Practical implementation: a startup check that runs `SELECT relforcerowsecurity FROM pg_class WHERE relname = 'users' AND relnamespace = 'identity'::regnamespace` and logs `IDENTITY-USERS-RLS-RE-ENABLED` if it returns true. Throwing is too aggressive (blocks startup in dev); a structured warning is enough.
3. Update `docs/ARCHITECTURE.md` §7.1 (Tenant + Location isolation) with a paragraph documenting the carve-out, the rationale, and what protects against the leak it would otherwise create (every other table joins back through `users` and hits its own RLS).

**Files in scope.**
- New migration: `platform/NickERP.Platform.Identity.Database/Migrations/<timestamp>_RemoveRlsFromIdentityUsers.cs`
- `platform/NickERP.Platform.Identity.Database/Services/DbIdentityResolver.cs` (startup check)
- `docs/ARCHITECTURE.md` §7.1

**Acceptance criteria.**
- Migration applied; `pg_class.relforcerowsecurity` returns `false` for `identity.users`, still `true` for every other identity.* table.
- After H2 merges, swap dev appsettings to `Username=nscim_app` (just locally, don't commit) → host boots clean → `/cases` renders 200 → swap back. (H3 will handle the merged appsettings change.)
- `dotnet build` 0 errors. Existing tests pass.
- Architecture doc updated with the carve-out paragraph.

**Out of scope.** Renaming `users` to `identity_users` (the actual table name uses `users` — verify before writing migration; if it's `identity_users`, adjust). Multi-tenant SECURITY DEFINER complexity. Strong-naming.

---

#### H3 — F5 Slice 3 Cutover (`nscim_app` Production Posture)

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | H2 (must merge first) |
| **Parallel-safe with** | (waits for H2) |
| **Effort** | ~0.25 day |
| **Branch** | `plan/f5-prod-minimum` (already exists, commit `8b29407`) |

**Why this matters.** Slice 3's role + grants migrations are correct (verified manually in Sprint 1). The blocker was H2. After H2 merges, slice 3 becomes a verify-and-merge.

**Deliverable.**

1. Rebase `plan/f5-prod-minimum` onto post-H2 main.
2. Apply slice 3's role + grants migrations to dev (already partially applied during Sprint 1 verification — re-run idempotently).
3. Verify under the new appsettings (`Username=nscim_app`):
   - Host boots clean on `:5410`.
   - `/healthz/ready` returns 200.
   - `/cases`, `/locations`, `/scanners` etc. render 200.
   - `psql -U nscim_app -d nickerp_inspection -c "SELECT count(*) FROM inspection.cases;"` (no `app.tenant_id` set) returns **0** — the RLS enforcement that's been a code-side claim since F1 is now also DB-side enforced under the production role.
4. Merge `plan/f5-prod-minimum` to main. Delete the branch local + remote.
5. Update `TESTING.md` env-var template to use `Username=nscim_app`.

**Acceptance criteria.**
- Host runs as `nscim_app` end-to-end.
- `psql -U nscim_app` without `app.tenant_id` returns 0 rows from any tenanted table.
- D4's e2e test still passes (it uses its own DB role; verify nothing assumes `postgres`).
- `TESTING.md` updated.

**Out of scope.** New work. This is purely the merge + verification.

---

#### A1 — RuleEvaluation Persistence (was V3)

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none (D3 already wired the auto-fire path in main) |
| **Parallel-safe with** | H1, H2, A2 |
| **Effort** | ~1 day |
| **Branch** | `plan/a1-rule-evaluation-persistence` |

**Why this matters.** Today, `EvaluateAuthorityRulesAsync` returns a `RulesEvaluationResult` that lives only in the calling page's component state. Reload a case, rules result vanishes; analyst has to click "Run authority checks" again. The auto-fire from D3 (which lands the result into the page after `FetchDocumentsAsync`) gets blown away on every navigation. That's the analyst's most annoying paper-cut today.

**Deliverable.**

1. New entity `RuleEvaluation` in `NickERP.Inspection.Core.Entities`:
   ```
   Id, CaseId, EvaluatedAt, AuthorityCode,
   ViolationsJson (jsonb), MutationsJson (jsonb),
   ProviderErrorsJson (jsonb), TenantId
   ```
   One row per evaluation run. Most-recent-per-case is the analyst's view; full history is queryable from `/audit`.

2. Migration on `nickerp_inspection`:
   - `inspection.rule_evaluations` table with the columns above.
   - RLS + `FORCE ROW LEVEL SECURITY` + `tenant_isolation_rule_evaluations` policy (mirror F1's pattern).
   - Index `(TenantId, CaseId, EvaluatedAt DESC)` for the latest-per-case query.

3. `CaseWorkflowService.EvaluateAuthorityRulesAsync` persists the result before returning:
   - For each `EvaluatedViolation` per `AuthorityCode`, group + serialize.
   - Insert one `RuleEvaluation` row per AuthorityCode (one per provider). Keeps queries simple — "latest evaluation per (case, authority)" is the natural index.
   - Existing `nickerp.inspection.rules_evaluated` event keeps firing.

4. `CaseDetail.razor`'s `Reload()` reads the latest `RuleEvaluation` row(s) for the case and hydrates `_rulesResult`. The page works correctly on cold load (no fetch click required) when prior evaluations exist.

5. The "Run authority checks" button still re-runs and re-persists.

**Files in scope.**
- New: `modules/inspection/src/NickERP.Inspection.Core/Entities/RuleEvaluation.cs`
- `modules/inspection/src/NickERP.Inspection.Database/InspectionDbContext.cs` (DbSet + entity config + RLS policy in migration)
- New migration on `nickerp_inspection`
- `modules/inspection/src/NickERP.Inspection.Web/Services/CaseWorkflowService.cs` (persist in `EvaluateAuthorityRulesAsync`)
- `modules/inspection/src/NickERP.Inspection.Web/Components/Pages/CaseDetail.razor` (hydrate on `Reload`)

**Acceptance criteria.**
- Migration applied; `inspection.rule_evaluations` exists with the policy + index.
- Test (in `tests/NickERP.Inspection.Web.Tests/`): evaluate rules → reload page → assert prior result is visible without a re-fetch click.
- `dotnet build` 0 errors; existing tests pass.
- Auditable: an evaluation produces both a row in `rule_evaluations` AND the existing `nickerp.inspection.rules_evaluated` event in `audit.events`.

**Out of scope.** A separate `RuleViolation` table (we keep `ViolationsJson` for now; queryable via Postgres jsonb operators if needed). UI for browsing historical evaluations beyond the most-recent-render. Performance optimization.

---

#### A2 — Acceptance-Bar Instrumentation (was V1)

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none |
| **Parallel-safe with** | H1, H2, A1 |
| **Effort** | ~1 day |
| **Branch** | `plan/a2-acceptance-bars` |

**Why this matters.** `ARCHITECTURE.md` §7.7 specifies acceptance bars for the image pipeline (thumbs ≤ 50ms p95, previews ≤ 80ms p95, cache hit rate ≥ 85%, ingestion throughput ±5%). Sprint 1's audit found that none of these are measurable today. We literally don't know if the pipeline meets the spec. After A2 lands, we will.

**Deliverable.**

Four OTel meters/histograms, each as `System.Diagnostics.Metrics.Histogram<double>` or `Counter<long>` on `NickErpActivity.Meter`:

1. `nickerp.inspection.image.serve_ms` — image endpoint response time. Tags: `kind` (`thumbnail`/`preview`), `status` (200/304/404). Wrap the lambda in `modules/inspection/src/NickERP.Inspection.Web/Program.cs`.
2. `nickerp.inspection.prerender.render_ms` — renderer duration. Tags: `kind`, `mime`. Wrap `PreRenderWorker.TryRenderAndPersistAsync`.
3. `nickerp.inspection.scan.ingest_ms` — scan ingestion duration. Tags: `scanner_type_code`. Wrap `CaseWorkflowService.IngestArtifactAsync`.
4. `nickerp.inspection.case.state_transitions_total` — counter. Tags: `from`, `to`. Increment alongside each `EmitAsync(... case_*)` call in `CaseWorkflowService`.

Plus a small admin page `/perf` that exposes a snapshot of the meters' current rolling histograms. Implementation can be simple — read from `System.Diagnostics.Metrics.MeterListener` registered at startup, render p50/p95/p99 + bucket counts in a small Razor table. Refreshes every 5 seconds. Internal-only (`[Authorize]` already gates it).

**Files in scope.**
- `platform/NickERP.Platform.Telemetry/NickErpActivity.cs` (if histograms aren't already centrally defined; otherwise add inline)
- `modules/inspection/src/NickERP.Inspection.Web/Program.cs` (image endpoint timing + meter setup)
- `modules/inspection/src/NickERP.Inspection.Imaging/PreRenderWorker.cs`
- `modules/inspection/src/NickERP.Inspection.Web/Services/CaseWorkflowService.cs`
- New: `modules/inspection/src/NickERP.Inspection.Web/Components/Pages/Perf.razor`
- New: `modules/inspection/src/NickERP.Inspection.Web/Services/MeterSnapshotService.cs` (singleton MeterListener; surfaces snapshots to the page)

**Acceptance criteria.**
- After running the e2e test: `/perf` shows non-zero counts for at least 3 of the 4 meters (case-state-transitions, scan ingest, prerender render — image serve only fires when an image is actually fetched, so requires a separate browser hit).
- Histograms have p50/p95/p99 displayed.
- `dotnet build` 0 errors. Existing tests pass.
- The image endpoint's response time stays under the spec bars on a 100-request burst (smoke check; not a formal AC since Sprint 2's role is to *measure*, not *meet* — meeting comes later if we're off).

**Out of scope.** Prometheus scraping integration, alerting, dashboards beyond `/perf`. Production observability story.

---

#### E1 — Multi-Location Federation Proof (was V2 — integration gate)

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | H1, H2, H3 (all merged) |
| **Parallel-safe with** | none (this is the gate) |
| **Effort** | ~1 day |
| **Branch** | `plan/e1-multi-location-proof` |

**Why this matters.** "Federation by location" (vision §1.1) and "multi-tenant from day 1" (vision §1.6) are the two anchor claims. Sprint 1 made them code-true. Sprint 2's gate makes them *observably* true via a scripted scenario.

**Deliverable.**

A new e2e test in `tests/NickERP.Inspection.E2E.Tests/MultiLocationFederationTests.cs` that:

1. Spins up a fresh DB pair (reuse `PostgresFixture` from D4). Apply migrations under `nscim_app` (proving H3's role works for migrations + interceptors). Then connect the test as `nscim_app` for the assertions.

2. Seeds:
   - **Tenant 1** (`nick-tc-scan`) with locations `tema` + `kotoka`. Scanner instances: `tema-fs6000` (FS6000 watching `<temp>/tema-incoming/`), `kotoka-fs6000` (FS6000 watching `<temp>/kotoka-incoming/`). Users: `analyst-tema@t1` assigned to `tema` only; `analyst-kotoka@t1` assigned to `kotoka` only.
   - **Tenant 2** (`other-customer`) with location `tema` (different physical tenant, same vocabulary). Scanner instance: `tema-fs6000` watching `<temp>/t2-tema-incoming/`. User: `analyst-tema@t2` assigned to its `tema`.

3. Drops scans into all three watch folders simultaneously. Waits up to 60s for cases to appear.

4. Assertions:
   - **App-layer federation:** `analyst-tema@t1` requests `/cases` → sees only Tenant 1 / Tema cases. `analyst-kotoka@t1` → only Tenant 1 / Kotoka cases. `analyst-tema@t2` → only Tenant 2 / Tema cases.
   - **DB-layer RLS:** `psql -U nscim_app` without `app.tenant_id` set → `SELECT count(*) FROM inspection.cases` returns **0**. With `SET app.tenant_id = '1'` → returns Tenant 1's cases only.
   - **Cross-tenant URL guess:** `analyst-tema@t1` GETs `/cases/{tenant-2-case-id}` directly → returns 404 (route fails to resolve under tenant 1's RLS), not 200 with the foreign data.
   - **Audit:** events for all three tenants land in `audit.events`, each tagged with the correct `TenantId`. Cross-tenant queries on `audit.events` fail without the right context.

5. Markdown runbook at `docs/runbooks/federation-walkthrough.md` — same as D4's demo runbook but with the multi-tenant + multi-location seeding steps explicit. This becomes the deploy-day walkthrough for adding a second location.

**Files in scope.**
- New: `tests/NickERP.Inspection.E2E.Tests/MultiLocationFederationTests.cs`
- Helper: extend `tests/NickERP.Inspection.E2E.Tests/E2EFixtures.cs` for multi-tenant seeding
- New: `docs/runbooks/federation-walkthrough.md`

**Acceptance criteria.**
- The new e2e test passes under `dotnet test --filter Category=Integration` in <90s.
- All four assertions above hold.
- The runbook is internally consistent (every step has a file path / button name / psql command).
- Existing tests still pass.

**Out of scope.** Cross-tenant audit aggregation (the system tenant context, G1). Performance under multi-location load. Edge node sync.

---

### 14.6 Status snapshot

| ID | Phase | Status | Merge commit |
|---|---|---|---|
| H1 | Hardening | **done** | `0a40242` |
| H2 | Hardening | **done** | `03330fd` |
| H3 | Hardening | **done** (clean alternative — see followup #1) | `8fe01ef` |
| A1 | Analyst | **done** | `7c489e1` |
| A2 | Analyst | **done** | `9bc66d9` |
| E1 | Expansion | **done** | `21a548b` |

**Sprint 2: 6 of 6 scoped items shipped.** Main is at `21a548b`. Live host on `:5410` confirmed running as `nscim_app` with all 5 health components green; 20/20 tests passing locally.

**Sprint 2 followups discovered during execution** (queued for Sprint 3 or beyond):

1. **Audit history-table grant gap.** A fresh-install host running `Database.Migrate()` as `nscim_app` would fail because `audit.__EFMigrationsHistory` lacks `UPDATE/DELETE` for `nscim_app` (audit's append-only posture excludes those grants). H3's one-shot `relocate-platform.sql` adds them for upgrade installs; a future migration must do the same for fresh installs. Surfaced by E1.
2. **Cross-tenant `/cases/{guid}` returns 200-with-empty-state instead of clean 404.** Functionally correct (RLS hides the foreign row → page renders not-found state), but UX-wise a 404 would be cleaner. Surfaced by E1's 1f assertion.
3. **PreRender/SourceJanitor source-blob cross-tenant sharing.** Content-addressed by SHA-256 alone; theoretically two tenants emitting byte-identical scans would share a blob and `SourceJanitorWorker` could race-evict the shared one. Astronomically unlikely in production. Surfaced by H1.
4. **Stale `__EFMigrationsHistory` row** (`20260427164643_Add_ScanRenderArtifact`) carried into per-context history table during H3's relocation. Benign (EF only fires on file-without-row). Cleanup is a one-line `DELETE`. Surfaced by H3.
5. **`dotnet ef database update` env-var passthrough**. Fails to authenticate when called from the same shell that authenticates `psql` successfully — likely a Windows shell/dotnet child-process env quirk. Workaround in use: `dotnet ef migrations script | psql -f`. Surfaced by H3.
6. **Drop `public.__EFMigrationsHistory`** from both DBs once production has flipped to per-context history without issue. Held for safety. Surfaced by H3.

### 14.7 Dispatch plan

**Wave 1** (4 parallel agents, all independent):
```bash
cd "C:\Shared\ERP V2"
git worktree add ../erp-v2-h1 -b plan/h1-worker-tenant-resolution main
git worktree add ../erp-v2-h2 -b plan/h2-identity-tenancy-interlock main
git worktree add ../erp-v2-a1 -b plan/a1-rule-evaluation-persistence main
git worktree add ../erp-v2-a2 -b plan/a2-acceptance-bars main
```

After wave 1 merges (in order: H1 → H2 → A2 → A1 to minimize CaseWorkflowService.cs conflict surface):

**Wave 2** (single agent, verify + merge an existing branch):
```bash
# H3 reuses the parked plan/f5-prod-minimum worktree
cd "C:\Shared\erp-v2-f5"   # already exists from Sprint 1
git pull origin main --rebase   # or rebase onto post-H2 main
```

After wave 2 merges:

**Wave 3** (single agent — integration gate):
```bash
git worktree add ../erp-v2-e1 -b plan/e1-multi-location-proof main
```

### 14.8 End-of-sprint smoke verification

After E1 lands:

1. Live host on `:5410` runs as `nscim_app` (under H3's appsettings).
2. `/healthz/ready` → 200.
3. Drop an FS6000 triplet → case appears within 30s with thumbnail rendered (proves H1).
4. `/cases/{id}` renders correctly under any analyst — including auth flow under `nscim_app` (proves H2).
5. Click "Fetch documents" → rules pane populates → reload page → rules pane *still* populated (proves A1).
6. `/perf` shows non-zero rolling histograms across all four meters (proves A2).
7. The federation test passes (proves E1).

### 14.9 Out of sprint (Sprint 3 candidates)

- **V4** — Analyst viewer Razor (W/L sliders, ROI inspector, pixel probe, 16-bit decode). The big UX piece. (✅ shipped in Sprint 3 at `31ba561`.)
- **G1** — Platform tightening before NickFinance.
- **G2** — NickFinance Petty Cash pathfinder.
- **P1** — Standalone operations runbooks (deploy, secret rotation, etc.).

---

## 15. Sprint 4 — Platform Tightening + Audit Grants Fix

### 15.0 Goal

Two things, bundled because they're both unblockers for what comes next:

1. **FU-1** — fix the audit history-table grants gap so a fresh-install host running `Database.Migrate()` as `nscim_app` doesn't fail. Discovered by E1; documented in §14.6 followups; ~15 min of work.
2. **G1** — platform tightening before NickFinance. Prevents foot-guns and silent-data-loss at the platform layer that NickFinance's money/decimal-shaped data would expose. ~2 days.

After this sprint, the platform is ready to host a non-Inspection consumer. **G2 (NickFinance Petty Cash) is explicitly NOT in this sprint** — its domain shape (money type, voucher entity, ledger event, approval chain, currency conversion) is a product question that needs user input before any code lands.

### 15.1 Phases

```
       ┌── FU-1 (audit grants fix, parallel-safe) ──┐
       │                                             │
       └── G1 (platform tightening, parallel-safe) ──┘──→ end-of-sprint smoke
```

Both items can dispatch in parallel — they touch entirely different files. ~2 days wall-clock with parallelism.

### 15.2 Work items

#### FU-1 — Audit `__EFMigrationsHistory` Grants for Fresh-Install Migrate()

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none (post-Sprint 2 main is fine) |
| **Parallel-safe with** | G1 |
| **Effort** | ~0.25 day |
| **Branch** | `plan/fu1-audit-history-grants` |

**Why this matters.** E1 discovered: `audit.__EFMigrationsHistory` (introduced by H3 when EF history tables were relocated out of `public`) only has SELECT+INSERT for `nscim_app` because audit's append-only posture excludes UPDATE/DELETE on the audit schema in general. EF Core's `NpgsqlHistoryRepository.AcquireDatabaseLock` runs `LOCK TABLE … ACCESS EXCLUSIVE MODE` which Postgres requires UPDATE/DELETE/TRUNCATE/MAINTAIN to grant. The H3 one-shot relocate script adds the necessary grants for upgraded installs; a fresh install (e.g. spin up a brand-new dev cluster, run `dotnet ef database update`) would fail.

**Deliverable.**

A new EF migration on `AuditDbContext` that emits SQL granting `nscim_app` UPDATE+DELETE on `audit.__EFMigrationsHistory` specifically. Mirror the per-context schema convention from H3.

```csharp
// Up
migrationBuilder.Sql(@"
  GRANT UPDATE, DELETE ON audit.""__EFMigrationsHistory"" TO nscim_app;
");

// Down
migrationBuilder.Sql(@"
  REVOKE UPDATE, DELETE ON audit.""__EFMigrationsHistory"" FROM nscim_app;
");
```

The `audit.events` table itself stays SELECT+INSERT only (append-only invariant unchanged). The grant is targeted at exactly one table — the EF bookkeeping one. Document this in the migration's class XML doc.

**Acceptance criteria.**

- Migration applied to dev `nickerp_platform`. `psql -U postgres -c "SELECT has_table_privilege('nscim_app', 'audit.\"__EFMigrationsHistory\"', 'UPDATE');"` returns `t`.
- `psql -U postgres -c "SELECT has_table_privilege('nscim_app', 'audit.events', 'UPDATE');"` returns `f` (append-only invariant intact).
- `dotnet build` 0 errors. `dotnet test` 22/22 pass.
- A simulated fresh-install path works: as `nscim_app`, run a small migration step that `LOCK TABLE … ACCESS EXCLUSIVE` on `audit.__EFMigrationsHistory` succeeds where it would have failed pre-fix.

**Out of scope.** Broader audit-schema posture redesign. The only change is one table's grants.

---

#### G1 — Platform Tightening Before NickFinance

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none |
| **Parallel-safe with** | FU-1 |
| **Effort** | ~2 days |
| **Branch** | `plan/g1-platform-tightening` |

**Why this matters.** NickFinance lands in G2. Before that, the platform has six silent foot-guns the audit (Sprint 1) flagged as "blocks Finance" or "would be nice." Most carry a corruption-or-collision risk that's invisible until Finance actually exercises the platform with money-shaped data.

**Deliverable.** Six surgical platform fixes, in order of "blocks Finance" → "nice-to-have":

1. **Decimal-as-string canonical `JsonSerializerOptions` for `DomainEvent` payloads.** Today `DomainEvent.Payload` is opaque `JsonElement`. If a publisher round-trips through `System.Text.Json` defaults, `decimal` becomes `double` and you lose cents. Add a public static `JsonSerializerOptions DomainEventOptions` on the `NickERP.Platform.Audit` package that enforces `decimal`-as-string + invariant culture. Document it as the canonical payload writer.

2. **Audit channel routing accepts globs / multi-segment prefixes.** `DbEventPublisher.ChannelFor` (in `NickERP.Platform.Audit.Database`) currently takes only the first dotted segment, so every event publishes to channel `"nickerp"`. Finance subscribers wanting only `nickerp.finance.*` get every Inspection event and have to filter in-process. Change to accept the second segment (`finance` / `inspection`) OR accept a glob in `Subscribe`.

3. **`ITenantContext.SetSystemContext()` for cross-tenant system jobs.** Today `ITenantContext.SetTenant` rejects tenantId ≤ 0. Finance needs a system context for parent-tenant consolidated reporting + nightly FX-rate jobs. Add a sanctioned `SetSystemContext()` method that internally sets `app.tenant_id = '0'` (the same fail-closed sentinel RLS uses) AND a paired bypass policy on tables that legitimately need cross-tenant reads. Or simpler: the system context is a flag `IsSystem` on `ITenantContext`; `TenantConnectionInterceptor` writes a sentinel value when `IsSystem == true`; specific RLS policies that allow system reads use a `OR current_setting('app.tenant_id') = '-1'` clause.

   **STRATEGIC NOTE FOR THE EXECUTING AGENT:** This one is a posture-shift on tenant isolation. Before implementing, write a short proposal to `docs/master-decisions-needed.md` describing the chosen mechanism + its blast radius (which RLS policies it relaxes, on which tables). Halt + ask for sign-off. Don't ship without confirmation. (This is a hard-stop class — broadens the security model.)

4. **`DomainEvent.TenantId` nullable** on the entity + the `audit.events` table. Finance has events without a single owning tenant ("FX rate published for the suite", "GL chart of accounts updated globally"). Either make `TenantId` nullable (preferred — semantically right), or use the `0` sentinel with index support for `IS NULL OR = ?`. Pick nullable. Add a migration on `nickerp_platform.audit.events` to drop `NOT NULL` on `TenantId` + add a partial index for the system-event case.

5. **Plugin `Module` / `Namespace` partitioning.** `IPluginRegistry` indexes by global `TypeCode`. Finance adding a `momo` plugin would silently collide with a hypothetical Inspection `momo` plugin. Add a required `Module` field to the `[Plugin]` attribute + `PluginManifest` (e.g., `[Plugin("momo", Module = "finance")]`); key the registry on `(module, typeCode)`. Bump every existing plugin's manifest to declare its module (`"inspection"` for all of today's plugins). Bump `[ContractVersion]` on the Plugins package since this is an additive contract change. The host's `IPluginRegistry.Resolve<T>(string typeCode)` should grow a `string module` parameter; existing call sites pass `"inspection"`.

6. **Scope-claim regex enforcement.** `IdentityAdminEndpoints`'s `MapScopes` POST currently accepts any non-empty string as a scope code. Finance scope `Finance.PettyCash.Approver` and Inspection scope `Identity.Admin` would coexist OK, but a maliciously-named scope like `"admin"` (no namespace) would also be accepted. Add a regex check at the API boundary requiring `^[A-Z][A-Za-z]+(\.[A-Z][A-Za-z]+)+$` with a clear error message. Document the per-app prefix as a hard rule in `IDENTITY.md`.

**Files in scope.** Roughly:

- `platform/NickERP.Platform.Audit/` — JsonSerializerOptions, DomainEvent shape change.
- `platform/NickERP.Platform.Audit.Database/` — DbEventPublisher.ChannelFor, migration for events.TenantId nullable.
- `platform/NickERP.Platform.Tenancy/` — ITenantContext.SetSystemContext + TenantConnectionInterceptor change.
- (For #3) — possibly RLS policy migrations across multiple DBs (BUT see strategic note: gate on user sign-off).
- `platform/NickERP.Platform.Plugins/` — PluginAttribute + PluginManifest + PluginLoader + PluginRegistry — `Module` field, key change.
- All plugin.json files in `modules/inspection/plugins/*` — declare `module: "inspection"`.
- `platform/NickERP.Platform.Identity.Api/` — scope regex enforcement.
- `IDENTITY.md` if it exists, otherwise add a section to `ARCHITECTURE.md` §7.x.

**Acceptance criteria.**

- Decimals round-trip through `DomainEvent` payloads losslessly. New unit test in `tests/NickERP.Platform.Tests/` (create the project if it doesn't exist) that publishes an event with a payload containing `decimal.MaxValue / 7m`, reads it back, asserts byte-identical.
- Channel routing test: subscribe to `nickerp.finance.*`, publish a `nickerp.inspection.case_opened`, assert NOT received. Publish a `nickerp.finance.transaction_recorded`, assert received.
- (#3 only after sign-off) System-context test: `ITenantContext.SetSystemContext()` followed by a query on a sanctioned cross-tenant table returns rows from multiple tenants.
- `audit.events` accepts an INSERT with `TenantId` NULL.
- Plugin namespace test: register two plugins with the same `TypeCode` but different `Module`; both load, `Resolve("finance", "x")` and `Resolve("inspection", "x")` return distinct instances.
- Scope regex test: POST `/api/identity/scopes` with `"admin"` returns 400; with `"Finance.PettyCash.Approver"` returns 200.
- `dotnet build` 0 errors. `dotnet test` all tests pass (existing 22 + the new G1 tests).

**Out of scope.** NickFinance domain model. Finance-specific endpoints. Anything in `modules/finance/` (doesn't exist; G2 creates it).

### 15.3 Status snapshot

| ID | Phase | Status | Branch | Merge commit |
|---|---|---|---|---|
| FU-1 | Followup | shipped 2026-04-26 | `plan/fu1-audit-history-grants` (deleted) | `5854c81` |
| G1 | Generalization | shipped 2026-04-26 (items #1, #2, #4, #5, #6) | `plan/g1-platform-tightening` (deleted) | `545a0f9` |

G1 sub-item #3 (`ITenantContext.SetSystemContext`) was held — see
`docs/master-decisions-needed.md`. The four options + master
recommendation are written up there; G2 (NickFinance) cannot start
until the user picks a mechanism.

### 15.4 End-of-sprint smoke verification

After both merge:

1. Live host on `:5410` boots clean as `nscim_app`.
2. Decimals in `DomainEvent` payloads round-trip losslessly (by inspection of the new test).
3. Audit channel subscribers see only their module's events.
4. System context (post sign-off) reads across tenants where allowed.
5. Plugins still load (5 of them); `Resolve` calls still work; new module-aware `Resolve` works.
6. Scope-create regex rejects underspecified scopes.
7. Fresh-install `nscim_app` `Database.Migrate()` against a wiped audit schema completes (FU-1).

### 15.5 Out of sprint (Sprint 5+ candidates)

- **G2** — NickFinance Petty Cash pathfinder. **Hard-stopped pending domain shape**: money type, voucher lifecycle, custodian/approver workflow, journal-event types, period-lock semantics, currency conversion contract. Master agent must surface this as a question to the user before any G2 code lands.
- **P1** — Operations runbooks.
- **P2** — Edge node.
- **P3** — Audit projection + notifications.
- **FU-2 to FU-7** — small followups from Sprint 1+2 retrospectives.
- **IMAGE-ANALYSIS-MODERNIZATION track** — own sprint family per `docs/IMAGE-ANALYSIS-MODERNIZATION.md`.

## 16. Sprint 5 — System Context Mechanism (G1-3)

### 16.0 Goal

Land the `SetSystemContext()` mechanism on `ITenantContext` so cross-tenant
system jobs (FX-rate publication, suite-wide GL updates, parent-tenant
consolidated reporting) have a sanctioned API instead of needing a BYPASSRLS
role. Sprint 4's G1 #3 was held pending a security-posture decision; the user
picked **option 2** (explicit `IsSystem` flag + sentinel `-1` + per-table RLS
opt-in clauses) on 2026-04-28. This sprint implements that choice and ships
the first opt-in clause on `audit.events` so G1 #4's NULL-tenant write path is
exercisable from `nscim_app` (today it only works under `postgres`/BYPASSRLS).

This sprint is **single-focus** on purpose. The change touches the platform
tenancy contract, an interceptor on the connection-open path, and an RLS
policy migration. All three are security-load-bearing; bundling unrelated work
would muddy the review surface.

After Sprint 5, G2 (NickFinance) is unblocked on its **system-context dependency**
but still gated on the user's domain-shape product calls.

### 16.1 Phases

```
       ┌── G1-3 (system context, single agent) ──┐
       │                                          │
       └── end-of-sprint smoke ──────────────────┘
```

Single work item, single agent. ~0.5 day wall-clock.

### 16.2 Work items

#### G1-3 — `ITenantContext.SetSystemContext()` + `audit.events` opt-in

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none (Sprint 4's G1 #1, #2, #4, #5, #6 already in main) |
| **Parallel-safe with** | (none — single-item sprint) |
| **Effort** | ~0.5 day |
| **Branch** | `plan/g1-3-system-context` |

**Why this matters.** `ITenantContext.SetTenant` rejects `tenantId <= 0`; every
RLS policy enforces `"TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint`.
Together these mean a Finance suite-wide event ("FX rate published",
`TenantId=NULL`) cannot be inserted from production code running as
`nscim_app` — the interceptor pushes `'0'`, the RLS policy's `WITH CHECK`
sees `NULL = 0` (which is NULL, which is false), insert fails. G1 #4 dropped
the `NOT NULL` constraint on `audit.events.TenantId` and added the partial
index, but verified the AC under `postgres` (BYPASSRLS) only. This sprint
closes that gap.

**Deliverable.**

1. **`ITenantContext` contract change.** Add to `platform/NickERP.Platform.Tenancy/ITenantContext.cs`:

   ```csharp
   /// <summary><c>true</c> when SetSystemContext() has been called; false in tenant-scoped requests.</summary>
   bool IsSystem { get; }

   /// <summary>
   /// Switch this context into system mode. The connection interceptor will
   /// push the sentinel app.tenant_id = '-1' on open; RLS policies that opt in
   /// to system access (via OR current_setting('app.tenant_id') = '-1') will
   /// allow cross-tenant reads / NULL-tenant writes. After SetSystemContext(),
   /// IsResolved is true and TenantId is -1.
   /// </summary>
   /// <remarks>
   /// Every call site MUST be registered in docs/system-context-audit-register.md.
   /// Reviewed at every sprint boundary.
   /// </remarks>
   void SetSystemContext();
   ```

   Update `TenantContext`'s impl:
   - Add private `bool _isSystem` and public `IsSystem => _isSystem`.
   - `SetSystemContext()` sets `_isSystem = true`, `TenantId = -1L`, `IsResolved = true`.
   - `SetTenant(long)` clears `_isSystem` (back to tenant-scoped) — so a single
     scoped context that calls `SetSystemContext()` then later `SetTenant(2)`
     ends up in tenant 2's regular scope. Document this in XML doc-comment.

2. **`TenantConnectionInterceptor`** — change `ResolvedId()` to return `-1L`
   when `_tenant.IsSystem`, otherwise the existing branch. The SQL surface
   stays `SET app.tenant_id = '<n>'` — only the value differs.

3. **`TenantOwnedEntityInterceptor`** — when `_tenant.IsSystem`, allow
   `TenantId IS NULL` writes by **leaving entity.TenantId alone** (don't stamp
   `-1` onto entities — `-1` is a session-context sentinel, not a row-data
   value). When **not** in system mode the existing `IsResolved=false` throw
   stands. Modules wanting to insert a system-owned row pass an
   `ITenantOwned` with `TenantId = 0L` (the "not yet stamped" sentinel) — the
   interceptor stamps tenant id from context as before, **except** when
   `IsSystem` AND the entity's `TenantId == 0L` it leaves it as `0L`. (Note
   that `0L` is the in-memory representation of NULL pre-stamp; for `audit.events`
   the schema column is now nullable so `EF` will translate it.)

   **Important nuance:** the audit `DomainEvent.TenantId` is `long?` (nullable)
   per Sprint 4 G1 #4's migration. The TenantOwnedEntityInterceptor branch
   should only act when an entity implements `ITenantOwned` (which has a
   non-nullable `long TenantId`). Suite-wide audit events that need NULL
   tenancy use `DomainEvent` directly and don't implement `ITenantOwned` —
   they bypass this interceptor. Verify this is the case (it should be,
   per the existing audit module shape); document the invariant in an XML
   comment on `TenantOwnedEntityInterceptor.StampTenant`.

4. **RLS opt-in migration on `audit.events`.** New migration on
   `AuditDbContext`: `AddSystemContextOptInToEvents`. Drop the existing
   `tenant_isolation_events` policy and recreate it as:

   ```sql
   DROP POLICY IF EXISTS tenant_isolation_events ON audit.events;
   CREATE POLICY tenant_isolation_events ON audit.events
     USING (
       "TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint
       OR (current_setting('app.tenant_id', true) = '-1' AND "TenantId" IS NULL)
     )
     WITH CHECK (
       "TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint
       OR (current_setting('app.tenant_id', true) = '-1' AND "TenantId" IS NULL)
     );
   ```

   The Down reverts to Sprint 1 / F1's plain shape (no `OR` clause). This is
   `audit.events` ONLY — do not touch any other table's RLS in this migration.
   Adding system-context opt-in to other tables is out of scope (each future
   table opts in via its own targeted migration with its own justification).

5. **New doc** `docs/system-context-audit-register.md`. Format:

   ```markdown
   # System-Context Audit Register

   Append-only register of every code path that calls
   `ITenantContext.SetSystemContext()`. Reviewed at every sprint boundary by
   the rolling master and at every security review by the user.

   ## Format

   | Caller | File:Line | Why | RLS opt-in clauses needed | Date | Sprint |
   |---|---|---|---|---|---|

   ## Entries

   _(none — Sprint 5 ships the mechanism; the first caller lands in G2 / NickFinance.)_

   ## Tables that opt in to system context

   | Table | Migration | Sprint | Rationale |
   |---|---|---|---|
   | `audit.events` | `<timestamp>_AddSystemContextOptInToEvents` | Sprint 5 | Suite-wide events (FX rate, GL chart-of-accounts) need NULL-tenant inserts; G1 #4 dropped NOT NULL but the RLS policy blocked the write. |

   ## Review checklist

   At every sprint boundary, the master coordinator confirms:

   - Every entry in "Entries" still corresponds to live code (no dead callers).
   - Every entry in "Tables that opt in" still has its `OR ... = '-1'` clause
     intact (run `psql -c "\d+ audit.events"` and inspect the policy).
   - No new `SetSystemContext()` callers exist that aren't in this register
     (`grep -r "SetSystemContext" --include='*.cs'`).
   - No table outside the "Tables that opt in" list has the `'-1'` clause
     (this would be a silent posture broadening). Run a `pg_policies` audit:
     `SELECT schemaname, tablename, policyname FROM pg_policies WHERE qual LIKE '%''-1''%' OR with_check LIKE '%''-1''%';`.
   ```

6. **Tests** in `tests/NickERP.Platform.Tests/`. Three new tests in a new file
   `SystemContextTests.cs` (or wherever G1's existing tenancy tests live —
   if a `TenancyInterceptorTests.cs` exists, add to it):

   - `SystemContext_AllowsNullTenantInsert_ToAuditEvents` — open a connection
     under `nscim_app`, call `SetSystemContext()`, INSERT into `audit.events`
     with `TenantId=NULL`. Assert success. Read back and assert the row is
     visible (still under system context).
   - `SystemContext_DoesNotLeakReads_ForNonOptedInTables` — under
     `nscim_app`, `SetSystemContext()` then `SELECT count(*) FROM
     inspection.locations`. Assert count is **0** (the table has not opted
     in; the system context's `'-1'` does not match any tenant id).
   - `WithoutSystemContext_RejectsNullTenantInsert_ToAuditEvents` — under
     `nscim_app`, regular `SetTenant(1)`, attempt to INSERT into
     `audit.events` with `TenantId=NULL`. Assert it raises a Postgres RLS
     violation (or whatever EF's wrapped exception is). Existing
     F1+G1#4 invariant — this is the regression assertion for the new
     migration not weakening it.

   These need a real Postgres connection. Look for an existing test fixture
   in `tests/NickERP.Platform.Tests/` that already opens a real DB; reuse
   it. If none exists, the simplest addition is xUnit `IClassFixture` that
   spins up a connection to `nickerp_platform` using
   `Username=nscim_app;Password=$NICKSCAN_DB_PASSWORD` from env. Use
   `ConnectionStrings__Platform` if it's already set in CI; otherwise read
   `NICKSCAN_DB_PASSWORD` from env (Sprint 2 / H3 had a similar pattern —
   look there for the pattern to copy).

7. **Status update on `docs/master-decisions-needed.md`.** At the bottom of
   the G1-3 entry, add a new line:

   ```
   **Implementation shipped 2026-04-28** — Sprint 5, commit `<sha-after-merge>`.
   See `docs/system-context-audit-register.md` for the audit register that
   tracks every `SetSystemContext()` caller going forward.
   ```

   And in `docs/sprint-progress.json`, leave the existing `resolvedDecisions`
   entry but add an `implementedAt: "2026-04-28"` field and `implementationCommit: "<sha>"`.

**Files in scope.**
- `platform/NickERP.Platform.Tenancy/ITenantContext.cs` (interface + impl)
- `platform/NickERP.Platform.Tenancy/TenantConnectionInterceptor.cs` (sentinel branch)
- `platform/NickERP.Platform.Tenancy/TenantOwnedEntityInterceptor.cs` (system-mode branch)
- `platform/NickERP.Platform.Audit.Database/Migrations/<timestamp>_AddSystemContextOptInToEvents.cs` (new)
- `platform/NickERP.Platform.Audit.Database/Migrations/AuditDbContextModelSnapshot.cs` (no model change — only an RLS policy change which doesn't appear in the snapshot, but the migration must still be the latest one)
- `docs/system-context-audit-register.md` (new)
- `docs/master-decisions-needed.md` (status note appended)
- `tests/NickERP.Platform.Tests/SystemContextTests.cs` (new) — or appended to existing tenancy test file

**Acceptance criteria.**
- `dotnet build` 0 errors, 0 new warnings.
- `dotnet test` — all existing tests still pass; the three new
  SystemContext tests pass.
- Migration applied to dev `nickerp_platform.audit`. `psql -U postgres -d
  nickerp_platform -c "\d+ audit.events"` shows the policy with the
  `OR ... = '-1'` clause.
- Audit register file exists and includes the `audit.events` table entry.
- Master decisions doc has the implementation-shipped note.
- A manual probe under `nscim_app`:
  ```bash
  PGPASSWORD=$NICKSCAN_DB_PASSWORD psql -U nscim_app -d nickerp_platform \
    -c "SET app.tenant_id = '-1';" \
    -c "INSERT INTO audit.events (\"Id\", \"OccurredAt\", \"EventType\", \"TenantId\", \"Source\", \"Payload\") VALUES (gen_random_uuid(), now(), 'sprint5.smoke', NULL, 'manual-test', '{}'::jsonb);" \
    -c "SELECT count(*) FROM audit.events WHERE \"EventType\" = 'sprint5.smoke';"
  ```
  succeeds and returns count = 1.

**Out of scope (hard stops).**
- Adding the `'-1'` clause to **any other RLS policy**. `audit.events` is the
  only opt-in table in this sprint. Future tables opt in one-at-a-time with
  their own justification. (Per the master prompt: "blanket policy change
  across multiple tables is a halt.")
- Any change to the `nscim_app` role's grants, or any new role.
- Removing or modifying `nscim_app`'s NOBYPASSRLS posture.
- Anything in `modules/finance/` (G2 territory; gated on product calls).

### 16.3 Status snapshot

| ID | Phase | Status | Branch | Merge commit |
|---|---|---|---|---|
| G1-3 | Generalization | pending | `plan/g1-3-system-context` | _(filled at merge)_ |

### 16.4 End-of-sprint smoke verification

After merge:

1. Live host on `:5410` boots clean as `nscim_app`. `/healthz/ready` returns 200.
2. Existing tenant-scoped behaviour is unchanged: a request authenticating as
   tenant 1 sees only tenant 1's data; an anonymous request sees nothing.
3. The manual `audit.events` system-context probe (above) succeeds.
4. `pg_policies` audit query (above) shows exactly one row: `audit / events
   / tenant_isolation_events`. No leakage to other tables.
5. `docs/system-context-audit-register.md` lists `audit.events` and zero
   callers (correct for now — first caller lands in G2).

### 16.5 Out of sprint

- Adding system-context opt-in to other tables — future sprints, on demand.
- The first actual `SetSystemContext()` caller (G2 / NickFinance / FX-rate
  worker) — domain-gated.

## 17. Sprint 6 — Followup Sweep (FU-2 .. FU-7)

### 17.0 Goal

Drain the small-followups queue from Sprint 1+2 retrospectives. Six items,
mostly orthogonal — five touch entirely different files, one is a doc-only
note. Bundling them into one sprint keeps overhead low: one PLAN section,
one set of agent dispatches, one merge cycle, one smoke run.

After Sprint 6, the followup queue (FU-1 already shipped in Sprint 4) is
empty.

### 17.1 Phases

```
       ┌── FU-2 (404 instead of empty case page) ──┐
       │                                            │
       ├── FU-3 (source-blob collision defense doc) ┤
       │                                            │
       ├── FU-4 (stale __EFMigrationsHistory row) ──┤── all parallel-safe
       │                                            │
       ├── FU-5 (dotnet ef env-var doc)  ──────────┤
       │                                            │
       ├── FU-6 (drop public.__EFMigrationsHistory) ┤
       │                                            │
       └── FU-7 (AuthorityDocument rename) ─────────┘
                              │
                              ▼
                       end-of-sprint smoke
```

Six work items, six parallel agents. ~1 day wall-clock with parallelism;
expect FU-7 to be the longest (touches consumers of the colliding type).

### 17.2 Work items

#### FU-2 — `/cases/{guid}` Foreign-Tenant Returns 404, Not 200-with-Empty-State

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none |
| **Parallel-safe with** | FU-3, FU-4, FU-5, FU-6, FU-7 |
| **Effort** | ~0.25 day |
| **Branch** | `plan/fu2-foreign-case-404` |

**Why this matters.** E1 surfaced this in its 1f assertion: a user
authenticated as tenant A who visits `/cases/<tenant-B-case-guid>` gets the
empty "no case found" state because RLS hides the row. Functionally fine,
UX-wise a clean 404 (or 403) is what users expect — both for browser back
behaviour and for any future API consumers that branch on status code.

**Deliverable.**

The page is `modules/inspection/src/NickERP.Inspection.Web/Components/Pages/CaseDetail.razor`
(`@page "/cases/{CaseId:guid}"`). Today, when the EF query returns null, the
page renders a `_case == null` empty state. Change to: when null, set a
404 status code on the response and render a "Case not found" page that
matches the rest of the app's not-found styling.

In Blazor SSR / interactive-server, the way to do this is via
`HttpContext.Response.StatusCode = 404` from a `[CascadingParameter] HttpContext`
or `IHttpContextAccessor`, set inside `OnInitializedAsync` before the first
render of the empty-state branch. Alternatively (cleaner), add a route
guard: navigate to a dedicated `NotFound.razor` (`@page "/not-found"`) and
return a 404 from the request that brought you there.

Whichever shape is cleaner — choose one and implement it. Document the
choice in a code comment. The functional acceptance is the same.

**Files in scope.**
- `modules/inspection/src/NickERP.Inspection.Web/Components/Pages/CaseDetail.razor`
- Possibly a new `NotFound.razor` page if the cleaner shape needs it
- `tests/NickERP.Inspection.E2E.Tests/` — add an integration test that asserts
  404 on a foreign-tenant case (mirror E1's existing 1f assertion which
  asserted 200; flip it).

**Acceptance criteria.**
- Authenticated as tenant A, GET `/cases/<tenant-B-case-guid>` returns
  HTTP 404.
- Authenticated as tenant A, GET `/cases/<tenant-A-case-guid>` returns
  HTTP 200 with the case detail (existing behaviour preserved).
- Anonymous GET still redirects to login (existing auth behaviour
  preserved).
- E2E test passes; `dotnet build` 0 errors; `dotnet test` all pass.

**Out of scope.** Any other empty-state pages (ScanViewer, NewCase, etc.).
This is `CaseDetail.razor` only.

---

#### FU-3 — Source-Blob Cross-Tenant SHA-256 Collision: Defense Doc

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none |
| **Parallel-safe with** | FU-2, FU-4, FU-5, FU-6, FU-7 |
| **Effort** | ~0.25 day |
| **Branch** | `plan/fu3-source-blob-collision-doc` |

**Why this matters.** `DiskImageStore` is content-addressed by SHA-256 alone
(`source/{hash[0..2]}/{hash}.{ext}`). Two tenants emitting byte-identical
scans (cosmically unlikely, but) would share a blob; `SourceJanitorWorker`'s
deletion logic could race-evict the shared blob if one tenant's case lifecycle
moves it past retention while the other tenant still references it. H1 surfaced
this; its severity is "astronomically unlikely" — but defending in depth is
cheap.

**Deliverable.**

Two parts:

1. **Documentation.** Append a section to `docs/ARCHITECTURE.md` (or a new
   `docs/IMAGE-STORAGE.md` if it doesn't exist) describing:
   - The current content-addressed scheme and its blast radius (theoretical
     cross-tenant blob sharing).
   - Why it's astronomically unlikely (SHA-256 collision space, tenant
     scan-byte uniqueness via timestamp / location / scanner serial).
   - The defense-in-depth posture: `ScanArtifact` rows are tenant-scoped,
     RLS hides foreign rows, only **storage** is shared; an attacker cannot
     read another tenant's data via this path because they cannot enumerate
     blob paths without reading rows.
   - The race-eviction note: `SourceJanitorWorker` deletes a blob only when
     its referencing rows are all past retention; a future hardening (this
     followup's part 2) adds a cross-tenant-aware check.

2. **Low-priority guard.** In `SourceJanitorWorker`, change the discovery
   query from "this tenant's `ScanArtifact` rows past retention" to "this
   tenant's `ScanArtifact` rows past retention WHERE the
   content-hash is not referenced by any other tenant's `ScanArtifact`."
   The cross-tenant existence check needs a system-context query (or the
   worker can raise `BYPASSRLS`-equivalent via `SetSystemContext()` for the
   single existence-check query — but this is **out of scope** for this
   followup since the system-context mechanism just landed in Sprint 5 and
   it needs a paired RLS opt-in clause on `inspection.scan_artifacts` which
   broadens posture). Instead, ship the guard **behind a feature flag**
   `Imaging:SourceJanitor:EnforceCrossTenantBlobGuard` (default `false`).
   When `false`, today's behaviour is unchanged. When `true`, the worker
   refuses to evict a blob whose hash appears in `scan_artifacts` rows of
   any other tenant — implemented via a raw SQL query bypassing the
   per-tenant filter (use the same DbContext, a `FromSqlRaw` query that
   intentionally short-circuits RLS via a sub-query under
   `current_user`-aware logic, OR — simpler — defer enforcement and just
   document the flag as "future hardening").

   **Pragmatic approach:** ship part 1 (doc) plus part 2 as **just the flag
   declaration in `ImagingOptions` with a comment** ("not yet enforced;
   tracked in FU-3 / future sprint"). Do not implement the cross-tenant
   query in this followup — it needs Sprint 5's system-context mechanism
   plus a new RLS opt-in clause, which together exceed this followup's
   "low-priority" budget. The implementation is itself a future sprint
   item.

**Files in scope.**
- `docs/ARCHITECTURE.md` (new section) OR `docs/IMAGE-STORAGE.md` (new file)
- `modules/inspection/src/NickERP.Inspection.Imaging/ImagingOptions.cs`
  (new option declaration with comment, default `false`)

**Acceptance criteria.**
- Documentation section is present and reviewed for accuracy by reading
  the actual `DiskImageStore` and `SourceJanitorWorker` code.
- `ImagingOptions.EnforceCrossTenantBlobGuard` exists, defaults to `false`,
  has an XML doc-comment that explains it's a future enforcement hook.
- `dotnet build` 0 errors; existing tests still pass.

**Out of scope.** Implementing the cross-tenant guard. That's a future sprint
item, dependent on Sprint 5's system-context mechanism plus an
`inspection.scan_artifacts` RLS opt-in clause.

---

#### FU-4 — Stale `__EFMigrationsHistory` Row Cleanup

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none |
| **Parallel-safe with** | FU-2, FU-3, FU-5, FU-6, FU-7 |
| **Effort** | ~0.1 day |
| **Branch** | `plan/fu4-stale-migrations-history-row` |

**Why this matters.** During H3's relocation of EF history out of `public`,
the old row `20260427164643_Add_ScanRenderArtifact` was carried into
`inspection.__EFMigrationsHistory` alongside the live migration
`20260427164855_Add_ScanRenderArtifact`. Benign — EF only re-applies
migrations it can't find a row for — but it confuses `dotnet ef migrations
list` output and is just untidy.

Confirmed present in `nickerp_inspection.public."__EFMigrationsHistory"`
today (psql query 2026-04-28). The orphan row needs to come out of the
**per-context** history table (the live one in
`inspection.__EFMigrationsHistory`); the `public` copy is being dropped in
FU-6.

**Deliverable.**

A new EF migration on `InspectionDbContext` named `Cleanup_StaleScanRenderArtifactHistoryRow`
(or similar). The migration's Up does:

```csharp
migrationBuilder.Sql(@"
  DELETE FROM inspection.""__EFMigrationsHistory""
  WHERE ""MigrationId"" = '20260427164643_Add_ScanRenderArtifact';
");
```

Down is a no-op (or re-INSERT with the matching ProductVersion if you want
true reversibility — `Down` on a cleanup migration is "irreversible" and
that's documented in the migration's XML doc).

Verify after applying that `SELECT "MigrationId" FROM
inspection."__EFMigrationsHistory" ORDER BY 1` shows exactly seven rows
(the original 8 minus the stale one).

**Files in scope.**
- `modules/inspection/src/NickERP.Inspection.Database/Migrations/<timestamp>_Cleanup_StaleScanRenderArtifactHistoryRow.cs` (new)

**Acceptance criteria.**
- `dotnet build` 0 errors.
- After applying, `SELECT count(*) FROM inspection."__EFMigrationsHistory"
  WHERE "MigrationId" = '20260427164643_Add_ScanRenderArtifact'` returns 0.
- Other tests continue to pass.
- Live host on `:5410` still boots clean.

**Out of scope.** Anything else about the public `__EFMigrationsHistory` table
(that's FU-6).

---

#### FU-5 — Document `dotnet ef database update` Env-Var Quirk on Windows

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none |
| **Parallel-safe with** | FU-2, FU-3, FU-4, FU-6, FU-7 |
| **Effort** | ~0.25 day |
| **Branch** | `plan/fu5-dotnet-ef-env-var-doc` |

**Why this matters.** H3 surfaced that `dotnet ef database update` fails to
authenticate when called from the same shell that authenticates `psql`
successfully — likely a Windows shell-to-dotnet child-process env-var
passthrough quirk. The workaround used by H3 is `dotnet ef migrations
script | psql -f`. This needs to be in a runbook so future maintainers don't
re-discover it.

**Deliverable.**

A new section in `docs/RUNBOOK.md` (or in `docs/MIGRATIONS.md` if it exists,
or a new `docs/MIGRATIONS.md` if not) titled "Applying migrations on
Windows under nscim_app" documenting:

1. The symptom: `dotnet ef database update` returns "password authentication
   failed for user 'nscim_app'" even though `psql -U nscim_app` with the
   same `PGPASSWORD` succeeds in the same shell.
2. The hypothesis: dotnet's child-process spawn on Windows isn't inheriting
   `NICKSCAN_DB_PASSWORD` (or `PGPASSWORD`, or whichever the connection
   string interpolates) cleanly. Tested workarounds:
   - Inline the password into a `dotnet ef`-local connection string —
     fragile, leaks into shell history.
   - **Recommended:** `dotnet ef migrations script <PreviousMigration>
     <TargetMigration> --idempotent --output migration.sql` then
     `psql -U nscim_app -d <db> -f migration.sql`. The `psql` invocation
     reads `PGPASSWORD` correctly. Idempotent script means running
     against a partially-applied DB is safe.
3. A concrete worked example, e.g.:

   ```bash
   cd "C:\Shared\ERP V2\platform\NickERP.Platform.Audit.Database"
   dotnet ef migrations script <previous-id> AddSystemContextOptInToEvents \
     --idempotent --output /tmp/migrate.sql --context AuditDbContext
   PGPASSWORD=$NICKSCAN_DB_PASSWORD '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
     -U nscim_app -d nickerp_platform -f /tmp/migrate.sql
   ```

4. A note that running migrations as `postgres` (via a separate
   `Username=postgres;...` connection string) also works for dev cycles
   and avoids the quirk entirely. This is the recommended dev workflow;
   the script-and-pipe is for the production-like `nscim_app` path.

**Files in scope.**
- `docs/RUNBOOK.md` (append a section) OR `docs/MIGRATIONS.md` (new file)

**Acceptance criteria.**
- Doc section exists and is reviewed for accuracy.
- A reader unfamiliar with the codebase can follow the worked example
  and apply a migration as `nscim_app` without re-discovering the quirk.

**Out of scope.** Actually fixing the quirk (its root cause is in
`dotnet ef`'s shell-to-child env-var handling on Windows; out of our
control).

---

#### FU-6 — Drop `public.__EFMigrationsHistory` from Both DBs

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none (per-context history is in production for both DBs since H3) |
| **Parallel-safe with** | FU-2, FU-3, FU-4, FU-5, FU-7 |
| **Effort** | ~0.1 day |
| **Branch** | `plan/fu6-drop-public-migrations-history` |

**Why this matters.** H3 relocated EF migration history from `public` to
per-context schemas (`audit.__EFMigrationsHistory`,
`inspection.__EFMigrationsHistory`, etc.). The `public.__EFMigrationsHistory`
copy was kept around for safety during the cutover. Per the master prompt,
production has flipped successfully — safe to drop.

**Deliverable.**

A small SQL script + runbook entry. The cleanest shape: two new EF
migrations (one per DbContext that has a `public.__EFMigrationsHistory`
remnant), each with:

```csharp
// Up
migrationBuilder.Sql(@"DROP TABLE IF EXISTS public.""__EFMigrationsHistory"";");
// Down (irreversible; document)
migrationBuilder.Sql(@"-- intentionally no-op; pre-H3 history is gone");
```

But check first: do all the per-context contexts (Audit, Identity, Tenancy,
Inspection) actually share `public.__EFMigrationsHistory` in both DBs, or
just one of them? Inspection has a remnant per H3's notes; the platform
contexts may or may not. Run:

```bash
psql -U postgres -d nickerp_platform -c "\dt public.__EFMigrationsHistory"
psql -U postgres -d nickerp_inspection -c "\dt public.__EFMigrationsHistory"
```

If the table exists in either DB, ship a migration on the appropriate
context to drop it. If it doesn't exist in a given DB (e.g. fresh installs
post-H3 never had it), no migration needed there.

**Files in scope.**
- Possibly `platform/NickERP.Platform.<context>.Database/Migrations/<timestamp>_Drop_PublicEFMigrationsHistory.cs`
- Possibly `modules/inspection/src/NickERP.Inspection.Database/Migrations/<timestamp>_Drop_PublicEFMigrationsHistory.cs`
- `docs/RUNBOOK.md` (note documenting the cleanup for ops awareness)

**Acceptance criteria.**
- After applying, `\dt public.__EFMigrationsHistory` returns "Did not find
  any relation" in both `nickerp_platform` and `nickerp_inspection`.
- `dotnet build` 0 errors; `dotnet test` all pass; live host still boots.
- Per-context history tables are unaffected (verify
  `inspection.__EFMigrationsHistory` and `audit.__EFMigrationsHistory`
  still exist and have all expected rows).

**Out of scope.** Any other cleanup of remnants from H3's relocation. Just
the public history table.

---

#### FU-7 — `AuthorityDocument` Namespace Collision Rename

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none |
| **Parallel-safe with** | FU-2, FU-3, FU-4, FU-5, FU-6 (touches different files) |
| **Effort** | ~0.5 day |
| **Branch** | `plan/fu7-authoritydocument-rename` |

**Why this matters.** Two distinct types share the name `AuthorityDocument`:

1. `NickERP.Inspection.Core.Entities.AuthorityDocument` — the persistence
   entity, joined to `InspectionCase` and `ExternalSystemInstance`.
2. `NickERP.Inspection.ExternalSystems.Abstractions.AuthorityDocument` —
   the DTO record returned by adapter `FetchDocumentsAsync`.

Code that needs both has to alias one. Code that imports both namespaces
hits a CS0104 ambiguous-reference error. Surfaced by A1.

**Deliverable.**

Rename the **DTO** (the `record` in `ExternalSystems.Abstractions`) to
`AuthorityDocumentDto`. The persistence entity keeps the cleaner name
(it's the long-lived domain noun; the DTO is plumbing).

Rename touches:

1. `modules/inspection/src/NickERP.Inspection.ExternalSystems.Abstractions/IExternalSystemAdapter.cs` — rename the `record` declaration.
2. Every consumer of the DTO. Likely sites:
   - `modules/inspection/plugins/NickERP.Inspection.ExternalSystems.IcumsGh/IcumsGhAdapter.cs`
   - `modules/inspection/plugins/NickERP.Inspection.ExternalSystems.Mock/MockExternalSystemAdapter.cs`
   - `modules/inspection/src/NickERP.Inspection.Web/Services/CaseWorkflowService.cs`
   - Any tests in `tests/NickERP.Inspection.*.Tests/`

   Run a `grep -rn "AuthorityDocument" --include="*.cs"` and audit each hit:
   if the reference is in `ExternalSystems.Abstractions` namespace context
   (or imports it without aliasing), update to `AuthorityDocumentDto`.

3. Document the rename in a code comment on each renamed type, e.g.
   `/// <summary>DTO returned by IExternalSystemAdapter.FetchDocumentsAsync. Renamed from AuthorityDocument in FU-7 to disambiguate from the persistence entity.</summary>`

**Files in scope.**
- `modules/inspection/src/NickERP.Inspection.ExternalSystems.Abstractions/IExternalSystemAdapter.cs`
- All consumers — see grep above.

**Acceptance criteria.**
- `dotnet build` 0 errors, 0 new warnings.
- `dotnet test` all pass.
- `grep -rn "ExternalSystems.Abstractions.AuthorityDocument\b" --include="*.cs"`
  returns no hits (all renamed).
- The persistence entity `NickERP.Inspection.Core.Entities.AuthorityDocument`
  is unchanged.

**Out of scope.** Any wider refactor of the adapter/DTO/entity layering.
Just the rename.

### 17.3 Status snapshot

| ID | Phase | Status | Branch | Merge commit |
|---|---|---|---|---|
| FU-2 | Followup | pending | `plan/fu2-foreign-case-404` | _(filled)_ |
| FU-3 | Followup | pending | `plan/fu3-source-blob-collision-doc` | _(filled)_ |
| FU-4 | Followup | pending | `plan/fu4-stale-migrations-history-row` | _(filled)_ |
| FU-5 | Followup | pending | `plan/fu5-dotnet-ef-env-var-doc` | _(filled)_ |
| FU-6 | Followup | pending | `plan/fu6-drop-public-migrations-history` | _(filled)_ |
| FU-7 | Followup | pending | `plan/fu7-authoritydocument-rename` | _(filled)_ |

### 17.4 End-of-sprint smoke verification

1. Live host on `:5410` boots clean as `nscim_app`.
2. `/healthz/ready` returns 200.
3. Foreign-tenant case GET returns 404 (FU-2).
4. `ImagingOptions.EnforceCrossTenantBlobGuard` exists and defaults false (FU-3).
5. `inspection.__EFMigrationsHistory` no longer carries the stale row (FU-4).
6. `docs/MIGRATIONS.md` (or RUNBOOK section) documents the env-var quirk (FU-5).
7. `public.__EFMigrationsHistory` is dropped from both DBs (FU-6).
8. `dotnet build` 0 errors; `dotnet test` all pass (FU-7 rename clean).

## 18. Sprint 7 — Operations Runbooks (P1)

### 18.0 Goal

Ship the standalone operations runbooks the production deploy needs. P1's
shape was deferred from Sprint 2 (folded into V2's integration gate at the
time, but only at a "good enough for one demo" level). Now it gets a
proper standalone treatment: deploy, secret rotation, PreRender stalled,
plugin-load-failure, ICUMS outbox backlog. Five runbooks, doc-heavy.

After Sprint 7, an on-call engineer who has never touched the system can
read a single runbook page and execute the correct response to a
named incident.

### 18.1 Phases

```
       ┌── P1 (5 runbooks, single agent) ──┐
       │                                    │
       └── end-of-sprint review ───────────┘
```

Single work item, single agent. ~1 day wall-clock. (Could parallelize as 5
agents writing 1 runbook each, but the unifying voice + cross-references
between runbooks is easier to keep consistent with one author.)

### 18.2 Work items

#### P1 — Operations Runbooks

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none |
| **Parallel-safe with** | (none — single-item sprint) |
| **Effort** | ~1 day |
| **Branch** | `plan/p1-operations-runbooks` |

**Why this matters.** A production system without runbooks is a system
that wakes someone up at 3am and gives them no path forward. Existing
`docs/RUNBOOK.md` (if it exists) covers a small subset; this sprint
delivers five canonical runbooks that an on-call engineer can follow
without prior system knowledge.

**Deliverable.**

A single doc tree under `docs/runbooks/`:

```
docs/runbooks/
├── README.md              (index + when-to-use guide)
├── 01-deploy.md           (deploying a new build to live)
├── 02-secret-rotation.md  (rotating NICKSCAN_DB_PASSWORD, NICKHR_JWT_KEY, plugin secrets)
├── 03-prerender-stalled.md (PreRenderWorker not draining)
├── 04-plugin-load-failure.md (a plugin fails to load on host start)
└── 05-icums-outbox-backlog.md (file-based outbox piling up unpushed)
```

For each runbook, the structure is:

1. **Symptom** — what the on-call sees (alert, log line, user report).
2. **Severity** — P1/P2/P3 with response-time guidance.
3. **Quick triage** — 60-second confidence check ("did anything else change?").
4. **Diagnostic commands** — copy-pasteable shell + psql + browser actions.
5. **Resolution** — step-by-step. **Every command tested.**
6. **Verification** — how to confirm the issue is resolved.
7. **Aftermath** — postmortem template, who to notify.
8. **References** — links to architecture docs, prior incidents, related runbooks.

Specific content per runbook:

- **01-deploy.** From a green CI build on `main`, the steps to ship to
  live `:5410` (and the four other NSCIM ports if multi-host). Include
  pre-flight (build artifact integrity, smoke against staging if it exists),
  deploy command (the `robocopy` from Y:\ pattern that NSCIM_PRODUCTION uses,
  but for ERP V2's deploy target if defined; if not, document the gap as
  "deploy target undefined as of Sprint 7 — see ROADMAP.md for prod-deploy
  decisions"), post-deploy verification (`/healthz/ready`, smoke a known
  case GET).

- **02-secret-rotation.** Rotating `NICKSCAN_DB_PASSWORD` on Postgres
  (rolling: change in DB, change env var, restart hosts). Rotating
  `NICKHR_JWT_KEY` (note: ERP V2 may not use this — verify; if it doesn't,
  document the analogue for ERP V2's auth signing key). Rotating
  per-plugin secrets in `plugin.json` files / appsettings overrides.

- **03-prerender-stalled.** Symptoms: `/healthz/ready` reports
  PreRenderWorker stuck (or AttemptCount climbing on rows). Diagnosis:
  EF migration vs. disk full vs. tenancy regression vs. ImageStore IO
  failure. Resolution paths per cause.

- **04-plugin-load-failure.** A plugin DLL fails to load on host start
  (contract version mismatch, missing dependency, signed/unsigned mix-up).
  How to identify which plugin (host log line shape from F3's contract
  pinning), how to roll back (replace DLL with the previous version,
  restart), and how to keep the host up while one plugin fails (since F3
  the host should fail gracefully — verify and document).

- **05-icums-outbox-backlog.** The ICUMS outbox is file-based (per the
  topology memory). If it backlogs (network down, ICUMS endpoint down,
  signing key rotated mid-flight), files pile up. Runbook: where the
  files live, how to inspect them safely (don't double-send), how to
  drain manually, how to re-sign if the key rotated.

The README.md indexes all five runbooks with a one-line description each
and a "if you don't know which runbook to use, start here" decision tree.

**Files in scope.**
- `docs/runbooks/README.md` (new)
- `docs/runbooks/01-deploy.md` (new)
- `docs/runbooks/02-secret-rotation.md` (new)
- `docs/runbooks/03-prerender-stalled.md` (new)
- `docs/runbooks/04-plugin-load-failure.md` (new)
- `docs/runbooks/05-icums-outbox-backlog.md` (new)
- Possibly link from `docs/ARCHITECTURE.md` and `docs/RUNBOOK.md` (the
  pre-existing one) to the new tree.

**Acceptance criteria.**
- All six files exist with the documented structure.
- Every command in every "Diagnostic commands" and "Resolution" section is
  tested (cut-and-paste runs cleanly under the assumed shell).
- Every claim about the system shape (worker names, port numbers, table
  names, plugin-loader behaviour) matches the live code; no stale facts.
- README's decision tree covers every documented runbook.
- `dotnet build` 0 errors (no code touched, but verify); existing tests
  unaffected.

**Out of scope.**
- Implementing automated runbook checks (a future sprint could turn each
  runbook into a script).
- Runbooks for systems we haven't built yet (NickFinance, edge node).
- Updating the existing `docs/RUNBOOK.md` content beyond linking out to
  the new tree.

### 18.3 Status snapshot

| ID | Phase | Status | Branch | Merge commit |
|---|---|---|---|---|
| P1 | Production prep | pending | `plan/p1-operations-runbooks` | _(filled)_ |

### 18.4 End-of-sprint smoke verification

1. Live host on `:5410` still boots (no code change, but verify).
2. All six runbook files render cleanly in GitHub markdown (no broken
   links, no missing sections).
3. A spot-check on one runbook (suggested: 03-prerender-stalled) — the
   diagnostic command chain executes cleanly against the live host.

## 19. Sprint 8 — Audit Projection + Notifications (P3)

### 19.0 Goal

Ship the audit projection + notifications inbox on top of `audit.events`.
Today, audit events are written but unread — there's no in-app surface
that says "you have a notification" or "show me the recent activity for
this case." P3 closes that gap with a small projection table and a
notifications inbox UI.

After Sprint 8, the rolling-master drainable backlog is empty.

### 19.1 Phases

```
       ┌── P3 phase A (projection schema + worker) ─┐
       │                                             │
       │── P3 phase B (notifications inbox UI) ─────┤── sequential within agent
       │                                             │
       └── end-of-sprint smoke ─────────────────────┘
```

Single work item, single agent (the projection and the UI are tightly
coupled — splitting them adds merge friction). ~2-3 days wall-clock.

### 19.2 Work items

#### P3 — Audit Projection + Notifications Inbox

| | |
|---|---|
| **Status** | pending |
| **Predecessors** | none |
| **Parallel-safe with** | (none — single-item sprint) |
| **Effort** | ~2-3 days |
| **Branch** | `plan/p3-audit-projection-notifications` |

**Why this matters.** `audit.events` is a write-only firehose. To turn
it into something a user can read, we need a projection (reshape into
tenant- and user-scoped notification rows) and an inbox UI (badge, list,
mark-as-read).

**Deliverable.**

Two phases, both on the same branch:

**Phase A — Projection.**

1. New table `audit.notifications` (in the `audit` schema, but not
   constrained to append-only since users can mark-as-read).

   ```sql
   CREATE TABLE audit.notifications (
     "Id" uuid PRIMARY KEY,
     "TenantId" bigint NOT NULL,
     "UserId" uuid NOT NULL,
     "EventId" uuid NOT NULL REFERENCES audit.events("Id"),
     "EventType" text NOT NULL,
     "Title" text NOT NULL,
     "Body" text,
     "Link" text,
     "CreatedAt" timestamptz NOT NULL,
     "ReadAt" timestamptz NULL
   );
   CREATE INDEX ix_notifications_user_unread ON audit.notifications ("UserId", "TenantId") WHERE "ReadAt" IS NULL;
   -- RLS: tenant-isolated, USER-isolated within tenant
   ```

   Plus standard tenant RLS + grants for `nscim_app`.

2. Projection worker. A `BackgroundService` (`AuditNotificationProjector`)
   that reads `audit.events` since-checkpoint, fans-out per
   subscription rules (event type → user(s) to notify), inserts rows into
   `audit.notifications`. Checkpoint stored in a small
   `audit.projection_checkpoints` table.

3. Subscription rules. Hardcoded for Sprint 8 (no per-user UI yet):
   - `inspection.case.opened` → notify the tenant's "case-opened-watcher"
     scope holders (or all admins as a v0).
   - `inspection.case.assigned` → notify the assigned analyst.
   - `inspection.case.verdict-rendered` → notify the case opener.

   Format: a small `INotificationRule[]` collection registered in DI;
   each rule says "when this event type fires, find these users to
   notify, with this title/body template."

**Phase B — Notifications Inbox UI.**

1. A `NotificationsBell.razor` component in the top nav: shows unread
   count (live-updated via SignalR or via a polling interval — pick
   whichever the existing app uses; if neither, polling every 30s is
   fine).

2. A `Notifications.razor` page (`/notifications`): paginated list of
   the current user's notifications, oldest-first or newest-first
   (newest-first), with a "mark all as read" button and per-row
   "mark as read" + "follow link" actions.

3. Endpoints:
   - `GET /api/notifications?unreadOnly=true&page=1` — list
   - `POST /api/notifications/{id}/read` — mark single as read
   - `POST /api/notifications/read-all` — mark all as read

   All under existing auth; tenant-scoped; user-scoped (user can only
   see their own notifications).

**Files in scope.**
- `platform/NickERP.Platform.Audit.Database/Migrations/<timestamp>_Add_Notifications.cs` (new)
- `platform/NickERP.Platform.Audit.Database/Entities/Notification.cs` (new)
- `platform/NickERP.Platform.Audit/Services/AuditNotificationProjector.cs` (new)
- `platform/NickERP.Platform.Audit/Services/INotificationRule.cs` (new)
- `platform/NickERP.Platform.Audit/Services/NotificationRules/` (new dir, hardcoded rules)
- `modules/inspection/src/NickERP.Inspection.Web/Components/Layout/NotificationsBell.razor` (new)
- `modules/inspection/src/NickERP.Inspection.Web/Components/Pages/Notifications.razor` (new)
- `modules/inspection/src/NickERP.Inspection.Web/Endpoints/NotificationsEndpoints.cs` (new)
- `tests/NickERP.Platform.Tests/AuditNotificationProjectorTests.cs` (new)
- `tests/NickERP.Inspection.Web.Tests/NotificationsEndpointsTests.cs` (new)

**Acceptance criteria.**
- Migration applied; `audit.notifications` exists with correct shape and RLS.
- AuditNotificationProjector running on the live host; checkpoint advances
  as new events land.
- A new `inspection.case.opened` event fires → within 30s a row appears
  in `audit.notifications` for the appropriate user(s).
- The bell shows the unread count; clicking through to `/notifications`
  shows the row.
- Marking as read updates the row + decrements the badge.
- Tenant isolation: a user in tenant A never sees tenant B's
  notifications. RLS-enforced.
- User isolation within tenant: a user never sees another user's
  notifications.
- `dotnet build` 0 errors. `dotnet test` all pass; new tests for the
  projector and the endpoints pass.
- Live host smoke: open a case, watch the bell increment.

**Out of scope.**
- Per-user subscription preferences UI (hardcoded rules for now).
- Email / SMS / push notifications (in-app only).
- Notification grouping or threading.
- Notifications for non-Inspection events (when Finance / HR ship events,
  add their rules then).

### 19.3 Status snapshot

| ID | Phase | Status | Branch | Merge commit |
|---|---|---|---|---|
| P3 | Production prep | pending | `plan/p3-audit-projection-notifications` | _(filled)_ |

### 19.4 End-of-sprint smoke verification

1. Live host on `:5410` boots clean.
2. `/healthz/ready` reports the AuditNotificationProjector running.
3. Open a case → notifications bell increments within 30s (logged-in user
   is one of the rule targets).
4. Click the bell → `/notifications` shows the row.
5. Mark as read → bell decrements, row stays in list with read indicator.
6. Switch tenants (via re-login) → no leakage.

### 19.5 Out of sprint

After Sprint 8, the rolling-master drainable backlog is **empty**. Remaining
gated items (G2, P2, image-analysis track) are user-driven and need
explicit unblocking calls.
