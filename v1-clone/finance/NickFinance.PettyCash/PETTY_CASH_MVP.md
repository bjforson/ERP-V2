# NickFinance.PettyCash — MVP-zero contract

> Status (2026-04-26): **first consumer of the GL kernel ships**. 14/14
> tests green against Postgres 18 — full chain `Create Float → Submit →
> Approve → Disburse → Journal posted` exercised end-to-end.

This module is intentionally the *thinnest* slice of the Petty Cash spec
(`docs/modules/PETTY_CASH.md`). Its job is to prove the GL kernel is
genuinely consumable by a real module — not to be a finished product.
Everything beyond the chain above is deferred to subsequent versions
(see *Deferred*).

---

## What ships

A class library `NickFinance.PettyCash` that exposes one service interface:

```csharp
public interface IPettyCashService
{
    Task<Float>   CreateFloatAsync(Guid siteId, Guid custodianUserId, Money initialFloat, Guid actorUserId, long tenantId = 1, CancellationToken ct = default);
    Task<Voucher> SubmitVoucherAsync(SubmitVoucherRequest req,                            CancellationToken ct = default);
    Task<Voucher> ApproveVoucherAsync(Guid voucherId, Guid approverUserId, long? amountApprovedMinor, string? comment, CancellationToken ct = default);
    Task<Voucher> RejectVoucherAsync(Guid voucherId, Guid approverUserId, string reason,  CancellationToken ct = default);
    Task<Voucher> DisburseVoucherAsync(Guid voucherId, Guid custodianUserId, DateOnly effectiveDate, Guid periodId, CancellationToken ct = default);
}
```

…and three persistent entities: `Float`, `Voucher`, `VoucherLineItem`.

### Voucher state machine

```
                         ┌── ApproveVoucherAsync ──► Approved ── DisburseVoucherAsync ──► Disbursed (terminal)
SubmitVoucherAsync ──►   │
                         └── RejectVoucherAsync  ──► Rejected (terminal)
```

`Draft` exists as a state but isn't reachable from `SubmitVoucherAsync` —
the v1 service goes straight to `Submitted`. A later UI may persist
drafts before submit; the column is reserved so we don't have to migrate.

---

## What the kernel proves through this module

The disbursement step is where the kernel is consumed:

```csharp
// Inside PettyCashService.DisburseVoucherAsync, simplified:
var ledgerEvent = new LedgerEvent {
    SourceModule = "petty_cash",
    SourceEntityType = "Voucher",
    SourceEntityId = voucher.VoucherId.ToString("N"),
    IdempotencyKey = $"petty_cash:{voucher.VoucherId:N}:disburse",
    EventType = LedgerEventType.Posted,
    Narration = $"Petty cash disbursement {voucher.VoucherNo}: {voucher.Purpose}",
    ActorUserId = custodian,
    EffectiveDate = effective,
    PeriodId = period
};

// DR each expense line at its category-default GL account:
foreach (var line in voucher.Lines)
    ledgerEvent.Lines.Add(new LedgerEventLine {
        AccountCode = line.GlAccount,         // e.g. "6300" Travel
        DebitMinor  = line.GrossAmountMinor,
        CurrencyCode = "GHS",
        ProjectCode = voucher.ProjectCode
    });

// CR petty-cash float:
ledgerEvent.Lines.Add(new LedgerEventLine {
    AccountCode = "1060",                     // Petty cash float
    CreditMinor = voucher.AmountApprovedMinor!.Value,
    CurrencyCode = "GHS"
});

var eventId = await _ledger.PostAsync(ledgerEvent);
voucher.LedgerEventId = eventId;
voucher.Status = VoucherStatus.Disbursed;
```

The kernel handles everything from there — balance check, period lock,
idempotency, append-only invariant. If the period is hard-closed the
post throws `ClosedPeriodException`, the voucher stays `Approved`, and
the operator can fix the period and retry.

The `EndToEnd_…_PostsBalancedJournal` test verifies:

* Balances move correctly: `6300` += GHS 250, `1060` -= GHS 250.
* The journal is balanced (sum debits == sum credits).
* The voucher carries the journal id (`Voucher.LedgerEventId`).
* The journal carries the voucher id (`source_entity_id`).

That's the full handshake.

---

## Schema (`petty_cash` in the host database)

| Table | Purpose |
|---|---|
| `petty_cash.floats` | One active row per (`tenant`, `site`, `currency`); closed floats stack underneath via the partial unique index. |
| `petty_cash.vouchers` | One row per voucher; carries `voucher_no`, requester, category, amounts, status, timestamps, the optional `ledger_event_id`. |
| `petty_cash.voucher_line_items` | Lines within a voucher; each has its own GL account so a single voucher can split across categories. Cascades on voucher delete (only Drafts can be deleted; everything past Submitted is immutable status-wise though the row itself can be edited until Approved, which is fine for v1). |

Nothing else in MVP-zero. Approvals are stored inline on the voucher
(`decided_by_user_id`, `decision_comment`, `decided_at`); the dedicated
`voucher_approvals` table from the spec is **deferred** until multi-step
routing arrives.

### Indexes

* `ux_floats_active_per_site_currency` — partial unique on `is_active = TRUE`, prevents two open floats for the same site/currency
* `ux_vouchers_tenant_voucher_no` — voucher numbers are tenant-unique
* `ix_vouchers_tenant_float_status` — fast custodian queue ("what's pending on my float?")
* `ix_vouchers_tenant_requester_status` — fast requester queue ("what's the status of my open requests?")
* `ux_voucher_lines_voucher_lineno`

---

## Invariants the service enforces

| # | Where | What |
|---|---|---|
| 1 | `CreateFloatAsync` | Initial float ≥ 0; one active per (site, currency, tenant). |
| 2 | `SubmitVoucherAsync` | Purpose required; amount > 0; ≥ 1 line; **lines sum exactly to voucher amount**; lines share voucher currency; float is active and same currency. |
| 3 | `ApproveVoucherAsync` | State must be `Submitted`; **requester ≠ approver** (separation of duties); approved amount ≤ requested amount and > 0. |
| 4 | `RejectVoucherAsync` | State must be `Submitted`; reason required; **requester ≠ approver**. |
| 5 | `DisburseVoucherAsync` | State must be `Approved`; approved amount > 0; **approver ≠ disbursing custodian** (separation of duties); approved amount **must equal** requested amount in MVP-zero (partial-approval pro-rata is deferred). |
| 6 | (kernel side) | The journal must balance per currency, the period must not be hard-closed, and the idempotency key is unique — kernel rejects with the appropriate `LedgerException` and the voucher status is left untouched. |

---

## Default GL account by category

The 5-category enum maps to 5 codes in the Ghana standard chart of
accounts (`NickFinance.Coa.GhanaStandardChart`). Line items can override
per-line.

| Category | Default GL | Description |
|---|---|---|
| `Transport` | `6300` | Travel |
| `Fuel` | `6310` | Vehicle running |
| `OfficeSupplies` | `6410` | Office supplies |
| `StaffWelfare` | `6900` | Other operating expense |
| `Emergency` | `6400` | Petty cash expense — general |

Disbursement always credits **`1060` Petty cash float**.

---

## Tests (14/14 green, ~10s)

```bash
export NICKFINANCE_TEST_DB="Host=localhost;Port=5432;Database=nickscan_finance_test;Username=postgres;Password=$NICKSCAN_DB_PASSWORD"
dotnet test finance/NickFinance.PettyCash.Tests -c Release
```

The fixture creates `nickscan_pettycash_test` (rewriting whatever DB
name was passed), provisions the **Ledger** schema first (so its
triggers fire on subsequent posts) plus the **Petty Cash** schema, and
tears the DB down on dispose. Both contexts share the connection
string — proving the production wiring will work the same way.

Test breakdown:

| Group | Tests | Focus |
|---|---|---|
| **Happy path** | 1 | Full chain Float→Submit→Approve→Disburse→Ledger |
| **Float invariants** | 2 | Second active float per site rejected; negative initial rejected |
| **Submit shape** | 4 | Line-total mismatch / mixed-currency / zero-amount / closed-float rejected |
| **Approve / reject** | 3 | SoD on requester=approver; double-approve rejected; reject records reason |
| **Disburse** | 3 | SoD on approver=custodian; closed-period leaves voucher Approved (retry-safe); double-disburse rejected, ledger has exactly one event |
| **CoA mapping** | 1 | Every category maps to a 4-digit GL code |

---

## Deferred (one or more follow-up sessions)

What MVP-zero deliberately doesn't do:

- **Multi-step approval policy DSL.** v1 does single-approver. The YAML
  policy in `PETTY_CASH.md §5.1` and the matching `voucher_approvals`
  table land in v1.1 alongside delegation + escalation.
- **Receipts + OCR.** No `voucher_receipts` table, no upload, no Azure
  Form Recognizer. Receipts are an attachment problem; the journal
  doesn't depend on them.
- **Mobile-money disbursement.** v1 just flips the voucher status; the
  Hubtel `/api/disburse/momo` integration through NickComms.Gateway
  ships separately. Custodian still records the cash hand-off offline.
- **Tax engine.** No VAT/NHIL/GETFund/COVID/WHT split per line item.
  Lines carry gross amount only; the tax engine (Phase 6.5) will
  add columns and split journals per the Ghana compound formula
  without breaking the existing voucher contract.
- **Float top-ups, replenishment workflow.** The `replenish_threshold_pct`
  field is wired but no service / report uses it yet.
- **Cash counts + reconciliation.** No daily count entries, no
  variance journal, no monthly reconciliation. Belongs in v1.2 with
  audit + fraud signals.
- **Budgets.** No per-site / per-category caps, no consumption
  tracking, no 80%-warning trigger.
- **Fraud signals F1–F8.** Salami slicing, ghost payee, duplicate
  receipt pHash, GPS mismatch, Benford anomalies — none yet.
- **Notifications.** No email / SMS / WhatsApp on submit / approve /
  reject / disburse. A future event-bus integration handles this.
- **Blazor Server UI.** No `/petty-cash/...` pages. v1.1 ships the
  pages; v1 ships the contract.
- **Vendors registry.** No vendor master; payee is free text.
- **Offline / PWA mode.** Border sites still go through the online
  flow.
- **Reports.** No spend dashboard, no variance reports, no aging.
- **CSV export.** No accountant-friendly export; the Ledger detail
  query is the only audit-trail surface.

---

## Module layout

```
finance/NickFinance.PettyCash/
├── Entities.cs                    — Float, Voucher, VoucherLineItem, enums
├── Exceptions.cs                  — InvalidVoucherTransitionException, SeparationOfDutiesException, …
├── PettyCashDbContext.cs          — petty_cash schema EF mapping
├── PettyCashService.cs            — IPettyCashService + impl (the only API surface)
├── PETTY_CASH_MVP.md              — this file
└── NickFinance.PettyCash.csproj

finance/NickFinance.PettyCash.Tests/
├── PettyCashFixture.cs            — IAsyncLifetime, provisions both schemas in one DB
├── PettyCashServiceTests.cs       — 14 end-to-end tests
└── NickFinance.PettyCash.Tests.csproj
```

---

## Open questions for v1.1

* [ ] **Voucher-number race.** `GenerateVoucherNoAsync` does a count-then-format
  inside the same transaction; two concurrent submits can collide and the
  unique constraint catches it. A monotonic per-site sequence (Postgres
  sequence per site) or `nextval()` plus a lookup table would be cleaner.
* [ ] **Partial approvals.** Disburse currently rejects part-approvals
  to keep the journal-line scaling simple. Should a partial approval
  pro-rata each line? Or require an admin to re-create the voucher with
  the smaller amount? Decision needed before the policy DSL lands.
* [ ] **Cancellation.** Spec calls for a `Cancelled` terminal state with
  admin override + reason. Not in the v1 enum yet; add when the admin
  scope arrives.
* [ ] **Float currency conversion.** A USD float at Aflao would post in
  USD but we'd want a GHS-equivalent restated journal for consolidation.
  Defer to multi-currency phase.

---

## Related docs

* `docs/modules/PETTY_CASH.md` — full 14-week spec; this MVP slice is
  the first 3-4 weeks of v1.0.
* `docs/modules/NICKFINANCE_PLATFORM.md` — Phase 6.1 sequence.
* `finance/NickFinance.Ledger/FINANCE_KERNEL.md` — kernel contract this
  module consumes.
* `finance/NickFinance.Coa/GhanaStandardChart.cs` — source of the GL
  codes the module's category map points at.
