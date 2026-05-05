# Runbook 12 — NickFinance (G2 pathfinder) operations

> **Scope.** Operating the NickFinance module shipped Sprint 10 as the
> "G2 pathfinder" — petty-cash voucher workflow + suite-wide FX rate
> publishing + monthly period locks, mounted into `apps/portal` for v0.
> This runbook covers deploy / health / migrations / backup / FX-rate
> publish / common operational tasks for the NickFinance database
> (`nickerp_nickfinance`) and the NickFinance services that run inside
> the portal host.
>
> **NickFinance is portal-mounted today.** Per the G2 §11 design,
> NickFinance is *optional* — a tenant may go to production without
> petty-cash. The portal host calls
> `services.AddNickErpNickFinanceWeb(configuration)`; if
> `ConnectionStrings:NickFinance` is null/empty, the module simply
> isn't wired. Deploy = redeploy the portal. A standalone
> `NickERP.NickFinance.Web` host on port 5420 is post-pilot
> (`AddNickErpNickFinanceSharedChrome` is in place but not yet a
> separate service).
>
> **`v1-clone/finance/` is NOT this module.** The v1 NickFinance
> AP/AR/Banking/Budgeting/CoA tree under `v1-clone/finance/` is the
> v1-flavoured side that will be folded into v2-native modules across
> ~6-10 sprints post-pilot per ROADMAP §1 locked answer 7. This
> runbook is for the v2-native G2 pathfinder only — petty-cash + FX
> + periods.
>
> **Sister docs:**
> - [`07-sprint-13-live-deploy.md`](07-sprint-13-live-deploy.md) §5.4 —
>   the live-deploy `nickfinance.sql` script that initialised the
>   `nickerp_nickfinance` DB.
> - [`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
>   — the v2-locked backup tool. The §6 pgbackrest stanza covers the
>   NickFinance DB alongside the platform + inspection DBs.
> - [`02-secret-rotation.md`](02-secret-rotation.md) — `nscim_app`
>   posture; NickFinance reads via the same role.
> - [`01-deploy.md`](01-deploy.md) — the portal-host deploy mechanics.
>   NickFinance ships as part of the portal until the post-pilot
>   standalone-host split lands.
> - [`../system-context-audit-register.md`](../system-context-audit-register.md)
>   — registers `FxRatePublishService` as a `SetSystemContext` caller
>   (suite-wide `fx_rate` writes need `app.tenant_id = '-1'`).
> - `ROADMAP.md` §1 (locked answer 7) — the "fold v1-clone into
>   v2-native" arc this runbook flags as post-pilot scope.
>
> ---

## 1. Module overview

NickFinance v2 is the **G2 pathfinder** — the first non-platform module
to ship after Inspection (G1). It deliberately scopes small to
exercise the platform contracts (Identity / Tenancy / Audit / Plugins)
end-to-end without committing the larger v1 finance surface (AP, AR,
Banking, Budgeting, CoA, GL) to v2 design upfront. Those v1 modules
live under `v1-clone/finance/` until the post-pilot fold-in (§10).

What G2 ships in v2:

- **Petty-cash boxes + vouchers + ledger events.** A "petty cash box"
  is a per-tenant float with a base currency. Vouchers move through
  Drafted → Approved → Disbursed → Reconciled with role-gated
  transitions. Each transition writes a `petty_cash_ledger_events`
  row + an audit event.
- **Suite-wide FX rates.** A single `fx_rate` table in
  `nickerp_nickfinance` stores published rates; every NickERP module
  reads via `IFxRateLookup`. Writes go through
  `FxRatePublishService` which is the canonical
  `SetSystemContext()` caller for suite-wide data.
- **Monthly period locks.** A `petty_cash_periods` table tracks
  open/closed status per (tenant, year-month). Posting into a closed
  period throws `PeriodLockedException` unless the caller has
  `petty_cash.reopen_period` (a late-post audit event is then emitted).

What G2 does NOT ship (and won't until post-pilot fold-in):

- **No GL chart-of-accounts in v2-native.** The v1 `NickFinance.Coa`
  module under `v1-clone/finance/` is the v1-flavoured side; v2-native
  GL is a post-pilot deliverable.
- **No AP / AR / Banking / Budgeting in v2-native.** Same shape — v1
  side under `v1-clone/finance/` is operational, v2-native fold-in is
  post-pilot per ROADMAP §1 answer 7.

Database: `nickerp_nickfinance` in the same Postgres cluster as the
platform + inspection DBs. Schema: `nickfinance`. Migration history
table: `nickfinance.__EFMigrationsHistory`. Two migrations apply
(see §5).

## 2. Service layout

NickFinance is composed of three .NET projects under
`modules/nickfinance/src/`:

| Project | What it ships | Reference |
|---|---|---|
| `NickERP.NickFinance.Core` | Entities + role constants + value objects (`Money`) — no DB or HTTP knowledge | `Roles/PettyCashRoles.cs` defines `petty_cash.reopen_period`, `petty_cash.publish_fx`, `petty_cash.manage_periods` |
| `NickERP.NickFinance.Database` | EF Core `NickFinanceDbContext` + migrations + `FxRateLookup` | `AddNickErpNickFinance(connectionString)` |
| `NickERP.NickFinance.Web` | Razor pages + `/api/nickfinance` minimal-API endpoints + workflow services + chrome | `AddNickErpNickFinanceWeb(configuration)` |

The host (today only `apps/portal`, post-pilot also a standalone
`NickERP.NickFinance.Web` on port 5420) calls
`AddNickErpNickFinanceWeb(configuration)`. The extension reads
`ConnectionStrings:NickFinance` from config; if null or empty the
extension returns early — the module simply isn't deployed for that
host. The portal then conditionally maps endpoints + Razor pages
based on the same connection-string check.

**No cross-module imports.** Per G2 §11 + the platform-layer rule,
no other module depends on NickFinance types directly. Inspection
that needs an FX rate goes through `IFxRateLookup` (an interface on
the platform-shared boundary), not through `NickFinance.Web` types.

**Pages.** Five Razor pages under
`modules/nickfinance/src/NickERP.NickFinance.Web/Components/Pages/`:
`PettyCashBoxes.razor`, `BoxDetail.razor`, `VoucherDetail.razor`,
`Periods.razor`, `FxRates.razor`. They render under the portal at
the same routes the portal mounts them on.

**Endpoints.** `/api/nickfinance/vouchers/{id}/{transition}`,
`/api/nickfinance/fx-rates`, `/api/nickfinance/periods/{ym}/close`,
`/api/nickfinance/periods/{ym}/reopen`. All require auth via the
portal's default policy; role gates checked in-handler.

## 3. Deploy

**Portal-mounted today.** A NickFinance code change is deployed by
redeploying the portal:

```bash
cd "C:/Shared/ERP V2"
dotnet publish apps/portal/NickERP.Portal.csproj \
  -c Release \
  -o publish/Portal
```

Then per [`01-deploy.md`](01-deploy.md) §5 — robocopy `publish/Portal`
to the host's deploy target, restart the portal service. The
NickFinance assemblies (`NickERP.NickFinance.Core.dll`,
`NickERP.NickFinance.Database.dll`, `NickERP.NickFinance.Web.dll`)
are pulled in transitively via the portal's project reference.

The portal reads `ConnectionStrings:NickFinance` from config:

```json
{
  "ConnectionStrings": {
    "NickFinance": "Host=127.0.0.1;Port=5432;Database=nickerp_nickfinance;Username=nscim_app;Password=$NICKSCAN_DB_PASSWORD;Pooling=true"
  }
}
```

If the connection string is missing, the portal boots without
NickFinance — the sidenav doesn't show finance entries and
`/api/nickfinance/*` returns 404. This is the documented "module not
deployed for this host" path (§11 design).

**Post-pilot — standalone host.** The shared-chrome wiring
(`AddNickErpNickFinanceSharedChrome` registers `ModuleId =
"nickfinance"` with the portal launcher URL) is in place for the
post-pilot deployment of `NickERP.NickFinance.Web` as a separate
service on port 5420. That standalone service has not yet been
provisioned; until it is, NickFinance always runs inside the portal.
Document any standalone deploy in §11 of this runbook when it lands.

## 4. Health checks

The portal exposes the standard `/healthz` endpoint per
[`01-deploy.md`](01-deploy.md) §6. NickFinance contributes no
module-specific check today — the platform-side `postgres-*`
health checks cover the `nickerp_nickfinance` DB connectivity (the
NickFinance DbContext registers with the same tenancy interceptors
the platform DBs use).

The relevant `/healthz/ready` checks:

- `postgres-nickfinance` — connectivity to `nickerp_nickfinance` via
  `nscim_app`. Fails with the same shape as
  `postgres-platform-identity` etc. — see
  [`02-secret-rotation.md`](02-secret-rotation.md) §5.
- `imaging-storage` / `plugin-registry` — not NickFinance-specific;
  these gate the portal as a whole.

**What good looks like.** All five Postgres-touching `/healthz/ready`
checks return `Healthy` within 5 s of process start. The
NickFinance-specific surface comes up via the Razor sidenav (the
portal's `NickFinanceFeatureFlag` service in
`apps/portal/Services/NickFinanceFeatureFlag.cs` reads the connection
string at boot and toggles the menu items accordingly).

**What bad looks like.** `postgres-nickfinance` Unhealthy = the
NickFinance DB isn't reachable. Most-common cause: stale
`NICKSCAN_DB_PASSWORD` or DB role drift. Path:
[`02-secret-rotation.md`](02-secret-rotation.md) §5. Less-common:
`nickerp_nickfinance` DB doesn't exist on this host — see §5 for
the create-DB path that runbook 07 §5.4 also covers.

A `/healthz/ready` Healthy with NickFinance pages still throwing in
the browser usually means the connection string is set but the
migrations haven't applied yet (the `nickfinance` schema is empty);
see §5.

## 5. Migrations

NickFinance has **two** EF migrations under
`modules/nickfinance/src/NickERP.NickFinance.Database/Migrations/`:

| Migration | Date | What it does |
|---|---|---|
| `20260429131827_Init_NickFinance` | 2026-04-29 | Creates the `nickfinance` schema, `petty_cash_boxes`, `petty_cash_vouchers`, `petty_cash_ledger_events`, `petty_cash_periods`, `fx_rate` tables + indexes |
| `20260429131858_Add_RLS_And_Grants` | 2026-04-29 | Adds `tenant_isolation_*` RLS policies (5 policies — one per tenant-owned table; `fx_rate` policy admits the `app.tenant_id = '-1'` system-context branch) + grants to `nscim_app` |

**Routine deploy-time apply.** Per [`01-deploy.md`](01-deploy.md)
§5.2, the portal's `Database.Migrate()` call on startup applies any
pending migrations. The migration is idempotent (the EF
`__EFMigrationsHistory` row gates re-application) so a redeploy with
no new NickFinance migrations is a no-op.

**First-time apply (live cutover).** Already covered by the Sprint 13
live-deploy in [`07-sprint-13-live-deploy.md`](07-sprint-13-live-deploy.md)
§5.4 (the staged `nickfinance.sql` script). That runbook is the path
to run for *any* fresh `nickerp_nickfinance` setup; it applies both
migrations + creates the role grants. **Do not** re-run if the live
DB has already had the script applied — runbook 07 §4.2 covers the
BEFORE-snapshot check.

**NickFinance prerequisites if the DB doesn't exist.** Per
[`07-sprint-13-live-deploy.md`](07-sprint-13-live-deploy.md) §3, the
`nickerp_nickfinance` database must be created out-of-band before
runbook 07 §5.4 can apply migrations:

```bash
psql -U postgres -d postgres -c "CREATE DATABASE nickerp_nickfinance;"
psql -U postgres -d nickerp_nickfinance -c "GRANT CONNECT ON DATABASE nickerp_nickfinance TO nscim_app;"
psql -U postgres -d nickerp_nickfinance -c "CREATE SCHEMA IF NOT EXISTS nickfinance;"
psql -U postgres -d nickerp_nickfinance -c "GRANT USAGE, CREATE ON SCHEMA nickfinance TO nscim_app;"
```

After the DB exists, hand off to runbook 07 §5.4 for the migration
apply; the rest of the live-deploy verification (§6.1, §6.2, §6.3)
covers NickFinance alongside the platform DBs.

**New migrations.** When a future sprint adds a NickFinance
migration, the `01-deploy.md` §5.2 routine flow handles it
automatically (portal startup runs `Database.Migrate()`); the
script-and-pipe flow from runbook 07 is only needed for a *first*
live deploy of an unmigrated DB or for an out-of-band DDL apply.

## 6. Backup posture

The `nickerp_nickfinance` DB is included in the v2 pgbackrest
stanza per [`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md).
**No NickFinance-specific stanza** — the cluster's pgbackrest config
treats every database in the Postgres cluster uniformly because
pgbackrest backs up at the cluster level, not per-DB.

Confirm `nickerp_nickfinance` is in the backup chain:

```bash
sudo -u postgres pgbackrest --stanza=nickerp info --output=json | \
  jq -r '.[0].db[].id'
# Returns the cluster-id; pgbackrest covers every DB in the cluster
# including nickerp_nickfinance. Per-DB backup verification is via
# the §8 quarterly drill (runbook 10).
```

**RPO posture.** With weekly fulls + 6-hourly incrementals + continuous
WAL archive, a NickFinance row created at T can be restored at T+6h
RPO without WAL replay, T+1m with WAL replay. The 14-day archive
window is the PITR floor.

**Quarterly drill scope.** Runbook 10 §8 covers `nickerp_inspection`
+ `nickerp_platform`; for NickFinance, the same drill restores the
`nickerp_nickfinance` DB alongside (since the cluster restores
together). Add a NickFinance-specific spot-check to the §8.4 sanity
queries:

```bash
# Schema is intact:
psql -U postgres -d nickerp_nickfinance -c '\dt+ nickfinance.*' | head

# Recent fx_rate row count is plausible:
psql -U nscim_app -d nickerp_nickfinance -c \
  'SELECT count(*) FROM nickfinance.fx_rate;'

# Recent voucher count:
psql -U nscim_app -d nickerp_nickfinance -c \
  'SELECT count(*) FROM nickfinance.petty_cash_vouchers;'

# RLS policies installed (expected: 5):
psql -U postgres -d nickerp_nickfinance -c \
  "SELECT count(*) FROM pg_policies
   WHERE schemaname = 'nickfinance'
     AND policyname LIKE 'tenant_isolation%';"
```

A drill that finds NickFinance rows missing while the inspection +
platform DBs are intact means the cluster-level backup excluded
`nickerp_nickfinance` — investigate the pgbackrest config; the
default config covers every DB but a misconfigured per-DB exclude
would manifest exactly this shape.

## 7. FX rate publishing

The FX-rate publish flow is the **only** NickFinance operation that
calls `ITenantContext.SetSystemContext()`. This is operationally
load-bearing — the publish path:

1. Captures the prior tenant id from the current context.
2. Calls `_tenant.SetSystemContext()` — flips the session into
   `app.tenant_id = '-1'` so the RLS WITH CHECK clause on
   `nickfinance.fx_rate` admits the suite-wide insert (rows have
   NULL `TenantId`).
3. Inserts or updates the rate row.
4. Emits a `nickfinance.fx_rate.published` audit event (one per
   rate row written).
5. Reverts to the prior tenant on completion (try / finally).

The caller is registered in
[`../system-context-audit-register.md`](../system-context-audit-register.md)
as the canonical NickFinance entry. **Adding any other
`SetSystemContext()` caller in NickFinance requires a register entry
+ user confirmation** — per the project's CLAUDE.md hard-rule §5,
weakening tenant isolation is one of the gated security postures.

**Permissions.** The endpoint POST `/api/nickfinance/fx-rates`
requires the `petty_cash.publish_fx` claim (constant in
`PettyCashRoles.PublishFx`). Today the role check is in-handler
(`IIdentityResolver.HasScope` reads the resolved scope set);
`MapPettyCashEndpoints` doesn't yet attach a policy. A future sprint
will graduate this to ASP.NET's policy infrastructure once the
identity-side role-policy contract lands.

**Audit trail.** Every publish writes:

- One `nickerp_nickfinance.nickfinance.fx_rate` row (insert OR update
  on conflict — re-publishing the same `(from_currency, to_currency,
  effective_date)` updates in place rather than duplicating).
- One `nickerp_platform.audit.events` row with
  `EventType='nickfinance.fx_rate.published'` and a payload that
  records the `actorUserId`, the rate triple, the prior value (if
  this was an update), and the source-of-truth string.

The audit event is the operator's primary triage tool when "the FX
rate looks wrong" — query `audit.events` filtered to that EventType
and the last 24 h to see what the publisher actually wrote.

**Common operational task — re-publish a rate.** See §8.1.

## 8. Common operational tasks

### 8.1 Re-publish an FX rate (correct a mis-typed rate)

Trigger: an operator published a wrong rate (typo, fat-finger, stale
source). Goal: correct the row in place and emit an audit event so
the trail of corrections survives.

Path: the endpoint POST `/api/nickfinance/fx-rates` is idempotent on
`(FromCurrency, ToCurrency, EffectiveDate)` — re-publishing the same
triple updates the existing row rather than inserting a duplicate.
The audit event records the prior value:

```bash
curl -X POST "http://localhost:5210/api/nickfinance/fx-rates" \
  -H "Authorization: Bearer $NICKERP_DEV_TOKEN" \
  -H "Content-Type: application/json" \
  -d '[
    { "fromCurrency": "USD", "toCurrency": "GHS", "effectiveDate": "2026-05-04", "rate": 14.85, "source": "BoG correction 2026-05-05" }
  ]'
```

Verify the update landed:

```bash
psql -U nscim_app -d nickerp_nickfinance -c \
  "SELECT from_currency, to_currency, effective_date, rate, source, updated_at
   FROM nickfinance.fx_rate
   WHERE from_currency='USD' AND to_currency='GHS'
   ORDER BY effective_date DESC LIMIT 5;"
```

And the audit event was emitted:

```bash
psql -U nscim_app -d nickerp_platform -c \
  "SELECT \"EventTime\", \"Payload\"
   FROM audit.events
   WHERE \"EventType\" = 'nickfinance.fx_rate.published'
     AND \"EventTime\" > NOW() - INTERVAL '1 hour'
   ORDER BY \"EventTime\" DESC LIMIT 5;"
```

### 8.2 Troubleshoot a stuck voucher disbursement

Trigger: a voucher is stuck in `Approved` and won't transition to
`Disbursed`. The portal page shows the disburse button but clicks
return an error.

Diagnostic flow:

```bash
# 1. Confirm the voucher state.
psql -U nscim_app -d nickerp_nickfinance -c \
  "SELECT id, state, amount, currency, period_year_month, requested_by_user_id, approved_at
   FROM nickfinance.petty_cash_vouchers
   WHERE id = '<voucher-id>';"
# Expected: state = 'Approved'

# 2. Confirm the voucher's period is open.
psql -U nscim_app -d nickerp_nickfinance -c \
  "SELECT year_month, status FROM nickfinance.petty_cash_periods
   WHERE year_month = (SELECT period_year_month
                       FROM nickfinance.petty_cash_vouchers
                       WHERE id = '<voucher-id>');"
# Expected: status = 'Open'. If 'Closed', that's the cause - see 8.3.

# 3. Look at the latest audit events for this voucher:
psql -U nscim_app -d nickerp_platform -c \
  "SELECT \"EventTime\", \"EventType\", \"Payload\"
   FROM audit.events
   WHERE \"Payload\" @> '{\"voucherId\": \"<voucher-id>\"}'
   ORDER BY \"EventTime\" DESC LIMIT 10;"
```

Common causes:
- Period is Closed → §8.3.
- Box float is exhausted (sum of disbursed-but-not-reconciled
  vouchers > box capacity) → reconcile a prior voucher first or
  top up the box.
- Caller lacks `petty_cash.approver` scope (the disburse transition
  re-checks the approver role) → grant via the identity-admin API.

### 8.3 Re-open a closed period to back-post

Trigger: an operator needs to post a back-dated voucher into a
period that has already been closed (e.g. a vendor invoice landed
in May for an April expense).

Permissions: requires `petty_cash.reopen_period` scope. Without it,
posting throws `PeriodLockedException`.

Path:

```bash
# Confirm the period exists + its status.
psql -U nscim_app -d nickerp_nickfinance -c \
  "SELECT year_month, status, closed_at, closed_by_user_id
   FROM nickfinance.petty_cash_periods
   WHERE year_month = '2026-04';"

# Re-open via the API (gated on petty_cash.reopen_period claim):
curl -X POST "http://localhost:5210/api/nickfinance/periods/2026-04/reopen" \
  -H "Authorization: Bearer $NICKERP_DEV_TOKEN"

# Verify status flipped:
psql -U nscim_app -d nickerp_nickfinance -c \
  "SELECT year_month, status FROM nickfinance.petty_cash_periods WHERE year_month='2026-04';"
# Expected: status = 'Open'
```

After the back-post lands, close the period again:

```bash
curl -X POST "http://localhost:5210/api/nickfinance/periods/2026-04/close" \
  -H "Authorization: Bearer $NICKERP_DEV_TOKEN"
```

The reopen + close emit `nickfinance.period.reopened` and
`nickfinance.period.closed` audit events; the back-post emits
`nickfinance.petty_cash.late_post` (because it lands during a
re-opened state — same shape as an in-period post but with the
late-post flag set so finance can audit which posts crossed the
period boundary).

### 8.4 Verify a tenant's base currency

Trigger: an inspection module call-out reports
`ITenantBaseCurrencyLookup` returning `USD` for a tenant that should
be `GHS`. Goal: confirm the seed row + cache state.

```bash
# Cross-DB lookup (the lookup is in apps/portal -> NickFinance.Web,
# but the data lives in nickerp_platform.tenancy.tenants).
psql -U postgres -d nickerp_platform -c \
  "SELECT id, name, base_currency FROM tenancy.tenants
   WHERE id = <tenant-id>;"
```

If the row's `base_currency` is correct but the lookup is returning
stale data, the `IMemoryCache` inside `TenantBaseCurrencyLookup` is
holding a stale value — restart the portal service to flush it.
(There is no operator-facing cache invalidation endpoint today; that's
a deferred follow-up tracked in `docs/sprint-progress.json` under
`FU-tenant-base-currency-cache-invalidate` if/when the operator
volume justifies it.)

## 9. Monitoring + alerts

NickFinance contributes no dedicated alerts today. The two relevant
v0 alerts are platform-side:

- **`postgres-nickfinance` Unhealthy** — fires P2 inside 4 h.
  Catches connection / role-drift on `nickerp_nickfinance`. Same
  response as runbook 02 §5.
- **`audit.events` projection backlog growing** — fires P2 if the
  audit projection worker is more than 30 s behind. NickFinance
  emits more audit events than most modules (one per voucher
  transition + one per FX publish + one per period flip), so a
  backlog in the projection worker hits NickFinance flows first.
  Path: investigate the projection worker (Sprint 8 P3) per the
  general projector runbook (deferred — currently no separate
  runbook, follow handoff
  `handoff-2026-04-29-image-analysis-session.md` §5).

**Module-specific monitoring deferred.** A NickFinance-specific
"voucher stuck in Approved > 24 h" alert is on the followup list
(`docs/sprint-progress.json` references this as
`FU-voucher-stuck-alert`); not v0.

## 10. Known limitations + post-pilot refactor scope

### 10.1 v0 limitations

- **No GL / AP / AR / Banking / Budgeting / CoA in v2-native.** Only
  petty-cash + FX + periods. The v1-flavoured side under
  `v1-clone/finance/` is operationally live (deploys via
  v1's NSCIM_PRODUCTION pipeline) but is *not* the same code as v2's
  G2 module. See §10.3.
- **In-handler role checks.** `MapPettyCashEndpoints` checks
  `petty_cash.publish_fx` etc. inside the handler rather than via
  ASP.NET policy attributes. Functionally equivalent at v0
  cardinality; graduates to policy infrastructure once Identity ships
  the role-policy contract.
- **Single-region.** The `nickerp_nickfinance` DB is in the same
  Postgres cluster as the platform + inspection DBs; no
  cross-region replica. Cross-region DR is post-pilot per ROADMAP
  §1 answer 3.
- **Portal-mounted only.** The standalone-host wiring is in place
  (`AddNickErpNickFinanceSharedChrome`) but the post-pilot 5420
  service has not been provisioned. Until then, NickFinance always
  ships in the portal binary.
- **No cache invalidation API.** The `TenantBaseCurrencyLookup`
  `IMemoryCache` is restart-only. Acceptable at v0 because tenant
  base-currency rotates rarely.

### 10.2 Auth + RLS posture

- **`fx_rate` RLS admits system context.** The
  `tenant_isolation_fx_rate` policy has an OR clause that admits
  `app.tenant_id = '-1'` for the suite-wide writes. This is the only
  RLS policy in NickFinance with a system-context branch; the four
  petty-cash tables are strictly per-tenant.
- **`nscim_app` is the runtime role.** Per
  [`02-secret-rotation.md`](02-secret-rotation.md), NickFinance reads
  + writes through `nscim_app` (`LOGIN NOSUPERUSER NOBYPASSRLS`). DDL
  goes through `postgres` only during the live-deploy (runbook 07).
- **Tenant interceptor.** The DbContext registration adds
  `TenantConnectionInterceptor` + `TenantOwnedEntityInterceptor` —
  the same shape as inspection. `app.tenant_id` is pushed on every
  connection open; the audit-register entry covers the SetSystemContext
  exception.

### 10.3 Post-pilot fold-in scope (~6-10 sprints)

Per ROADMAP §1 locked answer 7, the v1-clone NickFinance modules
(`v1-clone/finance/NickFinance.AP`, `NickFinance.AR`,
`NickFinance.Banking`, `NickFinance.Budgeting`, `NickFinance.Coa`,
plus tests) need to fold into v2-native modules over an estimated
6-10 post-pilot sprints. Until that fold-in starts:

- **Edits to v1-clone go via the `v1-clone/`-banner workflow** — see
  `v1-clone/README.md`. v1-clone is *not* deployed from this repo;
  it's a point-in-time clone for v2 design reference.
- **v2-native edits to G2 (this module) ship from
  `modules/nickfinance/`** — the path this runbook covers.
- **Don't merge or alias the two trees.** They share no imports;
  they share no DB. Petty-cash + FX + periods are exclusively
  v2-native; AP / AR / Banking / Budgeting / CoA / GL are
  exclusively v1-clone until the fold-in lands.

The fold-in arc per ROADMAP §1 answer 7:

| Pilot module (v1-clone) | Estimated sprints to fold-in | Notes |
|---|---|---|
| CoA (chart of accounts) | 2-3 | Foundation; AP/AR depend on it |
| Banking | 1-2 | Per-tenant bank account list + reconciliation |
| AP (accounts payable) | 2-3 | Vendor ledger; depends on CoA |
| AR (accounts receivable) | 2-3 | Customer ledger; depends on CoA |
| Budgeting | 1-2 | Period-bound expense plan; depends on CoA |
| GL roll-up | 1-2 | Aggregates AP + AR + Banking + Petty-cash to a single ledger |

Total: 9-15 sprints. The 6-10 figure in the ROADMAP is the
optimistic path; carry the longer estimate in operational planning.

## 11. References

- `ROADMAP.md` §1 (locked answer 7) — v1-clone fold-in arc.
- `ROADMAP.md` §G2 — the original NickFinance pathfinder design.
- [`07-sprint-13-live-deploy.md`](07-sprint-13-live-deploy.md) §5.4 —
  the staged `nickfinance.sql` script (initial DB + RLS apply).
- [`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
  — backup tool (covers `nickerp_nickfinance` alongside the platform
  + inspection DBs).
- [`01-deploy.md`](01-deploy.md) — portal-host deploy mechanics.
- [`02-secret-rotation.md`](02-secret-rotation.md) — `nscim_app`
  posture + DB password rotation.
- [`../MIGRATIONS.md`](../MIGRATIONS.md) — script-and-pipe pattern
  used in runbook 07 §5.4.
- [`../system-context-audit-register.md`](../system-context-audit-register.md)
  — `FxRatePublishService` SetSystemContext entry.
- `modules/nickfinance/src/NickERP.NickFinance.Core/Roles/PettyCashRoles.cs`
  — role-name constants.
- `v1-clone/README.md` — point-in-time clone of v1 NickFinance +
  NickHR; the v1-flavoured side §10.3 references.
- `v1-clone/finance/DEFERRED.md` — what's open in v1-clone;
  reading list for the fold-in arc.
