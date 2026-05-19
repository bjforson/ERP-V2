# Submission Queue Handoff Summary - 2026-05-13

## Done

- `CaseWorkflowService.SubmitAsync` no longer dispatches directly to the external system inline. It now creates or reuses an `OutboundSubmission`, marks it `queued`, and enqueues an `OutboundSubmissionPayload` through the transactional queue in the same database transaction.
- `SubmissionConsumer` is now the owner of queue-driven outbound dispatch for queued submissions. It claims the row, calls the external system adapter, and updates the submission/case state based on accepted, rejected, or failed responses.
- Audit review routing was moved behind `AuditDispositionService`, so `AuditReviewConsumer` delegates case state, verdict creation, and outbound submission handoff instead of carrying that full implementation inline.
- The ICUMS submission queue admin surface now understands `queued` rows. It includes them in status filters/defaults and avoids requeueing queue-owned rows into the older `pending` polling path.
- Tests were added or updated for strict-mode submit behavior, transactional queue enqueue, idempotent submit, queue consumer dispatch, legacy dispatcher skipping queued rows, admin requeue handling for queued rows, and audit review submission enqueueing.

## Verification

- `dotnet test tests\NickERP.Inspection.Web.Tests\NickERP.Inspection.Web.Tests.csproj --filter "StrictModeSubmissionTests|SubmissionConsumerTests|AuditReviewConsumerTests|OutboundDispatchRetryTests|IcumsSubmissionQueueAdminServiceTests"` passed: 34/34.
- `dotnet test tests\NickERP.Inspection.Web.Tests\NickERP.Inspection.Web.Tests.csproj` passed: 658/658.
- `git diff --check` reported no whitespace errors, only existing CRLF normalization warnings.

## Left

- `OutboundSubmissionDispatchWorker` still exists for legacy `pending` rows and manual/admin retry paths. Short-term this keeps backward compatibility; long-term it should either be retired or documented/configured as a distinct ownership path.
- E2E tests were not run. Web tests are green, but lifecycle E2E coverage may need updates if any tests still expect `SubmitAsync` to synchronously create an external outbox/response.
- The working tree still contains unrelated existing changes and untracked files, including `.claude/*`, `docs/runbooks/14-pilot-site-standup.pdf`, and adjacent review/audit files. These were left untouched.

## Resolved (post-handoff sweep 2026-05-13 → 2026-05-16)

This doc captures the state at handoff. The Left items above have since
been addressed; the original wording stays as-is for audit trail. New
status:

- ✅ **`OutboundSubmissionDispatchWorker` ownership documented.** Sprint 60
  [B3] landed [`docs/OUTBOUND-DISPATCH-OWNERSHIP.md`](OUTBOUND-DISPATCH-OWNERSHIP.md):
  the queue path owns `queued` rows; the legacy worker is the
  operator-driven retry path for `pending`; explicit retirement criteria.
  Commit `88fa160a`.
- ✅ **E2E lifecycle test reshaped and re-run.** `FullCaseLifecycleTests`
  now polls the `OutboundSubmission` row until `SubmissionConsumer` flips
  it from `queued` to `accepted`. E2E suite green 6/6. Commit `96d45391`
  (Phase A — closing the S+3 queue arc).
- ✅ **Working-tree residue committed.** The "adjacent review/audit files"
  (DI registration, queue-aware admin UI, the `AuditDispositionService`
  itself) turned out to be queue-arc work, not unrelated — committed
  cleanly in `96d45391`. `.claude/*` and the runbook 14 PDF remain
  deliberately untracked.

Additional follow-up scope beyond the original Left list:
- ✅ [B1] `OutboundSubmission.Status` state contract formalised in XML
  docs (commit `38c5cad0`).
- ✅ [B5] JSON wire-format pinned for `OutboundSubmissionPayload` (commit
  `96673ef4`). Platform-level queue mechanics + idempotency-key dedup
  already covered in
  `tests/NickERP.Platform.Tests/Queueing/Services/PostgresQueueIntegrationTests.cs`.

