# NickFinance.Database.Bootstrap — wiring NickFinance into Postgres

> Status (2026-04-26): **dry-run verified**. The CLI applies EF migrations
> for both Ledger and Petty Cash, applies the Postgres triggers, and
> reports CoA load — 7 tables + 2 trigger functions in 3 seconds against
> a fresh database.

This is the operator-run tool for putting the NickFinance schemas onto
a real Postgres database. The same CLI runs against:

* a developer's local box for try-it,
* a CI-disposable test database,
* the live `nickhr` database **once** at production rollout.

It is **idempotent** — re-running it against an already-bootstrapped
database is a no-op. EF migration history is checked, triggers are
re-installed via `DROP IF EXISTS` + `CREATE`.

---

## What it touches

| Schema | Tables | Source |
|---|---|---|
| `finance` | `ledger_events`, `ledger_event_lines`, `accounting_periods` | `NickFinance.Ledger` |
| `petty_cash` | `floats`, `vouchers`, `voucher_line_items`, `voucher_approvals` | `NickFinance.PettyCash` |

Plus two trigger functions in `finance`:

* `finance.ledger_events_balanced_fn` — deferred constraint trigger,
  re-checks `SUM(debit_minor) = SUM(credit_minor)` per `event_id` at
  commit time.
* `finance.ledger_reject_mutation_fn` — fires before any UPDATE / DELETE
  on `ledger_events` or `ledger_event_lines` and raises an exception
  containing the literal string `'append-only'` so application code can
  match on it.

The CLI does **not** create the database itself — that's the DBA's
job — and does **not** seed the chart of accounts as a database row
yet. A future Phase 6.2 (AR) commit lands the `accounts` table when a
consumer requires persistent CoA validation. Today, modules read
`NickFinance.Coa.GhanaStandardChart.Default` directly from code.

---

## Local / CI run

```bash
# Create an empty database
PGPASSWORD="$NICKSCAN_DB_PASSWORD" psql -h localhost -p 5432 -U postgres -d postgres \
  -c "CREATE DATABASE nickerp_finance_local;"

# Apply
cd /c/Shared/NSCIM_PRODUCTION/finance/NickFinance.Database.Bootstrap
dotnet run -- \
  --conn "Host=localhost;Port=5432;Database=nickerp_finance_local;Username=postgres;Password=$NICKSCAN_DB_PASSWORD" \
  --seed-coa
```

The `--seed-coa` flag is currently a no-op except for the
`Loaded 74 CoA rows in-memory` confirmation. Drop it for now.

---

## Live `nickhr` DB rollout (production)

> ⚠️ **Read before running.** This adds two schemas to the database that
> hosts NickHR's payroll, employees, leave, and disciplinary data. The
> migrations only `CREATE TABLE` inside the **new** schemas (`finance`,
> `petty_cash`); they don't touch any existing NickHR table. The trigger
> functions are scoped to the `finance` schema and don't fire on any
> NickHR row. None the less — back up first, run in a maintenance
> window, verify before rolling forward.

### 0. Pre-flight (do this on your laptop first)

Take a logical dump of `nickhr` so you have a guaranteed point to roll
back to:

```powershell
$ts = Get-Date -Format 'yyyyMMddHHmmss'
& "C:\Program Files\PostgreSQL\18\bin\pg_dump.exe" `
  -h localhost -p 5432 -U postgres -d nickhr -F custom -f "C:\Backups\nickhr-pre-finance-$ts.dump"
```

This is in addition to whatever the nightly `NickERP_PgBackup_Nightly`
job (`scripts\pg-backup.ps1`) produces. Keep both.

### 1. Confirm there are no schema collisions

```sql
-- These should both return 0 rows. If they don't, a previous bootstrap
-- attempt failed half-way; fix that before continuing.
SELECT schema_name FROM information_schema.schemata
 WHERE schema_name IN ('finance', 'petty_cash');

SELECT routine_schema, routine_name FROM information_schema.routines
 WHERE routine_schema = 'finance';
```

### 2. Run the bootstrap

```bash
# From the production repo on TEST-SERVER (or whichever host has the DB tools)
cd /c/Shared/NSCIM_PRODUCTION/finance/NickFinance.Database.Bootstrap
dotnet run -c Release -- \
  --conn "Host=localhost;Port=5432;Database=nickhr;Username=postgres;Password=$NICKSCAN_DB_PASSWORD"
```

Expected output:

```
[1/4] Applying Ledger migrations (finance schema)...
       Ledger: up to date.
[2/4] Applying Petty Cash migrations (petty_cash schema)...
       Petty Cash: up to date.
[3/4] Applying Postgres triggers (balance invariant + append-only)...
       Triggers: applied.
[4/4] Seeding Ghana standard chart of accounts...
       Loaded 74 CoA rows in-memory.
Bootstrap complete. The Ledger + Petty Cash schemas are ready.
```

If any step throws, re-read the stack trace — the CLI does not silence
errors. Roll back from the dump in §0 and investigate before retrying.

### 3. Smoke test against the live DB

```sql
\c nickhr

-- Should list 3 + 4 tables.
\dt finance.*
\dt petty_cash.*

-- Should list 2 trigger functions.
\df finance.*

-- Should be empty.
SELECT * FROM finance.ledger_events;
SELECT * FROM petty_cash.vouchers;

-- The migration history table is in each schema.
SELECT migration_id FROM finance.__ef_migrations_history;
SELECT migration_id FROM petty_cash.__ef_migrations_history;
```

### 4. Post-bootstrap

Nothing in NickHR knows about NickFinance yet. The schemas exist but
no NickHR code path touches them. The first consumer arrives when the
NickFinance host project ships (`NickFinance.WebApp` — Phase 6.1
section §6 of the spec). Until then the new tables sit idle.

### Rollback

If you need to undo a bootstrap before any data lands:

```sql
\c nickhr
DROP SCHEMA petty_cash CASCADE;
DROP SCHEMA finance CASCADE;
```

If data already landed, restore from the dump in §0 — NickFinance
journals are append-only by trigger, so a `DELETE` won't get you back.

---

## What the CLI is not (yet)

* No data-seeding hook beyond the in-memory CoA load. When the AR
  module needs a persisted accounts table, that migration ships with
  the AR module and the CLI gains a `--seed-coa` step that actually
  INSERTs.
* No Tally journal sync (Phase 6.9).
* No e-VAT credentials provisioning (separate concern).
* No backup-before-migrate guard. The CLI assumes the operator already
  ran §0.

These are deferred — the CLI is intentionally minimal so the DBA can
read it end to end before running it.

---

## Related docs

* `finance/NickFinance.Ledger/FINANCE_KERNEL.md` — kernel contract.
* `finance/NickFinance.PettyCash/PETTY_CASH_MVP.md` — first consumer
  module.
* `docs/modules/NICKFINANCE_PLATFORM.md` — Phase 6 plan.
* `scripts/pg-backup.ps1` — nightly backup that writes to
  `C:\Backups\Postgres\` and is the recovery floor for everything in
  this DB.
