# Outbound submission dispatch — path ownership

> **Status.** Two dispatch paths share the `inspection.outbound_submissions`
> table post-S+3 (Sprint S+3 queue arc, landed 2026-05-13). This doc is
> the authoritative reference for which path owns which row and when the
> legacy path can be retired.

## 1. The two paths

### 1.1 Queue path (primary, post-S+3)

```
CaseWorkflowService.SubmitAsync
    └── insert OutboundSubmission { Status = "queued" }
        + enqueue OutboundSubmissionPayload (same transaction)
              └── SubmissionConsumer
                      ├── claim row via queue lease
                      ├── flip Status -> "dispatching"
                      ├── call IExternalSystemAdapter.SubmitAsync
                      └── flip Status -> "accepted" | "rejected" | "error"
```

- **Code.** [`CaseWorkflowService.SubmitAsync`](../modules/inspection/src/NickERP.Inspection.Web/Services/CaseWorkflowService.cs),
  [`SubmissionConsumer`](../modules/inspection/src/NickERP.Inspection.Application/Workflows/SubmissionConsumer.cs).
- **Queue.** `inspection.queue_submission` (Postgres table, EF migration
  `20260509153236_Add_S3_QueueTables`). Backed by `ITransactionalQueue<OutboundSubmissionPayload>`.
- **Owns rows in status.** `queued`, `dispatching` (consumer intermediate),
  and any `error`/`pending` rows re-driven via an explicit queue payload
  (e.g. consumer-crash retry).
- **Atomicity.** The row insert and the queue enqueue land in the same
  database transaction, so the submission either exists with its payload
  or neither does — no orphan rows.

### 1.2 Legacy poll path (operator-driven retry, post-S+3)

```
Operator clicks "Requeue" in admin UI
    └── IcumsSubmissionQueueAdminService.RequeueAsync / RequeueBulkAsync
        └── flip Status -> "pending", clear ErrorMessage
              └── OutboundSubmissionDispatchWorker (poll loop)
                      ├── pick row where Status = "pending"
                      ├── call IExternalSystemAdapter.SubmitAsync
                      └── flip Status / schedule backoff / -> "error" on budget exhaustion
```

- **Code.** [`OutboundSubmissionDispatchWorker`](../modules/inspection/src/NickERP.Inspection.Web/Services/OutboundSubmissionDispatchWorker.cs),
  [`IcumsSubmissionQueueAdminService`](../modules/inspection/src/NickERP.Inspection.Application/Submissions/IcumsSubmissionQueueAdminService.cs).
- **Owns rows in status.** `pending` only.
- **Sources of `pending` rows post-S+3.**
  1. Operator-driven manual retry from the admin queue UI.
  2. Pre-S+3 historical rows still in the table from before the queue
     arc landed.
- **No new code path produces `pending` rows.** Net-new submissions go
  through the queue path; the legacy worker drains the residue.

## 2. Why both paths exist

Post-S+3 the queue path is the primary dispatch path. The legacy worker
is kept for two reasons:

1. **Operator retry escape hatch.** Bulk requeue of `error` rows is a
   common operator triage action. Routing those through the legacy
   worker keeps retries bounded by the dispatcher's existing retry budget
   (`OutboundSubmissionRetryOptions.MaxRetries` + exponential backoff)
   and free of contention with the queue consumer. Re-enqueueing every
   manual retry on the transactional queue would have required either
   duplicating the retry-budget logic in the consumer or carrying an
   "operator-driven" flag through the queue payload — neither is worth
   the simplicity it removes.
2. **Historical residue.** Pre-S+3 rows in `pending` would otherwise be
   stranded with no consumer.

## 3. Path contention — does not happen

- The queue consumer only acts on rows for which an explicit queue
  payload exists. It does not hunt the table for `pending` rows.
- The legacy worker only acts on `Status = "pending"` rows. It does not
  touch `queued`, `dispatching`, or terminal states.
- The admin requeue path is a no-op on rows already in `pending` (legacy
  path already owns it) or `queued` (queue path still owns it).

The two paths read the same column but operate on disjoint state values,
so there is no race window where both will dispatch the same row.

## 4. Retirement criteria for the legacy worker

The legacy `OutboundSubmissionDispatchWorker` can be retired when both
of the following hold:

1. **No `pending` rows remain in production tables** (`SELECT count(*)
   FROM inspection.outbound_submissions WHERE status = 'pending'` across
   all tenants returns 0 at a stable point).
2. **Operator retry has been re-routed** through the queue path — either
   by re-enqueueing on requeue, or by giving operators a "retry via
   queue" UI that bypasses the legacy worker entirely.

Until then the worker stays. It is registered behind
`IcumsSubmissionDispatchOptions.Enabled` (default-disabled per the
Sprint 24 architectural decision); production hosts that enable it
inherit the legacy path.

## 5. Operator implications

- The admin queue UI shows `queued` and `pending` as separate states —
  do not treat them as synonyms. `queued` rows are autonomously being
  picked up; `pending` rows are owned by the legacy retry path.
- Bulk requeue of `queued` rows is a no-op (queue still owns them).
- The `inspection.icums.submission_requeued` /
  `inspection.icums.submission_bulk_requeued` audit events fire only on
  legacy-path requeues. Queue-path dispatch events are
  `nickerp.inspection.submission_dispatched`.

## 6. References

- Entity state contract: [OutboundSubmission.Status XML doc](../modules/inspection/src/NickERP.Inspection.Core/Entities/OutboundSubmission.cs).
- Backlog runbook (downstream stalls): [runbooks/05-icums-outbox-backlog.md](runbooks/05-icums-outbox-backlog.md).
- Sprint S+3 queue arc handoff: [submission-queue-handoff-summary-2026-05-13.md](submission-queue-handoff-summary-2026-05-13.md),
  [audit-review-queue-stage-work-summary-2026-05-13.md](audit-review-queue-stage-work-summary-2026-05-13.md).
