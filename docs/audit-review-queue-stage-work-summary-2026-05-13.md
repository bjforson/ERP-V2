# Audit Review Queue Stage Work Summary - 2026-05-13

## Work Done

- Added a background-safe `IAuditDispositionService` / `AuditDispositionService`.
- Refactored `AuditReviewConsumer` into a thin queue adapter.
- Kept audit routing centered on `AuditReviewPayload.ReviewId`.
- Moved real disposition logic into the application layer:
  - `concur` creates or reuses a verdict and queues outbound submission.
  - `dissent`, `hold`, and `escalated` route the case to exception review without outbound submission.
  - system fallback can auto-complete an open audit review.
- Registered the disposition service through the review DI extension.
- Updated the audit review UI flow to reuse an existing open audit review instead of creating duplicates.
- Fixed targeted escalation order so escalation happens before completion.
- Added or updated tests for:
  - audit-review consumer routing
  - dissent/no-submission behavior
  - idempotent replay
  - one-time enqueue on review completion
  - open review reuse in service/page

## Verification

Ran:

```powershell
dotnet test tests/NickERP.Inspection.Web.Tests/NickERP.Inspection.Web.Tests.csproj
```

Result:

- Passed: 661
- Failed: 0

## Left To Do

- Decide the final product rule for mapping audit `concur` to a verdict when prior analyst verdict/finding context is richer than current severity-based inference.
- Consider factoring external-system serving-instance resolution so audit disposition reuses `ExternalSystemAdminService.ResolveServingInstancesAsync` logic directly instead of duplicating lookup shape.
- Add database/integration coverage against real Postgres queue tables if needed, especially for idempotency keys and old queued JSON payload compatibility.
- Clean up broader lifecycle/status naming separately, especially `queued` vs `pending` outbound submission semantics across workers/admin UI.
- Review unrelated pre-existing dirty files before committing, since the worktree had other modified submission/admin files already.

## Resolved (post-handoff sweep 2026-05-13 → 2026-05-16)

Original Left-to-do items above are preserved verbatim for audit trail.
All five have since been addressed:

- ✅ **Concur → verdict mapping rule.** [B4] `DecideVerdictAsync` now
  prefers the persisted analyst verdict — concur means "agree with the
  analyst's call," not re-infer from severity. Severity-based inference
  is retained as the fallback for the system-auto-concur and audit-first
  paths. Commit `e374f9d6`. New test
  `ProcessAsync_ConcurReusesPriorAnalystVerdictInsteadOfSeverityInference`.
- ✅ **External-system resolution factored.** [B2] Added
  `ExternalSystemAdminService.ResolvePreferredServingInstanceAsync`;
  `AuditDispositionService.ResolveExternalSystemInstanceAsync` is now a
  thin wrapper. Behavior alignment: the bindings filter now also tightens
  to PerLocation / SubsetOfLocations (matched the existing
  `ResolveServingInstancesAsync` shape). Commit `eb4b97e7`.
- ✅ **Queue payload back/forward-compat tests.** [B5] Six new
  `QueuePayloadCompatibilityTests` pin the JSON wire format for both
  `OutboundSubmissionPayload` and `AuditReviewPayload` (legacy positional
  shape deserialises with null extensions; new shape round-trips; unknown
  future fields are ignored). Platform-level queue mechanics + idempotency
  dedup already live in
  `tests/NickERP.Platform.Tests/Queueing/Services/PostgresQueueIntegrationTests.cs`,
  so the inspection-side gap was the payload contract specifically.
  Commit `96673ef4`.
- ✅ **`queued` vs `pending` status contract documented.** [B1]
  `OutboundSubmission.Status` XML doc now enumerates each value with its
  owner and transitions. `OutboundSubmissionDispatchWorker` /
  `SubmissionConsumer` / `IcumsSubmissionQueueAdminService` XML docs
  cross-link. [B3] full path-ownership contract:
  [`docs/OUTBOUND-DISPATCH-OWNERSHIP.md`](OUTBOUND-DISPATCH-OWNERSHIP.md).
  Commits `38c5cad0` (B1), `88fa160a` (B3).
- ✅ **Worktree residue reviewed and committed.** Phase A confirmed the
  "unrelated" files were actually the missing tail of the queue arc;
  staged + committed cleanly in `96d45391`. `.claude/*` and the runbook
  14 PDF remain deliberately untracked.

Tests at end of sweep: web 668/668, E2E 6/6, perf selftest 31/31.
