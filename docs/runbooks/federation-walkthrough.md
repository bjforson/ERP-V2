# Federation walkthrough — adding a second tenant + location by hand

This is the manual companion to the automated e2e test in
`tests/NickERP.Inspection.E2E.Tests/MultiLocationFederationTests.cs`.
Following these steps proves "federation by location" + "multi-tenant
from day 1" in production-style topology — a Tenant 2 customer with its
own location ("tema") sharing the same vocabulary as Tenant 1's "tema"
without the two seeing each other's cases.

Time budget: **under 10 minutes** once the prereqs are satisfied.

---

## Prereqs (one-time)

| Item | Setting |
|---|---|
| `NICKSCAN_DB_PASSWORD` | Set to the dev Postgres superuser password. |
| `NICKERP_APP_DB_PASSWORD` | In dev, same value as `NICKSCAN_DB_PASSWORD`. |
| `nscim_app` role provisioned | Run `tools/migrations/phase-f5/set-nscim-app-password.sh` once. |
| `__EFMigrationsHistory` relocated | Run `tools/migrations/phase-h3/relocate-migrations-history.sh` if upgrading from a pre-H3 install. |
| Inspection host on `:5410` | `cd modules/inspection/src/NickERP.Inspection.Web && dotnet run` with `nscim_app` connection strings. |
| Demo folders | Create:<br>`C:\inspection-demo\t1-tema-incoming`<br>`C:\inspection-demo\t1-kotoka-incoming`<br>`C:\inspection-demo\t2-tema-incoming` |

The convention from `TESTING.md` applies: migrations run as
`postgres` (out of band via `dotnet ef database update`), the host
runs as `nscim_app` (`LOGIN NOSUPERUSER NOBYPASSRLS`). RLS only
actually enforces under `nscim_app` — running as `postgres` silently
bypasses every policy.

---

## Step 1 — Register Tenant 2

The Portal `/tenants` page (`http://localhost:5400/tenants`) is the
canonical UI. Click **New tenant**, fill in:

| Field | Value |
|---|---|
| Code | `other-customer` |
| Name | `Other Customer` |
| Billing plan | `internal` |
| Time zone | `Africa/Accra` |
| Locale | `en-GH` |
| Currency | `GHS` |

Save. The new row appears in the list with auto-assigned `Id` (the next
free id; `2` on a fresh install).

If the Portal isn't running yet, you can do this directly via psql:

```bash
PGPASSWORD="$NICKSCAN_DB_PASSWORD" \
  "/c/Program Files/PostgreSQL/18/bin/psql.exe" \
  -h localhost -U postgres -d nickerp_platform -c "
    INSERT INTO tenancy.tenants
      (\"Code\", \"Name\", \"BillingPlan\", \"TimeZone\", \"Locale\",
       \"Currency\", \"IsActive\", \"CreatedAt\")
    VALUES ('other-customer', 'Other Customer', 'internal',
            'Africa/Accra', 'en-GH', 'GHS', true, now())
    RETURNING \"Id\";"
```

Note the returned id — you'll need it as `<T2_ID>` below.

---

## Step 2 — Provision a Tenant 2 admin user

The Inspection admin UI doesn't yet have a "switch tenant" UI in this
build. For the walkthrough, provision a Tenant 2 user directly — the
analyst in Step 4 will be assigned by this admin later.

```bash
T2_ID=2  # whatever Step 1 returned

PGPASSWORD="$NICKSCAN_DB_PASSWORD" \
  "/c/Program Files/PostgreSQL/18/bin/psql.exe" \
  -h localhost -U postgres -d nickerp_platform -c "
    INSERT INTO identity.identity_users
      (\"Id\", \"Email\", \"NormalizedEmail\", \"DisplayName\",
       \"IsActive\", \"CreatedAt\", \"UpdatedAt\", \"TenantId\")
    VALUES (gen_random_uuid(), 'admin@other-customer',
            'ADMIN@OTHER-CUSTOMER', 'Other Customer Admin',
            true, now(), now(), $T2_ID);"
```

`identity.identity_users` is **carved out of FORCE RLS** (per H2 — see
`IdentityUsersRlsGuard.cs` for the rationale), so the insert works
without `SET app.tenant_id`. Future per-tenant inserts on
`identity_users` still need the right `TenantId` column value — RLS
isn't enforcing it, but the application-layer query filters DO.

---

## Step 3 — Register a Tenant 2 location

Hit `/locations` on Inspection v2 admin (`http://localhost:5410`) as
the Tenant 2 admin (use `X-Dev-User: admin@other-customer` if you're
on dev-bypass). Click **New location**, fill in:

| Field | Value |
|---|---|
| Code | `tema` |
| Name | `Tema Port (Other Customer)` |
| Region | `Greater Accra` |
| Time zone | `Africa/Accra` |

Save. The row appears under Tenant 2.

The unique index `ux_locations_tenant_code` is per-tenant, so Tenant 1's
existing `tema` and Tenant 2's new `tema` both coexist. RLS prevents
either tenant from seeing the other's row.

If you want the psql equivalent (e.g. seeding a clean dev environment):

```bash
T2_ID=2
T2_TEMA_ID=$(uuidgen)

PGPASSWORD="$NICKSCAN_DB_PASSWORD" \
  "/c/Program Files/PostgreSQL/18/bin/psql.exe" \
  -h localhost -U postgres -d nickerp_inspection -c "
    SET app.tenant_id = '$T2_ID';
    INSERT INTO inspection.locations
      (\"Id\", \"Code\", \"Name\", \"Region\", \"TimeZone\",
       \"IsActive\", \"CreatedAt\", \"TenantId\")
    VALUES ('$T2_TEMA_ID', 'tema', 'Tema Port (Other Customer)',
            'Greater Accra', 'Africa/Accra', true, now(), $T2_ID);"
```

Note the location id — needed in Step 4.

---

## Step 4 — Register a Tenant-2-specific scanner

Still on Inspection v2 admin as the Tenant 2 admin, hit `/scanners`.
Click **New scanner**:

| Field | Value |
|---|---|
| Plugin | `fs6000` |
| Location | `Tema Port (Other Customer)` |
| Display name | `T2 Tema FS6000` |
| Config JSON | `{ "WatchPath": "C:\\inspection-demo\\t2-tema-incoming", "PollIntervalSeconds": 2 }` |

Save. The `ScannerIngestionWorker` will discover it on its next 60-second
discovery pass — no host restart needed. The worker walks every active
tenant in `tenancy.tenants`, sets `app.tenant_id` per tenant, and
enumerates that tenant's active scanner instances under RLS (one
discovery cycle is one pass per tenant). Tenant 1's existing
`tema-fs6000` is unchanged.

---

## Step 5 — Assign a Tenant 2 analyst

Provision a Tenant 2 analyst and assign them to the Tema-2 location:

```bash
T2_ID=2
T2_TEMA_ID=...            # from Step 3
T2_ANALYST_ID=$(uuidgen)
T2_ADMIN_ID=$(...)        # from Step 2 — query identity_users by email

PGPASSWORD="$NICKSCAN_DB_PASSWORD" \
  "/c/Program Files/PostgreSQL/18/bin/psql.exe" \
  -h localhost -U postgres -d nickerp_platform -c "
    INSERT INTO identity.identity_users
      (\"Id\", \"Email\", \"NormalizedEmail\", \"DisplayName\",
       \"IsActive\", \"CreatedAt\", \"UpdatedAt\", \"TenantId\")
    VALUES ('$T2_ANALYST_ID', 'analyst-tema@t2', 'ANALYST-TEMA@T2',
            'Tema-2 Analyst', true, now(), now(), $T2_ID);"

PGPASSWORD="$NICKSCAN_DB_PASSWORD" \
  "/c/Program Files/PostgreSQL/18/bin/psql.exe" \
  -h localhost -U postgres -d nickerp_inspection -c "
    SET app.tenant_id = '$T2_ID';
    INSERT INTO inspection.location_assignments
      (\"Id\", \"IdentityUserId\", \"LocationId\", \"Roles\",
       \"GrantedAt\", \"GrantedByUserId\", \"IsActive\", \"TenantId\")
    VALUES (gen_random_uuid(), '$T2_ANALYST_ID', '$T2_TEMA_ID',
            'analyst', now(), '$T2_ADMIN_ID', true, $T2_ID);"
```

The location-assignment row is what makes federation real for the
analyst's `/cases` view: `Cases.razor` filters on
`LocationAssignments.IdentityUserId == _userId AND IsActive` and only
shows cases whose `LocationId` is in that set.

---

## Step 6 — Drop a triplet, verify isolation

Drop a real FS6000 triplet into Tenant 2's watch folder (use the same
synthesizer pattern from the demo runbook):

```csharp
// One-off script:
NickERP.Inspection.E2E.Tests.E2EFixtures.WriteFs6000Triplet(
    @"C:\inspection-demo\t2-tema-incoming",
    "MSCU8675309-T2");
```

Within ~60s the case appears under `inspection.cases` keyed to the
new stem with `TenantId=2` and `LocationId=<T2 Tema id>`.

Federation verification — three curls (or three browser sessions with
different `X-Dev-User` headers):

```bash
# Tenant 1's Tema analyst — must NOT see the Tenant 2 case.
curl -H "X-Dev-User: analyst-tema@t1" http://localhost:5410/cases | grep MSCU8675309-T2
# (no match expected)

# Tenant 1's Kotoka analyst — must NOT see it either.
curl -H "X-Dev-User: analyst-kotoka@t1" http://localhost:5410/cases | grep MSCU8675309-T2
# (no match expected)

# Tenant 2's Tema analyst — MUST see it.
curl -H "X-Dev-User: analyst-tema@t2" http://localhost:5410/cases | grep MSCU8675309-T2
# (one or more matches expected)
```

If any of the first two `grep`s match, federation is broken — file an
incident, do NOT continue. The most likely culprit is a missing
`SET app.tenant_id` in some query path; check the host logs for
"Failed to push tenant id to Postgres" warnings from the
`TenantConnectionInterceptor`.

---

## Step 7 — psql RLS canary as `nscim_app`

Open a fresh psql as `nscim_app`. Without setting `app.tenant_id`,
RLS should fail closed (zero rows) on every tenant-owned table:

```bash
PGPASSWORD="$NICKERP_APP_DB_PASSWORD" \
  "/c/Program Files/PostgreSQL/18/bin/psql.exe" \
  -h localhost -U nscim_app -d nickerp_inspection -c "
    SELECT count(*) FROM inspection.cases;
    SELECT count(*) FROM inspection.locations;"
# Both → 0
```

With `SET app.tenant_id = '1'`, only Tenant 1's rows appear:

```bash
PGPASSWORD="$NICKERP_APP_DB_PASSWORD" \
  "/c/Program Files/PostgreSQL/18/bin/psql.exe" \
  -h localhost -U nscim_app -d nickerp_inspection <<'SQL'
SET app.tenant_id = '1';
SELECT \"TenantId\", count(*) FROM inspection.cases GROUP BY \"TenantId\";
SQL
# Output: 1 | <T1 case count>
```

Switch to `app.tenant_id = '2'` and you see only Tenant 2's case(s).
If you see rows tagged with the WRONG tenant id, RLS isn't enforcing —
verify `nscim_app` has `BYPASSRLS=false` (`\du nscim_app` should show
NO `Bypass RLS` attribute).

---

## Troubleshooting

### Tenant context not resolved (host logs say "no valid tenant_id claim")

`TenantResolutionMiddleware` reads `nickerp:tenant_id` from the
authenticated principal. The claim originates in
`NickErpAuthenticationHandler` from the resolved `IdentityUser.TenantId`
column. If a user has `TenantId=0` or NULL in the DB, the claim won't
materialize and the middleware logs a warning. Fix: update the row's
`TenantId` to the right tenant.

### RLS returning zero rows under `nscim_app` even with `app.tenant_id` set

Two common causes:
1. Connection pooling — Npgsql pools by default. The `app.tenant_id`
   you set with one client is NOT visible to a different connection.
   In a single psql session this is fine; in code, the
   `TenantConnectionInterceptor` re-pushes the value on every
   `ConnectionOpenedAsync` callback.
2. The role has `BYPASSRLS=true` somehow. Run `\du nscim_app` and
   confirm `NOBYPASSRLS`. If `BYPASSRLS` is set, the RLS policies are
   silently no-ops; reset with
   `ALTER ROLE nscim_app NOBYPASSRLS;` as `postgres`.

### Scanner watching the wrong folder

The `WatchPath` in `ConfigJson` is the only configuration the FS6000
adapter looks at. If you mistype it (e.g. a typo, or the directory
doesn't exist yet), the worker logs `FS6000 WatchPath does not exist`
and the per-instance loop backs off. Create the directory; the worker
will retry on its next cycle without needing a host restart.

### A Tenant-1 analyst CAN see a Tenant-2 case

Federation is broken. Most likely cause: someone bypassed
`UseNickErpTenancy()` middleware or registered a route that doesn't go
through `[Authorize]`. Check the path in question against
`Program.cs`'s middleware order — `UseAuthentication` →
`UseAuthorization` → `UseNickErpTenancy` is the required ordering.

### Step 5's `INSERT INTO location_assignments` returns "new row violates row-level security policy"

You forgot `SET app.tenant_id` before the INSERT. The RLS WITH CHECK
clause fails when the row's `TenantId` doesn't match the session's
`app.tenant_id`. The fix is in the SQL: `SET app.tenant_id = '$T2_ID';
INSERT ...`.

---

## Resetting between walkthroughs

For a clean slate, drop Tenant 2's data:

```bash
T2_ID=2
PGPASSWORD="$NICKSCAN_DB_PASSWORD" \
  "/c/Program Files/PostgreSQL/18/bin/psql.exe" \
  -h localhost -U postgres -d nickerp_inspection -c "
    DELETE FROM inspection.cases WHERE \"TenantId\" = $T2_ID;
    DELETE FROM inspection.scanner_device_instances WHERE \"TenantId\" = $T2_ID;
    DELETE FROM inspection.location_assignments WHERE \"TenantId\" = $T2_ID;
    DELETE FROM inspection.locations WHERE \"TenantId\" = $T2_ID;"

PGPASSWORD="$NICKSCAN_DB_PASSWORD" \
  "/c/Program Files/PostgreSQL/18/bin/psql.exe" \
  -h localhost -U postgres -d nickerp_platform -c "
    DELETE FROM identity.identity_users WHERE \"TenantId\" = $T2_ID;
    DELETE FROM tenancy.tenants WHERE \"Id\" = $T2_ID;"
```

Wipe the Tenant-2 watch folder:

```powershell
Remove-Item -Recurse -Force C:\inspection-demo\t2-tema-incoming\*
```

---

## Related material

- **Automated equivalent.**
  `tests/NickERP.Inspection.E2E.Tests/MultiLocationFederationTests.cs`.
  Run with `dotnet test --filter Category=Integration` from the repo
  root. Same multi-tenant + multi-location seeding, fully scripted.
  Spins up an ephemeral Postgres pair on the dev cluster and boots
  the Inspection host as `nscim_app`.
- **Demo walkthrough.** [`docs/runbooks/demo-walkthrough.md`](demo-walkthrough.md)
  — single-tenant lifecycle for the analyst-facing demo.
- **`TENANCY.md`** — design of record for `ITenantContext`,
  middleware, interceptors, and the F1 RLS rollout.
- **`TESTING.md`** — env-var conventions + the role-cutover (F5/H3)
  sequence the runbook prereqs assume.
