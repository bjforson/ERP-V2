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

