# Team TS — Tenant Safety

## Mission

Convert "multi-tenant from day 1" from an unenforced application-layer claim into a DB-enforced reality. Add Postgres RLS policies on every tenant-owned table; attach the tenant interceptors to every `DbContext` registration; remove the `SetTenant(1)` fallback that silently coerces unresolved tenants; tenant-filter the cross-DB Identity.Users query.

## Why this matters

The architecture doc (`C:\Shared\ERP V2\docs\ARCHITECTURE.md` §7.1) prescribes RLS for defense-in-depth. **No v2 migration emits any `CREATE POLICY` SQL.** `TenantConnectionInterceptor` (in `platform/NickERP.Platform.Tenancy`) is registered in DI by `AddNickErpTenancy()` but **never attached to any DbContext** via `options.AddInterceptors(...)`. So `app.tenant_id` is never SET on any connection — even if RLS policies existed, they'd `COALESCE(current_setting('app.tenant_id', true), '0')::bigint = TenantId` and fail closed to zero, returning no rows. The architecture's promise has never held.

The v1 NSCIM team retrofitted this to v1's 5 Postgres databases on 2026-04-25 (memory note "Tenant RLS now actually enforces"). v2 needs the same retrofit before more code lands on top.

## Current state

- 5 v2 migrations exist (3 inspection + 2 platform). Search via grep for `ROW LEVEL SECURITY|tenant_isolation|set_config|CREATE POLICY` returns **zero hits** across all migrations.
- `platform/NickERP.Platform.Tenancy/TenantConnectionInterceptor.cs` writes `SET app.tenant_id = '{id}'` on connection open via `IDbConnectionInterceptor.ConnectionOpenedAsync`. It compiles and is in DI but no `DbContext` registration includes it.
- `modules/inspection/src/NickERP.Inspection.Web/Program.cs` registers `InspectionDbContext` with no interceptors (~line 36-41).
- `platform/NickERP.Platform.Identity.Database/IdentityDatabaseServiceCollectionExtensions.cs` registers `IdentityDbContext` with no interceptors.
- Same gap on `AuditDbContext` and `TenancyDbContext`.
- `modules/inspection/src/NickERP.Inspection.Web/Services/CaseWorkflowService.cs` line ~61 has `if (!_tenant.IsResolved) _tenant.SetTenant(1);` — a silent fallback that coerces unresolved tenants to tenant 1.
- `modules/inspection/src/NickERP.Inspection.Web/Components/Pages/LocationAssignments.razor` line ~103 reads `await Identity.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync()` — no `WHERE TenantId =` filter; renders every tenant's users in the dropdown.

## Deliverables

### 1. RLS migration on `nickerp_inspection`

New migration: `modules/inspection/src/NickERP.Inspection.Database/Migrations/{timestamp}_Add_RLS_Policies.cs`.

For every table in the `inspection` schema that has a `TenantId` column, the migration's `Up()` must:

```sql
ALTER TABLE inspection.<table> ENABLE ROW LEVEL SECURITY;
ALTER TABLE inspection.<table> FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_<table> ON inspection.<table>
  USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint)
  WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
```

Tables (verify via the model snapshot at `modules/inspection/src/NickERP.Inspection.Database/Migrations/InspectionDbContextModelSnapshot.cs`): `locations`, `stations`, `scanner_device_instances`, `external_system_instances`, `external_system_bindings`, `location_assignments`, `cases`, `scans`, `scan_artifacts`, `scan_render_artifacts`, `authority_documents`, `review_sessions`, `analyst_reviews`, `findings`, `verdicts`, `outbound_submissions`.

`Down()` reverses with `DROP POLICY` + `ALTER TABLE ... DISABLE ROW LEVEL SECURITY` for each.

### 2. RLS migrations on platform DBs

Same pattern, separate migrations:

- `platform/NickERP.Platform.Identity.Database/Migrations/{timestamp}_Add_RLS_Policies.cs` — tables: `users`, `app_scopes`, `user_scopes`, `service_tokens`, etc. (verify against the model snapshot).
- `platform/NickERP.Platform.Tenancy.Database/Migrations/{timestamp}_Add_RLS_Policies.cs` — note: `tenants` table itself probably should NOT have RLS (tenants need to see themselves). Use judgment: any tenant-owned table gets policy; the `tenants` table is the root and stays unprotected.
- `platform/NickERP.Platform.Audit.Database/Migrations/{timestamp}_Add_RLS_Policies.cs` — table `events`.

### 3. Attach interceptors to every DbContext registration

Modify each registration to attach `TenantConnectionInterceptor` and `TenantOwnedEntityInterceptor`. Use the `IServiceProvider` overload:

```csharp
services.AddDbContext<InspectionDbContext>((sp, opts) =>
{
    opts.UseNpgsql(inspectionConn ?? throw new InvalidOperationException(...),
        npgsql => npgsql.MigrationsAssembly(typeof(InspectionDbContext).Assembly.GetName().Name));
    opts.AddInterceptors(
        sp.GetRequiredService<TenantConnectionInterceptor>(),
        sp.GetRequiredService<TenantOwnedEntityInterceptor>());
});
```

Files:
- `modules/inspection/src/NickERP.Inspection.Web/Program.cs` (the `AddDbContext<InspectionDbContext>` call)
- `platform/NickERP.Platform.Identity.Database/IdentityDatabaseServiceCollectionExtensions.cs` (the `AddDbContext<IdentityDbContext>` in `AddNickErpIdentityCore`)
- `platform/NickERP.Platform.Tenancy.Database/TenancyDatabaseServiceCollectionExtensions.cs` (if separate; otherwise inline in Program.cs)
- `platform/NickERP.Platform.Audit.Database/AuditDatabaseServiceCollectionExtensions.cs` (the `AddDbContext<AuditDbContext>` in `AddNickErpAuditCore`)

Verify both interceptors are publicly registered by `AddNickErpTenancy()` — if either is `internal`, expose them.

### 4. Remove the `SetTenant(1)` fallback

In `modules/inspection/src/NickERP.Inspection.Web/Services/CaseWorkflowService.cs`, find:

```csharp
if (!_tenant.IsResolved) _tenant.SetTenant(1);
```

Replace with:

```csharp
if (!_tenant.IsResolved)
    throw new InvalidOperationException(
        "Tenant context is not resolved. Verify NickErpTenancy middleware ran for this request.");
```

Also remove the `EnsureTenant` helper if it has a similar coerce-to-1 path.

### 5. Tenant-filter `LocationAssignments.razor`

In `modules/inspection/src/NickERP.Inspection.Web/Components/Pages/LocationAssignments.razor`, the `_users` query:

```csharp
_users = await Identity.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync();
```

Must become:

```csharp
_users = await Identity.Users.AsNoTracking()
    .Where(u => u.TenantId == Tenant.TenantId)
    .OrderBy(u => u.Email).ToListAsync();
```

(Inject `ITenantContext Tenant` if not already.)

Also add a `HasQueryFilter` to `IdentityUser` in `IdentityDbContext.OnModelCreating` so callers can't forget — find the `modelBuilder.Entity<IdentityUser>(e => { ... })` block and add:

```csharp
e.HasQueryFilter(u => u.TenantId == _tenantContext.TenantId);
```

This requires the DbContext to take an `ITenantContext` — verify the constructor signature; if not present, plumb it. (If this is too invasive and conflicts with platform conventions, the explicit `.Where()` on the call site is acceptable as a fallback.)

## Acceptance criteria

Run all of these and capture output:

1. **Policies exist:**
   ```bash
   PGPASSWORD="$NICKSCAN_DB_PASSWORD" psql -h localhost -U postgres -d nickerp_inspection -c "
     SELECT schemaname, tablename, policyname FROM pg_policies WHERE schemaname='inspection' ORDER BY tablename;"
   ```
   Expected: one `tenant_isolation_<table>` row per table listed in deliverable 1.

2. **RLS is FORCED:**
   ```bash
   PGPASSWORD="$NICKSCAN_DB_PASSWORD" psql -h localhost -U postgres -d nickerp_inspection -c "
     SELECT relname, relrowsecurity, relforcerowsecurity FROM pg_class
     WHERE relname IN ('cases','scans','scan_artifacts','locations') AND relnamespace =
       (SELECT oid FROM pg_namespace WHERE nspname='inspection');"
   ```
   Expected: every row has `relrowsecurity=t` AND `relforcerowsecurity=t`.

3. **Tenant fail-closed:** Connect as a non-superuser role (or use `SET ROLE` on the postgres connection if no app role exists yet). Without `SET app.tenant_id`:
   ```sql
   SELECT count(*) FROM inspection.cases;
   ```
   Expected: `0` rows (because `current_setting('app.tenant_id', true) IS NULL` → `'0'::bigint` → no match).

4. **Tenant scoping works:** With `SET app.tenant_id = '1'`, `SELECT count(*) FROM inspection.cases` returns only tenant 1's rows.

5. **Build green:** `dotnet build` from repo root → 0 errors, 0 new warnings.

6. **Inspection v2 starts cleanly:** Launch the host on `localhost:5410`, verify `/cases` renders 200 (the X-Dev-User header path lands on tenant 1 as before).

7. **Unresolved tenant throws:** Find a code path with no tenant middleware (e.g., a unit test or a synthetic call), verify `CaseWorkflowService.OpenCaseAsync` throws `InvalidOperationException` instead of silently writing to tenant 1.

## Out of scope (do NOT do)

- Don't touch `modules/inspection/plugins/*` — that's Team PT's territory.
- Don't add health checks — that's Team PM.
- Don't add new tests — that's Team TF (you may add a single integration-test stub if it's the only way to verify acceptance #3, but commit only the assertion logic, not a full test infrastructure).
- Don't edit anything under `C:\Shared\NSCIM_PRODUCTION\` — v1 is read-only per memory.

## Dependencies

- **Inbound:** none.
- **Outbound:** Team PT depends on the new `_tenant.IsResolved` throw behaviour. Team PM's health checks layer on top of the interceptor-attached DbContexts.

## Notes / gotchas

- **Tenant 0 / system context.** A future "system / cross-tenant" context (G1 in next sprint) will need a way to bypass RLS for system jobs (FX rate updates, multi-tenant rollups). For now, `'0'::bigint` is the fail-closed sentinel. Don't grant a bypass role yet.
- **Migration apply at deploy.** Team PM is wiring `db.Database.Migrate()` at startup. For now, apply manually:
  ```bash
  cd "C:\Shared\ERP V2"
  dotnet ef migrations script Add_ScanRenderArtifact Add_RLS_Policies \
    --project modules/inspection/src/NickERP.Inspection.Database \
    --startup-project modules/inspection/src/NickERP.Inspection.Database \
    --idempotent --output /tmp/rls.sql
  PGPASSWORD="$NICKSCAN_DB_PASSWORD" psql -h localhost -U postgres -d nickerp_inspection -f /tmp/rls.sql
  ```
  Repeat per platform DB.
- **`IdentityUser.TenantId`** — verify this column exists. If it doesn't, `IdentityUser` may not be tenant-owned in the current model (a global users table); in that case, the `LocationAssignments.razor` filter becomes meaningless and the right fix is for users to be scoped via `LocationAssignment` already, not the dropdown. Document the choice in your commit message.
- **Tests for this work** belong in Team TF's first-wave deliverables (a TestContainers-based test that asserts cross-tenant isolation).

## Commit message convention

```
feat(platform,inspection): RLS + tenant interceptor wiring (Sprint TS)

Closes the gap between the architecture doc's "multi-tenant from day 1"
claim and reality. Before this commit, no migration emitted any
CREATE POLICY SQL and no DbContext registration attached
TenantConnectionInterceptor — app.tenant_id was never SET, so any RLS
that did exist would have failed closed to zero. Now:

- Migrations on inspection / identity / audit DBs emit
  ENABLE + FORCE ROW LEVEL SECURITY plus tenant_isolation_<table>
  policies USING (TenantId = COALESCE(current_setting(...), '0')::bigint).
- Every AddDbContext registration attaches TenantConnectionInterceptor
  + TenantOwnedEntityInterceptor.
- CaseWorkflowService throws on unresolved tenant instead of silently
  defaulting to tenant 1.
- LocationAssignments.razor filters Identity.Users by tenant.

Verified: psql as non-superuser without app.tenant_id returns 0 rows
from inspection.cases; with SET app.tenant_id='1' returns tenant 1's
rows only. Inspection v2 boots clean on :5410 with the dev-bypass
header.

Co-Authored-By: Claude (Sprint Team TS)
```
