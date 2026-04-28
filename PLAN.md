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

| ID | Phase | Status | Branch |
|---|---|---|---|
| H1 | Hardening | pending | `plan/h1-worker-tenant-resolution` |
| H2 | Hardening | pending | `plan/h2-identity-tenancy-interlock` |
| H3 | Hardening | pending (waits for H2) | `plan/f5-prod-minimum` (existing) |
| A1 | Analyst | pending | `plan/a1-rule-evaluation-persistence` |
| A2 | Analyst | pending | `plan/a2-acceptance-bars` |
| E1 | Expansion | pending (waits for H1+H2+H3) | `plan/e1-multi-location-proof` |

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

- **V4** — Analyst viewer Razor (W/L sliders, ROI inspector, pixel probe, 16-bit decode). The big UX piece.
- **G1** — Platform tightening before NickFinance.
- **G2** — NickFinance Petty Cash pathfinder.
- **P1** — Standalone operations runbooks (deploy, secret rotation, etc.).
