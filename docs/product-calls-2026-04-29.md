# Product Calls 2026-04-29 — G2 + P2 + Followup Queue

This doc captures the user calls received via the post-Sprint-8 drain
(after Sprints 5–8 closed `8c1ed0a`). Where a call materially constrains
a spec, the **Decision** line is binding on the next master that picks
the work item up. Follow-up answers can amend lines here; nothing is
set in stone.

Cross-referenced from `PLAN.md` §20.

---

## 1. G2 — NickFinance Petty Cash domain shape (gating cleared)

Six product calls captured. With these answers G2 is no longer gated;
it joins the drainable backlog.

### 1.1 Money primitive

- **Decision.** C# value-object record `Money(decimal Amount, string CurrencyCode)`,
  `decimal(18,4)` backing, currency-aware arithmetic at the type level
  (operators reject mismatched currencies).
- ISO 4217 codes (`"GHS"`, `"USD"`, `"EUR"`).
- EF value-converter; needs a `Money` JSON shape for audit-event payloads.

### 1.2 Currency carrying

- **Decision.** Per-petty-cash-box. Each `Box` has a fixed `CurrencyCode`;
  `Voucher.CurrencyCode` is denormalised from `Box.CurrencyCode` for query
  ergonomics but the box owns the source of truth. Multi-currency tenants
  open multiple boxes (one per currency).
- A voucher cannot mix currencies; arithmetic across boxes goes through
  the FX layer (§1.10).

### 1.3 Negatives

- **Decision.** `Money.Amount` is always positive (`>= 0` invariant on
  the record). Direction is carried by a `LedgerDirection` enum on the
  ledger event — `Debit` (cash leaving box) / `Credit` (cash entering
  box) / `Adjust` (reconciliation difference; either sign via paired
  rows).

### 1.4 Voucher state machine

- **Decision.** `Request → Approve → Disburse → Reconcile`. Plus terminal
  exits `Rejected` (from `Request`) and `Cancelled` (from `Request` or
  `Approve` only — once disbursed, only a Reconcile-Adjust can correct).
- Cash moves on Disburse. That's the only state transition that emits a
  Debit ledger event.
- Reconcile = receipts attached, difference (if any) resolved by an
  Adjust event.

### 1.5 Approver

- **Decision.** Per-box designated approver. `Box` has a `CustodianUserId`
  AND an `ApproverUserId`, both required at box creation, must be
  different (DB CHECK constraint).
- Voucher.Approve transition requires the Approver. Voucher.Disburse
  requires the Custodian.
- Every approval emits an `inspection.voucher.approved` audit event
  carrying `ApproverUserId` for separation-of-duties review.

### 1.6 Ledger event shape

- **Decision.** Single-entry per money-movement now; double-entry GL
  projection later. Schema:

  | column | type | notes |
  |---|---|---|
  | id | uuid PK | |
  | tenant_id | bigint | RLS-isolated |
  | box_id | uuid FK | |
  | voucher_id | uuid FK NULL | null for replenishment events |
  | event_type | text | `disburse` / `refund` / `replenish` / `adjust` |
  | direction | text | `debit` / `credit` / `adjust` |
  | amount_native | numeric(18,4) | always positive |
  | currency_native | text | ISO 4217 |
  | amount_base | numeric(18,4) | converted to tenant base currency |
  | currency_base | text | tenant.base_currency_code |
  | fx_rate | numeric(18,8) | rate applied at posted_at::date |
  | fx_rate_date | date | which day's rate was used |
  | posted_at | timestamptz | |
  | posted_by_user_id | uuid | |
  | corrects_event_id | uuid NULL | for reversals |

- Append-only. No UPDATE on existing rows.
- Reversals = new row with opposite direction, `corrects_event_id`
  pointing at the original.
- Future projection: a separate `ledger_gl_projection` table synthesizes
  paired debit/credit rows against a Chart of Accounts when GL
  integration ships. Out of scope for the G2 pathfinder sprint.

### 1.7 Period locks

- **Decision.** Soft lock with admin override.

  | column | type | notes |
  |---|---|---|
  | tenant_id | bigint | |
  | period_year_month | text | e.g. `2026-04` |
  | closed_at | timestamptz NULL | NULL = open |
  | closed_by_user_id | uuid NULL | |

- A period is closed when `closed_at IS NOT NULL`.
- Posting a ledger event with `posted_at` inside a closed period throws
  unless the user has the `petty_cash.reopen_period` role (a new role
  to plumb).
- Every late post emits `petty_cash.late_post` audit event with the
  period + the actor. Re-close is a single audit event.

### 1.8 FX conversion shape

- **Decision.** Snapshot at event time + tenant base currency.
- Every ledger event carries BOTH (`amount_native`, `currency_native`)
  AND (`amount_base`, `currency_base`), computed using the FX rate at
  `posted_at::date`.
- Reports use `amount_base` for cross-currency aggregation; historical
  reports stay stable when rates move.
- New column `tenant.base_currency_code text` (default `'GHS'`).

### 1.9 Audit register entry (G2 will need it)

- The G2 module will likely need `SetSystemContext()` callers for
  cross-tenant FX-rate publication (§1.10). Each MUST land in
  `docs/system-context-audit-register.md` with the paired RLS opt-in
  clause, per the discipline established in Sprint 5.

### 1.10 FX rate authority

- **Decision.** Daily manual publish by finance admin, suite-wide
  (NOT per-tenant).

  | column | type | notes |
  |---|---|---|
  | tenant_id | bigint NULL | NULL = suite-wide |
  | from_currency | text | |
  | to_currency | text | |
  | rate | numeric(18,8) | |
  | effective_date | date | |
  | published_at | timestamptz | |
  | published_by_user_id | uuid | |

  PK = (`from_currency`, `to_currency`, `effective_date`).

- Publishing happens via a small admin page run by the finance admin
  under `SetSystemContext()` (one-row write to a NULL-tenant row),
  since rates are suite-wide. Adds an entry to
  `docs/system-context-audit-register.md` and an RLS opt-in clause on
  `fx_rate`.
- Per-event lookup: ledger writes look up `fx_rate WHERE from_currency
  = box.currency AND to_currency = tenant.base_currency AND
  effective_date <= posted_at::date ORDER BY effective_date DESC
  LIMIT 1`. Missing rate for the day means the ledger write fails
  fast — surface "rates not yet published" to the user.
- No external feed for v0. A future enhancement could add a
  Bank-of-Ghana / ECB pull as a separate publisher worker.

---

## 2. P2 — Edge node conflict resolution (gating cleared)

- **Decision.** Server-authoritative. Edge node operates as a
  write-buffer when offline:
  1. Edge writes go into a local SQLite buffer
     `edge_outbox(event_payload jsonb, edge_timestamp timestamptz, replayed_at timestamptz NULL)`.
  2. On reconnect, the edge replays buffered events to the server. Each
     replayed event keeps its `edge_timestamp` as the canonical
     `posted_at` value.
  3. Server processes them as fresh appends — same code path as live
     events.
  4. Conflicts dissolve naturally because nothing in the core schema is
     mutable from the edge side. The append-only audit posture
     (Sprint 5 + §1.6 above) makes this work.
- **What edge nodes CAN do offline.** Read recent state (cached
  reference data: scanners, locations, plugin configs). Capture
  scan-ingestion events, voucher-disbursement events, audit events.
- **What they CAN'T.** Edit existing rows. Delete anything. Post events
  with future timestamps (server rejects).
- **Replay ordering.** Per-edge-node FIFO. Different edges interleave
  by their own `edge_timestamp` — server doesn't try to merge across
  edges into a global order.
- **Sprint shape.** P2 stays a single-item production-prep sprint;
  estimate ~5–7 days. Now drainable.

---

## 3. Followup queue — sequential next sprints

All four flagged followups land sequentially (one sprint each), in this
order:

### 3.1 FU-deploy — `Deploy.ps1` for ERP V2

- **Deploy target.** Same Windows host as v1 (different ports + NSSM
  service names).
  - `NickERP_Inspection_Web` on port `:5410`
  - `NickERP_Portal` on port `:5400` (already established in P1
    runbook 01)
  - Future reservations: `NickERP_NickFinance_*` on `:5420` range,
    `NickERP_NickHR_*` on `:5430` range
- **Publish dir.** `C:\Shared\ERP V2\publish\<service>\` (mirrors v1's
  `C:\Shared\NSCIM_PRODUCTION\publish\`).
- **Robocopy source.** v0 picked the simpler same-host pattern: `dotnet
  publish -o C:\Shared\ERP V2\publish\<service>\` writes directly into
  the NSSM `AppDirectory`, collapsing v1's separate robocopy step into
  the publish step. The cross-host `Y:\` variant (separate dev box →
  robocopy from `Y:\` into prod) is documented in `Deploy.ps1`'s header
  as the "future when dev box is separate from prod" pattern; switch
  shape if/when that ever becomes the case.
- **Script shape.** Mirror `Deploy.ps1` from v1 (parameters
  `-ApiOnly`, `-WebAppOnly`, `-SkipBuild`, `-DryRun`). Each ERP V2
  service gets a publish step + a service-restart step + a healthz
  probe.
- **NSSM service config.** Document `binPath`, env-var injection for
  `NICKSCAN_DB_PASSWORD` (rotated) + the new `app.user_id` plumbing
  (FU-userid). LocalSystem account.
- **Effort.** ~0.5–1 day.

### 3.2 FU-userid — `app.user_id` session setting + RLS user-isolation

- **Why.** P3 deferred this. Lets `audit.notifications` user-isolation
  move from LINQ-level to RLS-level. Mirrors `app.tenant_id` plumbing.
- **Plumb in `IdentityTenancyInterceptor`** (the Sprint-2 / H2 piece).
  On `ConnectionOpening`, if there's a current user (from `IUserContext`
  or equivalent), `SET app.user_id = <uuid>::uuid`. Mirror with
  fail-closed default `'00000000-0000-0000-0000-000000000000'`.
- **Promote `audit.notifications` user filter.** Drop the LINQ filter;
  add an RLS policy clause `"UserId" = current_setting('app.user_id', true)::uuid`.
- **System-context interaction.** `SetSystemContext()` paths reading
  `audit.notifications` for projection need the OR clause for
  `app.tenant_id = '-1'`. Not many — just the projector.
- **Effort.** ~1–1.5 days.

### 3.3 FU-host-status — `BackgroundService` liveness endpoint

- **Why.** P1 runbook 03's worker-wedge detection is currently
  log-grep-based. A `/healthz/workers` endpoint reporting per-worker
  state (last-tick, attempt-count, last-error) tightens the loop.
- **Shape.** New `IBackgroundServiceProbe` interface; each
  `BackgroundService` (PreRenderWorker, ScannerIngestionWorker,
  SourceJanitorWorker, AuditNotificationProjector) implements + DI-
  registers. New endpoint aggregates the probes into a JSON response.
  Auth: existing `[Authorize]` posture.
- **Effort.** ~0.5 day.

### 3.4 FU-icums-signing — IcumsGh envelope signing + key rotation

- **Why.** Stub'd in runbook 05. ICUMS hasn't asked for signed
  envelopes yet; this is pre-emptive. Land it before they do, so when
  the spec arrives we just enable the flag.
- **Shape.** Detached signature on the JSON envelope; HMAC-SHA256 with
  a per-tenant signing key from a secure store. Rotate via a
  SetSystemContext admin action that issues a new key + a brief
  overlap window during which both old and new keys are accepted.
- **Effort.** ~1 day. Runbook 02 (secret-rotation) gets a new section
  when this lands.

---

## 4. Image-analysis track — explicit user-driven

Image-analysis stays out of rolling-master scope. The user spawns a
dedicated master run when ready. Until then, the parallel work in
`Inference.Abstractions/`, `Inference.Mock/`, `Inference.OnnxRuntime/`,
`tools/v1-label-export/` stays uncommitted in the user's local checkout.

**Heads-up for that future master.** The user's untracked
`IInboundOutcomeAdapter.cs` may reference `AuthorityDocument` from the
DTO namespace — that's now `AuthorityDocumentDto` (FU-7 rename). The
inference-master should grep for the bare name and rename refs.

---

## 5. Drainable backlog (refilled)

In execution-priority order:

1. FU-deploy (~0.5–1 day)
2. FU-userid (~1–1.5 days)
3. FU-host-status (~0.5 day)
4. FU-icums-signing (~1 day)
5. G2 NickFinance Petty Cash pathfinder (~5–7 days; spec above is
   binding)
6. P2 Edge node SQLite buffer + replay (~5–7 days; spec above is
   binding)

A future master spawn can pick from the top. The four followups can run
as a single bundled "Sprint 9: Followup-2 Sweep" if parallel-safe; G2
and P2 are single-item sprints.
