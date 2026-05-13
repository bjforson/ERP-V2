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
