# NickFinance.Ledger — kernel contract

> Status (2026-04-26): rebuilt from scratch from the design in
> `docs/modules/NICKFINANCE_PLATFORM.md` §4.5 and the Phase 6 spike at
> `docs/modules/spikes/03-ledger-kernel-results.md`. **33/33 tests green
> against Postgres 18.** This is the kernel every NickFinance module
> (Petty Cash, AR, AP, Banking, Tax, Reporting) sits on top of.

The thinnest possible "general ledger" — an immutable, append-only event
log with a balance invariant enforced at the database. Modules **never**
mutate ledger rows directly. They build a `LedgerEvent`, hand it to
`ILedgerWriter.PostAsync(...)`, and the kernel does the rest.

---

## Layer split — three projects

| Project | What it ships | Reference |
|---|---|---|
| `NickFinance.Ledger` | `LedgerEvent`, `LedgerEventLine`, `AccountingPeriod`, `Money`, `LedgerWriter`, `LedgerReader`, `PeriodService`, `SchemaBootstrap`. Talks to the `finance` schema. | `new LedgerWriter(db)` |
| `NickFinance.Coa` | Chart of Accounts entities (`Account`, `AccountType`) + the Ghana SME default chart. Pure data, no DB plumbing. | `GhanaStandardChart.Default` |
| `NickFinance.Ledger.Tests` | xUnit suite — 14 Money tests + 11 DB-backed Writer tests + 3 property tests + 5 CoA shape tests. | `dotnet test` |

The **kernel does not depend on the CoA project**. Account codes are opaque strings to the writer — modules choose how strict to be about which codes they accept. The Ghana chart is a *reference* tenants opt into via their setup tooling; nothing in the Ledger requires it.

---

## Why event-sourced

```
ledger_events           — one row per atomic accounting fact
ledger_event_lines      — the legs of each event (debits + credits)
accounting_periods      — Open / SoftClosed / HardClosed
```

Three rules that flow from "events are facts, not state":

1. **No UPDATE, no DELETE.** Database triggers reject both at the row
   level. Corrections happen via a follow-up `Reversal` event — same
   accounts, debits/credits flipped — that nets the original to zero.
   A compromised service account cannot rewrite history.

2. **Reversals are just events.** The reader sums `dr - cr` over all
   events; reversal events naturally cancel their original. The reader
   has zero special cases for corrections.

3. **Idempotency is a hard DB constraint.** Every event carries an
   `idempotency_key` unique within a tenant. Re-posting the same key is
   a **no-op** that returns the original event id — retry-safe by
   design, not by convention.

---

## Invariants

| # | Where enforced | What |
|---|---|---|
| 1 | C# in `LedgerWriter.ValidateShape` | At least 2 lines |
| 2 | C# | Each line has exactly one of `DebitMinor` / `CreditMinor` non-zero |
| 3 | C# | All lines on a single event share one currency (multi-currency events are post-V1) |
| 4 | C# | `SUM(dr) == SUM(cr)` per event |
| 5 | **Postgres `ledger_events_balanced` constraint trigger (DEFERRABLE)** | Same balance check at commit time — belt-and-suspenders against any future code path that bypasses C# validation |
| 6 | C# in `LedgerWriter.ValidatePeriodAsync` | Effective date's period is not `HardClosed` |
| 7 | **Postgres `ledger_events_no_update` / `ledger_lines_no_update` triggers** | UPDATE and DELETE on `ledger_events` and `ledger_event_lines` are rejected with `'append-only'` in the message |
| 8 | C# (unique index `ux_ledger_events_tenant_idempotency`) | Idempotency key unique per tenant — re-post is a no-op |
| 9 | C# in `LedgerWriter.ReverseAsync` | Reversal target exists, is not itself a reversal, and isn't already reversed |

Five invariants enforced in C#, four enforced again at the DB. Tests cover
both layers — the DB-level append-only test directly executes
`UPDATE finance.ledger_events SET narration='hacked' ...` from raw SQL
and asserts it throws.

---

## Tables (`finance` schema)

### `ledger_events`

| column | type | notes |
|---|---|---|
| `event_id` | `uuid` PK | client-generated |
| `committed_at` | `timestamptz` | wall-clock UTC |
| `effective_date` | `date` | accounting date — drives period |
| `period_id` | `uuid` FK | restricted delete |
| `source_module` | `text(64)` | `'petty_cash'`, `'ar'`, `'ap'`, … |
| `source_entity_type` | `text(64)` | `'Voucher'`, `'Invoice'`, … |
| `source_entity_id` | `text(128)` | the upstream id |
| `idempotency_key` | `text(200)` | unique per tenant |
| `event_type` | `smallint` | `Posted=0`, `Reversal=1` |
| `reverses_event_id` | `uuid?` | for reversal events |
| `narration` | `text(500)` | free-text; required, default empty |
| `actor_user_id` | `uuid` | canonical identity-service id |
| `tenant_id` | `bigint` | always present |

Indexes: `(tenant_id, idempotency_key)` unique, `(tenant_id, period_id, effective_date)`, `(tenant_id, source_module, source_entity_type, source_entity_id)`, `reverses_event_id`.

### `ledger_event_lines`

| column | type | notes |
|---|---|---|
| `event_id` | `uuid` | composite PK part 1 |
| `line_no` | `smallint` | composite PK part 2; 1-based, stable order |
| `account_code` | `text(64)` | natural key into the chart of accounts |
| `debit_minor` | `bigint` | exactly one of debit/credit non-zero |
| `credit_minor` | `bigint` | … |
| `currency_code` | `char(3)` | ISO 4217, upper-case |
| `site_id` | `uuid?` | dimension |
| `project_code` | `text(64)?` | dimension |
| `cost_center_code` | `text(64)?` | dimension |
| `dims_extra` | `jsonb?` | open-ended dimensions; keep small |
| `description` | `text(500)?` | per-line narration |

Indexes: `(account_code, currency_code)`, `site_id`.

### `accounting_periods`

| column | type | notes |
|---|---|---|
| `period_id` | `uuid` PK | |
| `fiscal_year` | `int` | |
| `month_number` | `smallint` | 1..12 |
| `start_date` | `date` | inclusive |
| `end_date` | `date` | inclusive |
| `status` | `smallint` | `Open=0`, `SoftClosed=1`, `HardClosed=2` |
| `closed_at` | `timestamptz?` | last status transition |
| `closed_by_user_id` | `uuid?` | who closed it |
| `tenant_id` | `bigint` | |

Unique: `(tenant_id, fiscal_year, month_number)`.

---

## API surface

### Writing

```csharp
public interface ILedgerWriter
{
    Task<Guid> PostAsync(LedgerEvent evt, CancellationToken ct = default);

    Task<Guid> ReverseAsync(
        Guid originalEventId,
        Guid periodId,
        DateOnly effectiveDate,
        Guid actorUserId,
        string reason,
        string idempotencyKey,
        CancellationToken ct = default);
}
```

* `PostAsync` returns the (possibly pre-existing) event id. Re-posting
  the same `IdempotencyKey` returns the original — modules can retry
  on transient failures without thinking.
* `ReverseAsync` builds a flipped-leg event automatically; modules
  pass the *new* effective date / period (so a current-period reversal
  of a prior-period event lands cleanly).

### Reading

```csharp
public interface ILedgerReader
{
    Task<Money> GetAccountBalanceAsync(
        string accountCode, string currencyCode,
        DateOnly asOf, long tenantId = 1, CancellationToken ct = default);

    Task<IReadOnlyList<TrialBalanceRow>> GetTrialBalanceAsync(
        string currencyCode, DateOnly asOf,
        long tenantId = 1, CancellationToken ct = default);
}
```

V1 is intentionally minimal — account balance + trial balance are what
Petty Cash needs to ship. Module-specific projections (AR aging, AP
aging, cash position) are the consuming module's job; they read
`ledger_event_lines` directly with the dimensions they care about.

The Balance Sheet, P&L and cash-flow reports live in
`NickFinance.Reporting` (Phase 6.7), not the kernel. They read from
the same tables but slice by `Account.Type` from the chart of accounts —
which is why CoA stays out of the kernel.

### Periods

```csharp
public interface IPeriodService
{
    Task<AccountingPeriod> CreateAsync(int year, byte month, long tenantId = 1, CancellationToken ct = default);
    Task<AccountingPeriod?> GetByDateAsync(DateOnly date, long tenantId = 1, CancellationToken ct = default);
    Task<AccountingPeriod> SoftCloseAsync(Guid periodId, Guid actorUserId, CancellationToken ct = default);
    Task<AccountingPeriod> HardCloseAsync(Guid periodId, Guid actorUserId, CancellationToken ct = default);
}
```

Lifecycle is linear: `Open → SoftClosed → HardClosed`. Never backwards.
A `SoftClosed` period still accepts posts at the kernel layer; the
caller's authorisation check (e.g. `Finance.PeriodAdjust` scope) is what
enforces "only the controller may post here".

---

## Money

`(long Minor, string CurrencyCode)` value type. Pesewa for GHS, cents for
USD. Banker's rounding (`MidpointRounding.ToEven`) on every conversion
from major to minor. Cross-currency arithmetic throws
`InvalidOperationException` at the operator boundary — caught at compile
time you'd need to construct the Money first, but caught at runtime
before any DB write.

Three factories:

```csharp
Money.Zero("GHS")
Money.FromMinor(1995, "GHS")  // 19.95 GHS
Money.FromMajor(1.995m, "GHS") // banker-rounded → 1.99 GHS = 199 minor
```

`+`, `-`, unary `-` operators work — same currency or it throws.
`MultiplyRate(0.15m)` for VAT-style maths, banker's rounded.

---

## Bootstrapping the schema

EF Core migrations apply tables / FKs / indexes. **Postgres-level
invariants** (the constraint trigger and append-only triggers) are
applied via `SchemaBootstrap.ApplyConstraintsAsync(db)` which runs raw
SQL. This is intentional — EF can't model deferred constraint triggers
or `RAISE EXCEPTION`-on-UPDATE triggers cleanly. Run it once after
`EnsureCreated` (dev) or after `dotnet ef database update` (prod).

```csharp
await using var db = new LedgerDbContext(opts);
await db.Database.EnsureCreatedAsync();      // tables
await SchemaBootstrap.ApplyConstraintsAsync(db); // triggers
```

The bootstrap is idempotent: every CREATE TRIGGER is preceded by a
DROP TRIGGER IF EXISTS, every CREATE OR REPLACE FUNCTION is just that.
Re-running it after a failed migration is safe.

---

## Chart of accounts

`NickFinance.Coa` ships entities + the Ghana SME default. The kernel
does not require a CoA — it'll happily accept any string `account_code`.
Modules choose:

* **Petty Cash** validates against active `1060`, `2900`, `6400`, etc.
  via a CoA lookup before posting.
* **AR / AP** post to `1100` / `2000` (control accounts) by code; the
  CoA flag `IsControl=true` makes those accounts off-limits to free-form
  journals from the UI.
* **Setup tooling** seeds the chart by enumerating
  `GhanaStandardChart.Default` and inserting via the tenant's
  Accounts table.

The 70-row Ghana baseline covers Asset / Liability / Equity / Income /
COGS / OpEx / Finance cost / Tax expense / Suspense, with explicit
codes for every NickERP integration point — six-site cash, three MoMo
networks, three GHS banks, USD account, the four GRA tax payables
(VAT/NHIL/GETFund/COVID), WHT, PAYE, both SSNIT tiers, the petty-cash
control accounts, and AR/AP control accounts. See
`NickFinance.Coa/GhanaStandardChart.cs`.

---

## Tests

Run from `finance/NickFinance.Ledger.Tests/`:

```bash
export NICKFINANCE_TEST_DB="Host=localhost;Port=5432;Database=nickscan_finance_test;Username=postgres;Password=$NICKSCAN_DB_PASSWORD"
dotnet test -c Release
```

The fixture creates and drops the test database per run — no manual
`CREATE DATABASE` needed. Per-run seed defaults to `20260424` for
deterministic property-test repro; override with `NICKFINANCE_TEST_SEED`.

Last clean run:

```
Total tests: 33   Passed: 33   Failed: 0   Total time: 30.6s
```

Breakdown:

| Suite | Count | Of which DB-backed |
|---|---|---|
| `MoneyTests` | 14 | 0 |
| `LedgerWriterTests` | 11 | 11 |
| `LedgerPropertyTests` | 3 | 3 |
| **Total** | **33** | **14** |

Property-test breakdown:

* `Post_1000_RandomBalancedJournals_AllAccepted` — 1,000 random
  balanced journals; system-wide `SUM(dr) == SUM(cr)` after.
* `Post_500_RandomUnbalancedJournals_AllRejected` — 500 random
  unbalanced journals; all rejected with `UnbalancedJournalException`,
  zero rows reach the DB.
* `Post_100_DuplicateIdempotencyKeys_OnlyFirstPersisted` — 100
  triplets × same key, 300 attempted writes, 100 rows persisted.

---

## Performance (informational)

* 1,000 balanced journals serial-posted in ~18 seconds → **~55 journals/sec**
  on a single connection against local Postgres 18.
* Projected production load: ~2,000 journals/day = ~0.023/sec steady,
  with month-end bursts ≤ 50/sec. Comfortably within budget.

The kernel hasn't been concurrency-stressed yet (multiple writers at
high QPS). Flagged for the real implementation rollout — see *Open
contract questions* below.

---

## What this kernel does NOT do

Intentional non-goals:

* **Multi-currency in one event.** Currency is per-line but a single
  event's lines must agree. Cross-currency journals (e.g. an FX
  translation leg) come in V2 with explicit FX-rate tracking.
* **Per-record authorisation.** "Can Angela post to account 1100?" is
  the module's call. The kernel only enforces shape + balance + period.
* **Reports beyond Trial Balance.** Balance Sheet, P&L, cash-flow,
  GL-by-account-by-period live in `NickFinance.Reporting` (Phase 6.7).
* **Sub-ledgers.** AR aging, AP aging, customer/vendor balances belong
  to their owning module; they project from `ledger_event_lines` with
  module-specific filters.
* **Year-end close.** A regular journal that zeros out P&L accounts
  into `3900 Current-year P&L summary` and rolls into `3100 Retained
  earnings`. The closing module owns the workflow; the kernel just
  posts the events.
* **Approvals on journals.** A workflow concern, owned by the
  consuming module. The kernel writer is the *final* step after
  approvals already passed.

---

## Open contract questions

* [ ] **High-concurrency posting.** Spike stressed serial 50/sec; not
      yet tested at 500/sec with 10 writers. Add a load test before
      the first cross-module rollout (Phase 6.2 AR or 6.3 AP).
* [ ] **Multi-tenant isolation under concurrency.** Indices carry
      `tenant_id` everywhere but the cross-tenant boundary hasn't been
      stress-tested.
* [ ] **`SoftClosed` authorisation.** Kernel allows posts; relies on
      module-level `[Authorize(Roles="Finance.PeriodAdjust")]`. When
      the audit module lands, verify every soft-closed post carries an
      audit event.
* [ ] **Period rollover automation.** `PeriodService.CreateAsync` is
      idempotent and one-shot; no scheduled "open next month" job
      yet. A stub Hangfire job can take that on.
* [ ] **EF migrations vs `EnsureCreatedAsync`.** Tests use
      `EnsureCreatedAsync`. Production wants `dotnet ef database
      update` — add the initial migration + tooling when wiring this
      into a host (deferred ROADMAP item).
* [ ] **Wire into the live `nickhr` Postgres database.** The kernel
      runs against any Postgres; it'll join the existing
      production database under a `finance` schema once a host
      project (NickFinance.WebApp / NickFinance.API) hands it a
      connection string.

---

## Module layout

```
finance/NickFinance.Ledger/
├── Money.cs                                  — value type
├── Entities.cs                               — LedgerEvent, LedgerEventLine, AccountingPeriod, enums
├── Exceptions.cs                             — LedgerException family
├── LedgerDbContext.cs                        — EF Core mapping, finance schema
├── LedgerWriter.cs                           — ILedgerWriter + impl
├── LedgerReader.cs                           — ILedgerReader + TrialBalanceRow
├── PeriodService.cs                          — IPeriodService + impl
├── SchemaBootstrap.cs                        — Postgres trigger SQL
├── FINANCE_KERNEL.md                         — this file
└── NickFinance.Ledger.csproj

finance/NickFinance.Coa/
├── Account.cs                                — entity
├── AccountType.cs                            — enum + NormalBalance + extensions
├── GhanaStandardChart.cs                     — 70-row baseline
└── NickFinance.Coa.csproj

finance/NickFinance.Ledger.Tests/
├── LedgerFixture.cs                          — IAsyncLifetime, creates throwaway test DB
├── MoneyTests.cs                             — 14 unit tests
├── LedgerWriterTests.cs                      — 11 DB-backed tests
├── LedgerPropertyTests.cs                    — 3 property tests (1000 / 500 / 100)
└── NickFinance.Ledger.Tests.csproj
```

---

## Related docs

* `docs/modules/NICKFINANCE_PLATFORM.md` — the full Phase 6 plan; this
  kernel is §4.5 of that document.
* `docs/modules/PETTY_CASH.md` — first consuming module (Phase 6.1).
* `docs/modules/spikes/03-ledger-kernel-results.md` — original
  exploratory spike that proved the design (also 33/33 green).
* `ROADMAP.md` Phase 6 — module sequence and acceptance criteria.
