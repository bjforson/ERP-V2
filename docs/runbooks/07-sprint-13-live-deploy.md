# Runbook 07 — Sprint 13 live-deploy: applying staged migrations to the platform DBs

> **Scope.** First-ever live application of v2 EF migrations to the
> three platform Postgres databases (`nickerp_platform`,
> `nickerp_nickfinance`, `nickerp_inspection`). Five DbContexts in
> total, staged as five idempotent SQL artifacts under
> [`tools/migrations/sprint-13-deploy/`](../../tools/migrations/sprint-13-deploy).
> The operator runs the scripts manually, in order, with `psql`. This
> runbook does **not** apply anything itself.
>
> **Why a one-shot runbook.** v2 has shipped 12 sprints of EF
> migrations (G1-3, FU-2..FU-7, P1, P3, FU-deploy / FU-userid /
> FU-host-status / FU-icums-signing, G2, P2, Sprint 12 image-analysis
> integration, Sprint 13 T2 / T3) without a coordinated cutover. Most
> of those migrations have **never been applied to a live DB**. The
> exception is `nickerp_inspection`, where the parallel image-analysis
> session in Sprint 12 applied 5 migrations out of band (per
> [`handoff-2026-04-29-image-analysis-session.md`](handoff-2026-04-29-image-analysis-session.md)
> §3). The 5 idempotent scripts staged here close the gap.
>
> **Sister docs:**
> - [`../MIGRATIONS.md`](../MIGRATIONS.md) — the EF child-process
>   env-var quirk on Windows. The script-and-pipe pattern below is
>   the documented workaround.
> - [`01-deploy.md`](01-deploy.md) — routine deploy mechanics. The
>   present runbook is a **one-time predecessor** to the first deploy
>   of any service that talks to a platform DB; once the migrations
>   land, [`01-deploy.md`](01-deploy.md) §5.2's per-deploy migration
>   step takes over.
> - [`02-secret-rotation.md`](02-secret-rotation.md) — the
>   `nscim_app` posture this runbook restores at the end.
> - [`reference_tools_migration_runner.md`](https://example.invalid)
>   — v1's pattern (memory file, not a v2 doc) for `psql`-less
>   environments. Not used here; v2 assumes `psql` on the box.

---

## 1. Symptom

You're staging the first live-deploy of v2's accumulated schema. There
is no symptom — this runbook is a **planned change** path. Use it
when:

- A v2 service (`apps/portal`, `modules/inspection/src/NickERP.Inspection.Web`,
  or any future module web/host) is about to be brought up on the live
  host for the first time.
- A `Database.Migrate()` call on host startup would otherwise apply
  ~37 migrations across 3 databases unattended — which is exactly
  what we are explicitly avoiding by going through the script-and-pipe
  flow.
- An audit of `__EFMigrationsHistory` reveals divergence between the
  source-tree migration list and the per-context history table — the
  scripts staged here re-converge them safely (idempotent guards
  skip whatever is already applied).

If a host is currently throwing
`relation "audit.events" does not exist` or
`relation "inspection.cases" does not exist`, you're not staging —
you're recovering an aborted deploy. The scripts and the order below
still apply, but execute them with the urgency of a P1.

## 2. Severity

| Trigger | Severity | Response window |
|---|---|---|
| Routine staged apply (planned cutover) | n/a — operator-initiated | as scheduled |
| Recovery after partial apply (transaction aborted mid-script) | P2 | inside 4 h |
| Recovery after host crashed mid-`Database.Migrate()` | P1 | inside 30 min |
| Wrong DB targeted by accident (e.g., script run against `nickerp_platform` for an inspection migration) | P1 | inside 30 min, restore from backup |

The scripts are **idempotent**: re-running any of them against a DB
that has already had the migrations applied is a no-op. So a P2
recovery is mostly "figure out which one didn't finish, re-run it,
verify". The P1 paths are the ones that touched the wrong DB or
crashed mid-statement.

## 3. Quick triage (60 seconds)

Before you start, answer:

- **Are the staged scripts at the expected commit?** They live at
  `tools/migrations/sprint-13-deploy/{tenancy,identity,audit,nickfinance,inspection}.sql`.
  If they're missing, regenerate from
  [`MIGRATIONS.md`](../MIGRATIONS.md) §"Generating + applying the
  migrations" — the same `dotnet ef migrations script --idempotent`
  command works.
- **Is the DB role rotated correctly?** The runtime app role is
  `nscim_app` (`LOGIN NOSUPERUSER NOBYPASSRLS`). DDL is run as
  `postgres` (the superuser). If `NICKSCAN_DB_PASSWORD` for
  `nscim_app` is stale, fix that first — the runbook assumes the
  app role is in good shape so the post-checks reconnect cleanly.
- **Do the three DBs exist?** `nickerp_platform`, `nickerp_inspection`,
  and `nickerp_nickfinance`. Of these, `nickerp_nickfinance` was
  added in G2 (Sprint 10) and may be missing on a host that
  predates that sprint — see [`MIGRATIONS.md`](../MIGRATIONS.md)
  §"NickFinance prerequisites" before running the nickfinance
  script.
- **Is anyone else applying?** Two operators running the scripts in
  parallel against the same DB will produce arbitrary failures (e.g.,
  one's `INSERT INTO __EFMigrationsHistory` racing the other's). One
  operator at a time.

## 4. Diagnostic commands

All commands assume bash (Git Bash on Windows). PowerShell
equivalents differ only in env-var syntax. Use forward slashes in
arguments; if a path contains spaces, quote it.

### 4.1 Confirm DB existence + connect-as-app

```bash
psql -U postgres -d postgres -c \
  "SELECT datname FROM pg_database
   WHERE datname IN ('nickerp_platform', 'nickerp_inspection',
                     'nickerp_nickfinance')
   ORDER BY datname;"
# Expected: 3 rows.

PGPASSWORD="$NICKSCAN_DB_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
  -U nscim_app -d nickerp_platform -c "SELECT 1;"
PGPASSWORD="$NICKSCAN_DB_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
  -U nscim_app -d nickerp_inspection -c "SELECT 1;"
PGPASSWORD="$NICKSCAN_DB_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
  -U nscim_app -d nickerp_nickfinance -c "SELECT 1;"
# Expected: each returns 1. A 28P01 means rotate
# NICKSCAN_DB_PASSWORD before continuing — see runbook 02 §5.
```

If `nickerp_nickfinance` is missing, follow
[`MIGRATIONS.md`](../MIGRATIONS.md) §"NickFinance prerequisites"
to create it (`CREATE DATABASE`, role grants) before running the
nickfinance script.

### 4.2 Snapshot the per-context migration history (BEFORE picture)

The five contexts each maintain a private `__EFMigrationsHistory`
table inside their schema. Snapshot each:

```bash
psql -U postgres -d nickerp_platform -c \
  'SELECT "MigrationId" FROM tenancy."__EFMigrationsHistory"
    ORDER BY "MigrationId";' 2>&1 | tee /tmp/before-tenancy.txt

psql -U postgres -d nickerp_platform -c \
  'SELECT "MigrationId" FROM identity."__EFMigrationsHistory"
    ORDER BY "MigrationId";' 2>&1 | tee /tmp/before-identity.txt

psql -U postgres -d nickerp_platform -c \
  'SELECT "MigrationId" FROM audit."__EFMigrationsHistory"
    ORDER BY "MigrationId";' 2>&1 | tee /tmp/before-audit.txt

psql -U postgres -d nickerp_nickfinance -c \
  'SELECT "MigrationId" FROM nickfinance."__EFMigrationsHistory"
    ORDER BY "MigrationId";' 2>&1 | tee /tmp/before-nickfinance.txt

psql -U postgres -d nickerp_inspection -c \
  'SELECT "MigrationId" FROM inspection."__EFMigrationsHistory"
    ORDER BY "MigrationId";' 2>&1 | tee /tmp/before-inspection.txt
```

If any of the four platform / nickfinance schemas is missing, the
query will return `relation "<schema>"."__EFMigrationsHistory" does
not exist` — that's expected on a never-migrated DB. The first run
of the script creates the schema + history table.

The expected BEFORE state on a host that has only had Sprint 12's
parallel-session work applied:

| Schema | Rows in `__EFMigrationsHistory` BEFORE |
|---|---|
| `tenancy` | 0 (relation may not exist) |
| `identity` | 0 (relation may not exist) |
| `audit` | 0 (relation may not exist) |
| `nickfinance` | 0 (relation may not exist) |
| `inspection` | 5 — the Sprint 12 image-analysis applied set |

The 5 inspection rows expected in BEFORE:

```
20260429062458_Add_PhaseR3_TablesInferenceModernization
20260429063951_Cleanup_StaleScanRenderArtifactHistoryRow
20260429064022_Drop_PublicEFMigrationsHistory
20260429123406_Add_IcumsSigningKeys
20260429140000_BootstrapScannerThresholdProfilesV0
```

If the inspection row count is non-zero but the IDs differ from the
above five, **stop** and reconcile — the scripts assume the Sprint 12
applied set or a clean state. Anything in between is custom.

### 4.3 Snapshot RLS policies (BEFORE picture)

```bash
psql -U postgres -d nickerp_platform -c \
  "SELECT schemaname, tablename, policyname FROM pg_policies
   WHERE policyname LIKE 'tenant_isolation%'
     AND schemaname IN ('audit', 'identity', 'tenancy')
   ORDER BY schemaname, tablename;" 2>&1 | tee /tmp/before-policies-platform.txt

psql -U postgres -d nickerp_inspection -c \
  "SELECT schemaname, tablename, policyname FROM pg_policies
   WHERE policyname LIKE 'tenant_isolation%'
   ORDER BY schemaname, tablename;" 2>&1 | tee /tmp/before-policies-inspection.txt

psql -U postgres -d nickerp_nickfinance -c \
  "SELECT schemaname, tablename, policyname FROM pg_policies
   WHERE policyname LIKE 'tenant_isolation%'
   ORDER BY schemaname, tablename;" 2>&1 | tee /tmp/before-policies-nickfinance.txt
```

Expected BEFORE policy counts on a host with only Sprint 12 inspection
applied:

| DB | Schema | tenant_isolation_* policies BEFORE |
|---|---|---|
| `nickerp_platform` | `tenancy` | 0 |
| `nickerp_platform` | `identity` | 0 |
| `nickerp_platform` | `audit` | 0 |
| `nickerp_nickfinance` | `nickfinance` | 0 |
| `nickerp_inspection` | `inspection` | 19 (already applied through `Add_IcumsSigningKeys`) |

### 4.4 Verify the staged scripts

```bash
cd "C:/Shared/ERP V2"
ls -la tools/migrations/sprint-13-deploy/
# Expected:
#   tenancy.sql       ~166 lines
#   identity.sql      ~446 lines
#   audit.sql         ~547 lines
#   nickfinance.sql   ~376 lines
#   inspection.sql   ~1778 lines
```

If the worktree on the live box doesn't have these files, copy from
the merged `main` (or pull on the deploy worktree) — they are
artifacts checked into the source tree, not generated on the box.

### 4.5 Backup verification

**Mandatory.** Before running anything, take a full backup of all
three DBs and verify it is restorable. The runbook does not include
a rollback that recovers from a corrupt apply — restoring from
backup is the only path back from "the script ran but the schema is
wrong".

```bash
TS=$(date +%Y%m%d-%H%M%S)
mkdir -p "/c/Shared/Backups/$TS"

PGPASSWORD="$POSTGRES_SUPERUSER_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/pg_dump.exe' \
  -U postgres -d nickerp_platform -Fc \
  -f "/c/Shared/Backups/$TS/nickerp_platform.dump"

PGPASSWORD="$POSTGRES_SUPERUSER_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/pg_dump.exe' \
  -U postgres -d nickerp_inspection -Fc \
  -f "/c/Shared/Backups/$TS/nickerp_inspection.dump"

PGPASSWORD="$POSTGRES_SUPERUSER_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/pg_dump.exe' \
  -U postgres -d nickerp_nickfinance -Fc \
  -f "/c/Shared/Backups/$TS/nickerp_nickfinance.dump"

ls -la "/c/Shared/Backups/$TS/"
# Expected: three .dump files of non-zero size.
```

Do **not** proceed if `pg_dump` failed for any of the three DBs.

## 5. Resolution — applying the scripts

> **Hard rule.** The scripts are run as **`postgres`** (the
> superuser). `nscim_app` does not have DDL grants on most of the
> objects these migrations create, and the `Add_NscimAppRole_Grants`
> migrations themselves grant on tables that didn't exist yet. This
> mirrors v1's posture (`reference_tools_migration_runner.md` —
> "`nscim_app` cannot run DDL").
>
> The host that connects post-apply runs as `nscim_app`. §6 verifies
> that posture is restored.

The five scripts are applied **in this order**:

| Step | Script | DB | Why this order |
|---|---|---|---|
| 5.1 | `tenancy.sql` | `nickerp_platform` | Defines no policies (the `tenants` root table stays unprotected) but adds the schema + history primitives every other context's RLS policy implicitly assumes. |
| 5.2 | `identity.sql` | `nickerp_platform` | Identity rows reference tenants conceptually (TenantId column on every tenant-owned table). Not a hard FK across schemas, but identity is the second-most-foundational schema after tenancy. |
| 5.3 | `audit.sql` | `nickerp_platform` | The `AddSystemContextOptInToEvents` migration in this script (`20260429061910`) lands the `current_setting('app.tenant_id') = '-1'` opt-in clause that NickFinance's RLS policies in 5.4 assume exists for `audit.events`. |
| 5.4 | `nickfinance.sql` | `nickerp_nickfinance` | `FxRatePublishService` (G2) writes audit events under a system-context fan-out; the policy shape it assumes was minted in 5.3. Apply NickFinance after audit. |
| 5.5 | `inspection.sql` | `nickerp_inspection` | Largest blast radius (24 tenant-owned tables), most plugins. Apply last so a partial earlier-step failure doesn't stop the platform-DB cutover behind it. The Sprint 12 partial-applied state means 5 of 14 migrations no-op on first run. |

The connection-string knob the host reads is `Username=nscim_app`,
not `postgres`. The dev-loop shortcut in
[`MIGRATIONS.md`](../MIGRATIONS.md) §"Dev-cycle shortcut" is
**only** for the script-and-pipe flow — never bake `postgres` into
an `appsettings.json`. The post-apply check in §6.4 catches this.

### 5.1 Apply tenancy.sql to nickerp_platform

```bash
cd "C:/Shared/ERP V2"

PGPASSWORD="$POSTGRES_SUPERUSER_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
  -U postgres -d nickerp_platform \
  -v ON_ERROR_STOP=1 \
  -f tools/migrations/sprint-13-deploy/tenancy.sql 2>&1 \
  | tee /tmp/apply-tenancy.log
```

Expected output: a stream of `INSERT 0 1` (the
`__EFMigrationsHistory` row writes) plus the schema-creation `DO`
blocks. `ON_ERROR_STOP=1` aborts on the first error so a partial
apply is visible immediately.

The script contains 4 migration blocks; on a clean DB all 4 apply.
On a re-run the `IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '...')`
guard wrapping each block makes the second run a no-op.

### 5.2 Apply identity.sql to nickerp_platform

```bash
PGPASSWORD="$POSTGRES_SUPERUSER_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
  -U postgres -d nickerp_platform \
  -v ON_ERROR_STOP=1 \
  -f tools/migrations/sprint-13-deploy/identity.sql 2>&1 \
  | tee /tmp/apply-identity.log
```

5 migration blocks; on a clean DB all 5 apply.

### 5.3 Apply audit.sql to nickerp_platform

```bash
PGPASSWORD="$POSTGRES_SUPERUSER_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
  -U postgres -d nickerp_platform \
  -v ON_ERROR_STOP=1 \
  -f tools/migrations/sprint-13-deploy/audit.sql 2>&1 \
  | tee /tmp/apply-audit.log
```

12 migration blocks; on a clean DB all 12 apply. Includes
`Drop_PublicEFMigrationsHistory` (`20260429064002`) which is
**irreversible** — see §7.

### 5.4 Apply nickfinance.sql to nickerp_nickfinance

```bash
PGPASSWORD="$POSTGRES_SUPERUSER_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
  -U postgres -d nickerp_nickfinance \
  -v ON_ERROR_STOP=1 \
  -f tools/migrations/sprint-13-deploy/nickfinance.sql 2>&1 \
  | tee /tmp/apply-nickfinance.log
```

2 migration blocks; on a clean DB both apply.

### 5.5 Apply inspection.sql to nickerp_inspection

```bash
PGPASSWORD="$POSTGRES_SUPERUSER_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
  -U postgres -d nickerp_inspection \
  -v ON_ERROR_STOP=1 \
  -f tools/migrations/sprint-13-deploy/inspection.sql 2>&1 \
  | tee /tmp/apply-inspection.log
```

14 migration blocks total. On the post-Sprint-12 host:

- 5 already-applied skip silently via the `IF NOT EXISTS` guards:
  - `20260429062458_Add_PhaseR3_TablesInferenceModernization`
  - `20260429063951_Cleanup_StaleScanRenderArtifactHistoryRow`
  - `20260429064022_Drop_PublicEFMigrationsHistory`
  - `20260429123406_Add_IcumsSigningKeys`
  - `20260429140000_BootstrapScannerThresholdProfilesV0`
- 9 actually execute:
  - `20260426105303_Initial_AddInspectionSchema`
  - `20260426171815_Add_CaseLifecycle_And_LocationAssignments`
  - `20260427164855_Add_ScanRenderArtifact`
  - `20260427211653_Add_RLS_Policies`
  - `20260427220330_Add_ScanRenderAttempt`
  - `20260427221059_Add_NscimAppRole_Grants`
  - `20260428104221_AddRuleEvaluations`
  - `20260428130909_Grant_NscimApp_CreateOnSchema`
  - `20260430111020_Add_PostHocOutcomeAdapter`

Wait — that ordering is misleading. The 9 that "execute" includes
8 that **predate** the Sprint 12 applied set in time. They appear
to apply because they have no row in `__EFMigrationsHistory`. The
script will create their schema artifacts as if from scratch.

**Important:** If the `nickerp_inspection` DB already has the
schema artifacts from migrations earlier than the Sprint 12 set
(e.g., `inspection.cases` already exists because a host previously
ran `Database.Migrate()`), the script will fail with `relation
"inspection.cases" already exists`. The Sprint 12 handoff
(§3, §97) says only the 5 listed migrations were applied; on
inspection, that means the **schema-creating earlier migrations
were applied too** as transitive prerequisites. A clean run of
`migrations script --idempotent` only guards via
`__EFMigrationsHistory` rows, not via DDL existence.

**Pre-flight for §5.5.** Before running, confirm whether the host
is genuinely in a "5-rows-only" state or a "5-rows-plus-pre-existing-schema"
state:

```bash
psql -U postgres -d nickerp_inspection -c \
  "SELECT count(*) FROM information_schema.tables
   WHERE table_schema = 'inspection';"
```

| Result | Meaning | Action |
|---|---|---|
| 0 | Schema is empty (despite 5 migration rows) | Stop. The 5 rows are corrupt — investigate. |
| ~5–10 | Only the Sprint 12 R3 + ICUMS tables exist | Stop. The earlier migrations must have been applied; recover their `__EFMigrationsHistory` rows manually before re-running 5.5. |
| ~28+ | Full pre-Sprint-13 inspection schema is present | The 5 rows in history ≠ what's actually applied; **the rows must be reconciled with reality before running 5.5**. See §5.5a below. |

#### 5.5a Reconciling a `nickerp_inspection` with full schema but only 5 history rows

If the table count above is ≥28, the schema-creating migrations
ran (probably via a `dotnet ef database update` from the
image-analysis session) but the history rows for them are missing
from your snapshot. **Most likely they're actually present and
§4.2 picked them up correctly** — re-run §4.2 and confirm. If
you genuinely have schema artifacts without history rows, do
**not** apply `inspection.sql` blindly — the
`CREATE TABLE` inside each migration will fail. Instead:

```bash
# For each pre-existing migration whose schema is already in place,
# stamp the history row by hand. Confirm with the operator before
# inserting; this is the "I'm telling EF this migration is already
# done" path.
psql -U postgres -d nickerp_inspection -c \
  "INSERT INTO inspection.\"__EFMigrationsHistory\"
   (\"MigrationId\", \"ProductVersion\")
   VALUES ('20260426105303_Initial_AddInspectionSchema', '10.0.7');"
# ... repeat per missing row ...
```

Then re-run §5.5; the now-stamped rows skip cleanly.

This branch of the runbook is unlikely to fire on the canonical
path (the Sprint 12 applied set per the handoff doc is exactly the
5 listed). It's documented because the cost of getting it wrong is
"abort and restore from §4.5 backup", which is expensive.

### 5.6 Apply confirmation

After 5.1–5.5 succeed:

```bash
grep -E "^(INSERT 0 1|ERROR)" /tmp/apply-*.log | sort | uniq -c | sort -rn
# Expected:
#   <N> INSERT 0 1
#   0 ERROR  (no error rows)
```

If any `ERROR` rows appear, jump to §7 (Aftermath / Postmortem).

## 6. Verification

In this order:

### 6.1 Migration history populated

Re-run §4.2's queries. Expected AFTER state:

| Schema | DB | Rows in `__EFMigrationsHistory` AFTER |
|---|---|---|
| `tenancy` | `nickerp_platform` | 4 |
| `identity` | `nickerp_platform` | 5 |
| `audit` | `nickerp_platform` | 12 |
| `nickfinance` | `nickerp_nickfinance` | 2 |
| `inspection` | `nickerp_inspection` | 14 |

`__EFMigrationsHistory` row delta = (AFTER − BEFORE):
- tenancy: 4 − 0 = **4 applied**
- identity: 5 − 0 = **5 applied**
- audit: 12 − 0 = **12 applied**
- nickfinance: 2 − 0 = **2 applied**
- inspection: 14 − 5 = **9 applied** (5 skipped as Sprint 12 pre-existing)

**Total fresh migrations applied: 32.**

### 6.2 Tables exist

```bash
psql -U postgres -d nickerp_platform -c '\dt+ tenancy.*'
psql -U postgres -d nickerp_platform -c '\dt+ identity.*'
psql -U postgres -d nickerp_platform -c '\dt+ audit.*'
psql -U postgres -d nickerp_nickfinance -c '\dt+ nickfinance.*'
psql -U postgres -d nickerp_inspection -c '\dt+ inspection.*'
```

Sanity-check expected table counts (rough, not authoritative):

| Schema | Approximate table count after apply |
|---|---|
| `tenancy` | 1 (the `tenants` root table) + `__EFMigrationsHistory` |
| `identity` | 5 (`identity_users`, `app_scopes`, `user_scopes`, `service_token_identities`, `service_token_scopes`) + history |
| `audit` | 6 (`events`, `notifications`, `projection_checkpoints`, `edge_node_authorizations`, `edge_node_replay_log`, `edge_node_api_keys`) + history |
| `nickfinance` | 5 (`petty_cash_boxes`, `petty_cash_vouchers`, `petty_cash_ledger_events`, `petty_cash_periods`, `fx_rate`) + history |
| `inspection` | ~28 (the full inspection domain — cases, scans, scan_artifacts, … — plus the 5 R3 tables and ICUMS keys) + history |

A short-by-one count is usually a missing migration that didn't
apply; investigate by comparing §4.2's BEFORE / AFTER snapshots.

### 6.3 RLS policies installed

Re-run §4.3's queries. Expected AFTER policy counts:

| DB | Schema | tenant_isolation_* policies AFTER |
|---|---|---|
| `nickerp_platform` | `tenancy` | 0 (intentional — `tenants` root unprotected) |
| `nickerp_platform` | `identity` | 5 |
| `nickerp_platform` | `audit` | 3 (`events`, `notifications`, `edge_node_api_keys`) |
| `nickerp_nickfinance` | `nickfinance` | 5 |
| `nickerp_inspection` | `inspection` | 24 |
| **Total** | | **37** |

Check that FORCE RLS is on for every policied table:

```bash
psql -U postgres -d nickerp_platform -c \
  "SELECT n.nspname, c.relname, c.relrowsecurity, c.relforcerowsecurity
   FROM pg_class c JOIN pg_namespace n ON c.relnamespace = n.oid
   WHERE n.nspname IN ('audit', 'identity', 'tenancy')
     AND c.relkind = 'r'
   ORDER BY n.nspname, c.relname;"

psql -U postgres -d nickerp_inspection -c \
  "SELECT n.nspname, c.relname, c.relrowsecurity, c.relforcerowsecurity
   FROM pg_class c JOIN pg_namespace n ON c.relnamespace = n.oid
   WHERE n.nspname = 'inspection' AND c.relkind = 'r'
   ORDER BY c.relname;"

psql -U postgres -d nickerp_nickfinance -c \
  "SELECT n.nspname, c.relname, c.relrowsecurity, c.relforcerowsecurity
   FROM pg_class c JOIN pg_namespace n ON c.relnamespace = n.oid
   WHERE n.nspname = 'nickfinance' AND c.relkind = 'r'
   ORDER BY c.relname;"
```

Every tenant-owned table should show `t / t` (rowsecurity on,
forcerowsecurity on). The `__EFMigrationsHistory` and `tenants`
tables are expected to show `f / f`.

### 6.4 Restore minimal-privilege state

The scripts ran as `postgres`. The host must reconnect as
`nscim_app`. Confirm via the standard pattern from
[`02-secret-rotation.md`](02-secret-rotation.md) §5.6:

```bash
psql -U postgres -d postgres -c \
  "SELECT rolname, rolsuper, rolbypassrls
   FROM pg_roles WHERE rolname = 'nscim_app';"
# Expected: super=f, bypassrls=f.
```

After bringing the host up (the next step after this runbook —
[`01-deploy.md`](01-deploy.md) §5.5):

```bash
psql -U postgres -d nickerp_inspection -c \
  "SELECT DISTINCT usename, application_name
   FROM pg_stat_activity
   WHERE datname = 'nickerp_inspection' AND state IS NOT NULL;"
# Expected: every row has usename = nscim_app.
```

If you see `postgres` in the connected-as column, the host has the
dev-shortcut connection string baked in — fix the connection string
and restart before declaring the cutover done.

### 6.5 Smoke through the host

After §6.4, hand off to [`01-deploy.md`](01-deploy.md) §5.0 to
bring up the supervised services. The host's startup log line on
a clean migration apply (everything already present) is:

```
info: Startup.Migrations[0]
      Migrations applied for Identity, Audit, Tenancy, Inspection.
```

`/healthz/ready` should be `Healthy` for all five checks within
5 s of process start.

## 7. Rollback notes

The scripts are idempotent on the **forward** path. Going backward
is migration-specific and largely "unsupported" — most v2 migrations
have intentionally-empty `Down()` methods, and a few are explicitly
**irreversible**.

### 7.1 Irreversible migrations called out

The following migrations have `Down() = no-op` by intent. Once
applied, they can only be rolled back by restoring the §4.5
backup. Do not assume `dotnet ef database update <previous-id>`
will recover them.

| Migration | Schema | Effect that cannot be undone |
|---|---|---|
| `20260429063951_Cleanup_StaleScanRenderArtifactHistoryRow` | inspection | `DELETE FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427164643_Add_ScanRenderArtifact'` (Sprint 6 / FU-4). Re-INSERTing the orphan row would require fabricating a synthetic `ProductVersion`. |
| `20260429064022_Drop_PublicEFMigrationsHistory` (inspection) | inspection | `DROP TABLE IF EXISTS public."__EFMigrationsHistory"` (Sprint 6 / FU-6). Pre-H3 history is gone. |
| `20260429064002_Drop_PublicEFMigrationsHistory` (audit) | audit | Same drop, applied to `nickerp_platform`. The audit-context migration handles all three platform schemas (audit, identity, tenancy) because they share the same DB. |

These three together are the **IRREVERSIBLE** set. They are safe on
the forward path (idempotent: `IF EXISTS` guards make re-running a
no-op). Forward + then-restore-from-backup is the only round-trip
shape.

### 7.2 Migrations with non-trivial Down() (informational)

The remaining migrations across all 5 contexts mostly have
either auto-generated `Down()` methods (which EF builds from the
inverse of the `Up()`) or empty no-op `Down()`s. The non-trivial
ones worth knowing about:

| Migration | Schema | Down() shape | Notes |
|---|---|---|---|
| `20260427211653_Add_RLS_Policies` (inspection) | inspection | DROP POLICY + ALTER TABLE NO FORCE / DISABLE RLS | Reverses cleanly. |
| `20260427211743_Add_RLS_Policies` (identity) | identity | Same shape | Reverses cleanly. |
| `20260427211843_Add_RLS_Policies` (tenancy) | tenancy | No-op (no policies) | Already a no-op. |
| `20260427211851_Add_RLS_Policies` (audit) | audit | Same shape | Reverses cleanly. |
| `20260428104421_RemoveRlsFromIdentityUsers` (identity) | identity | Re-creates the dropped policy | Effectively a forward-only patch on the `identity_users` table specifically. |
| `20260428194409_Make_TenantId_Nullable` (audit) | audit | Marks NOT NULL again | Reverse fails if any NULL rows landed (which the system-context opt-in started writing). |
| `20260429061910_AddSystemContextOptInToEvents` (audit) | audit | Reverts the policy USING/WITH CHECK clauses | Reverses, but downstream NickFinance G2 RLS policies assume the opt-in shape — **rolling back audit without rolling back nickfinance is unsafe**. |
| `20260429114858_Promote_Notifications_UserIsolation_To_Rls` (audit) | audit | Demotes back to LINQ-only isolation | Reverses, but the `Notification` host code (Sprint 8 P3 projector) expects RLS — host will keep working but isolation drops to single-tenant defaults. |
| `20260429131858_Add_RLS_And_Grants` (nickfinance) | nickfinance | Drops policies + grants | Reverses cleanly; the nickfinance host won't start without it (host fails closed if RLS isn't enforcing). |

### 7.3 Hard rollback ("the cutover is wrong, restore the world")

Restore from the §4.5 dumps:

```bash
PGPASSWORD="$POSTGRES_SUPERUSER_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/dropdb.exe' \
  -U postgres nickerp_platform
PGPASSWORD="$POSTGRES_SUPERUSER_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/createdb.exe' \
  -U postgres nickerp_platform
PGPASSWORD="$POSTGRES_SUPERUSER_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/pg_restore.exe' \
  -U postgres -d nickerp_platform \
  -v "/c/Shared/Backups/$TS/nickerp_platform.dump"
# Repeat for nickerp_inspection + nickerp_nickfinance.
```

This is the **only** path back from "the inspection script ran
to completion but the schema is wrong". Treat it as a P1.

## 8. Known not-applied delta on `nickerp_inspection`

On the canonical path (post-Sprint-12 host), `inspection.sql`
**will not apply all 14 migrations** — it will apply 9 fresh and
silently no-op on 5 already-applied. This is by design of
`--idempotent` and is not a failure mode.

The 5 already-applied migrations:

```
20260429062458_Add_PhaseR3_TablesInferenceModernization
20260429063951_Cleanup_StaleScanRenderArtifactHistoryRow
20260429064022_Drop_PublicEFMigrationsHistory
20260429123406_Add_IcumsSigningKeys
20260429140000_BootstrapScannerThresholdProfilesV0
```

This is documented in
[`handoff-2026-04-29-image-analysis-session.md`](handoff-2026-04-29-image-analysis-session.md)
§3 — the parallel image-analysis session in Sprint 12 ran
`dotnet ef database update` against `nickerp_inspection` with
those five migrations applied (some of them — `Cleanup_Stale...`,
`Drop_PublicEFMigrationsHistory`, `Add_IcumsSigningKeys` — were
authored by the rolling-master session but applied to the live
DB by the parallel session). The audit register in the next
runbook entry should record this as the canonical "out-of-band
apply" precedent the team is closing out.

After §6.1 confirms the inspection history has 14 rows, the
divergence is closed and future deploys can use the routine
[`01-deploy.md`](01-deploy.md) §5.2 flow (only deploy-time
deltas, not the full backlog).

## 9. Aftermath

### 9.1 Postmortem template

Even on a clean run, log the cutover. On a failed run, this is
mandatory:

```
## Sprint 13 live-deploy: <YYYY-MM-DD HH:MM>
- Outcome: success | partial-applied (which) | rolled-back
- Pre-apply __EFMigrationsHistory snapshot:
  tenancy=<n>, identity=<n>, audit=<n>, nickfinance=<n>, inspection=<n>
- Post-apply __EFMigrationsHistory snapshot:
  tenancy=<n>, identity=<n>, audit=<n>, nickfinance=<n>, inspection=<n>
- Pre-apply tenant_isolation_* policy count: <n>
- Post-apply tenant_isolation_* policy count: <n>
- Time per script (5.1 .. 5.5): <s, s, s, s, s>
- Backup taken at: /c/Shared/Backups/<ts>/
- Anomalies (if any):
- Followups filed: <CHANGELOG.md / open-issue links>
```

### 9.2 Who to notify

Single-engineer system today: capture the cutover in `CHANGELOG.md`
under a new dated bullet, and update `docs/sprint-progress.json` to
reflect the live-deploy slug as `done`. The next deploy should use
[`01-deploy.md`](01-deploy.md) §5 unmodified.

## 10. References

- [`../MIGRATIONS.md`](../MIGRATIONS.md) — script-and-pipe pattern,
  `nscim_app` posture, EF child-process env-var quirk.
- [`01-deploy.md`](01-deploy.md) — routine deploy mechanics; this
  runbook hands back to that one once the platform / nickfinance /
  inspection DBs are migrated.
- [`02-secret-rotation.md`](02-secret-rotation.md) §5.6 — restore
  minimal-privilege check, mirrored in §6.4 above.
- [`handoff-2026-04-29-image-analysis-session.md`](handoff-2026-04-29-image-analysis-session.md)
  §3 — the Sprint 12 partial-applied state the inspection script
  closes out.
- [`handoff-2026-04-29-rolling-master-session.md`](handoff-2026-04-29-rolling-master-session.md)
  §4 — the "live-deploy smoke is also missing" callout that prompted
  Sprint 13 T1.
- [`../system-context-audit-register.md`](../system-context-audit-register.md)
  — the four `SetSystemContext()` callers whose RLS opt-ins are
  among the policies installed by the audit + nickfinance + inspection
  scripts.
- [`../../tools/migrations/sprint-13-deploy/`](../../tools/migrations/sprint-13-deploy)
  — the five staged scripts referenced by §5.
- [`../../PLAN.md`](../../PLAN.md) — Sprint 13 T1 origin (live-deploy
  migration backlog staging).
