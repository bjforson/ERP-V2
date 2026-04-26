# NickERP.Platform.Tenancy

> Status: A.3 shipped (entities, DbContext, middleware, interceptors, initial migration). Tenancy admin REST API, EF query-filter extension, RLS policy generator, and demo are still open.
>
> See `ROADMAP.md §A.3` for the task list.

---

## What this layer does

Owns the canonical record of every isolated platform instance (every "customer") and the plumbing that makes sure modules NEVER read or write across tenants by accident:

```csharp
public interface ITenantContext
{
    long TenantId { get; }
    bool IsResolved { get; }
    void SetTenant(long tenantId);
}
```

`TenantResolutionMiddleware` reads the authenticated principal's `nickerp:tenant_id` claim (set by the Identity layer's auth handler) and stamps the per-request `ITenantContext`. EF Core interceptors then:

- **Stamp** `TenantId` on every newly-added `ITenantOwned` entity (`TenantOwnedEntityInterceptor`).
- **Push** `app.tenant_id` to Postgres on every connection open (`TenantConnectionInterceptor`) so Postgres Row-Level Security policies can fail-closed-by-default at the storage layer.

Modules wire in with two lines:

```csharp
// Program.cs
builder.Services.AddNickErpTenancy();      // in-process bits

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseNickErpTenancy();                   // middleware AFTER auth
```

And on each `DbContext`:

```csharp
options.AddInterceptors(
    sp.GetRequiredService<TenantOwnedEntityInterceptor>(),
    sp.GetRequiredService<TenantConnectionInterceptor>());
```

## Domain model

| Entity | Role |
|---|---|
| `Tenant` | One row per isolated platform instance. `Id` is `long`, seeded as `1` for the default deployment ("Nick TC-Scan Operations"). Lives in `tenancy.tenants` in the `nickerp_platform` Postgres DB. |
| `ITenantOwned` (marker) | Every business-data entity in every module implements this. `TenantId` column gets stamped on insert and read by every query filter / RLS policy. |

## RLS conventions

Every module's tables that hold business data must:

1. Have a `tenant_id BIGINT NOT NULL` column.
2. `ALTER TABLE … ENABLE ROW LEVEL SECURITY` + `FORCE ROW LEVEL SECURITY`.
3. Define a policy: `USING (tenant_id = COALESCE(current_setting('app.tenant_id', true), '0')::bigint)`.
4. The `COALESCE` to `'0'` fail-closed default is mandatory — otherwise an unset session variable lets queries through (the v1 incident in `reference_rls_now_enforces.md`).

The `TenantConnectionInterceptor` pushes the value; module migrations write the policies. A future helper in this package (`TenantQueryFilterExtensions`) will scan a `DbContext` model and add EF query filters automatically — backlog.

## Open contract questions

- [ ] **Cross-tenant background jobs.** Today the interceptor throws if `ITenantContext.IsResolved` is false. Background jobs that span tenants need to call `SetTenant(N)` per iteration. A helper / decorator pattern would be nicer.
- [ ] **Tenant-from-JWT vs tenant-from-subdomain.** Single-tenant deployments don't need either; multi-tenant deployments may want subdomain-based resolution. Defer until a real multi-tenant case forces the decision.
- [ ] **Soft-delete vs hard-delete on tenant suspension.** `Tenant.IsActive=false` is the soft path. If a tenant ever fully terminates, what happens to their data — purge, archive, anonymise? Compliance review needed before that's answered.

## Out of scope

- Per-record authorisation. Tenancy says "tenant 1 sees only tenant 1's rows." Whether user X within tenant 1 can see row Y is the module's call.
- Multi-region. All tenants share one cluster today. Multi-region is a Phase-5 conversation.

## Related docs

- `IDENTITY.md` — Identity layer that sets the `nickerp:tenant_id` claim this layer reads.
- `ROADMAP.md §A.3` — task list and acceptance criteria.
- v1 repo `reference_rls_now_enforces.md` (memory) — the v1 RLS-not-enforcing incident; v2 is built to not repeat that.
