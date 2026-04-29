# System-Context Audit Register

Append-only register of every code path that calls
`ITenantContext.SetSystemContext()`. Reviewed at every sprint boundary by
the rolling master and at every security review by the user.

## Format

| Caller | File:Line | Why | RLS opt-in clauses needed | Date | Sprint |
|---|---|---|---|---|---|

## Entries

| Caller | File:Line | Why | RLS opt-in clauses needed | Date | Sprint |
|---|---|---|---|---|---|
| `AuditNotificationProjector.ProjectOnceAsync` | `platform/NickERP.Platform.Audit.Database/Services/AuditNotificationProjector.cs` (`discoveryScope` block) | Discover the set of tenant ids that have new `audit.events` rows since the projector's checkpoint, before fanning out per-tenant. Reads `audit.events` only — already opted in (Sprint 5). | None new — `audit.events` already opts in (Sprint 5). | 2026-04-29 | Sprint 8 / P3 |
| `AuditNotificationProjector.ProjectTenantAsync` | `platform/NickERP.Platform.Audit.Database/Services/AuditNotificationProjector.cs` (per-tenant insert block) | INSERT notification rows for users in this tenant. The projector has no current user, so `app.user_id` resolves to the zero UUID; the new `tenant_user_isolation_notifications` policy would otherwise fail WITH CHECK against the row's real `UserId`. System context lets the OR clause admit the write. Per-tenant fan-out is preserved via a LINQ `e.TenantId == tenantId` filter on the read side. | `audit.notifications` opt-in (`tenant_user_isolation_notifications`) added FU-userid. | 2026-04-29 | Sprint 9 / FU-userid |

## Tables that opt in to system context

| Table | Migration | Sprint | Rationale |
|---|---|---|---|
| `audit.events` | `20260429061910_AddSystemContextOptInToEvents` | Sprint 5 | Suite-wide events (FX rate, GL chart-of-accounts) need NULL-tenant inserts; G1 #4 dropped NOT NULL but the RLS policy blocked the write. |
| `audit.notifications` | `20260429114858_Promote_Notifications_UserIsolation_To_Rls` | Sprint 9 / FU-userid | The combined `tenant_user_isolation_notifications` policy compares `"UserId"` against `app.user_id`; the projector (a background worker) has no current user so `app.user_id` resolves to the zero UUID. The system-context OR clause admits the projector's writes. Reads stay user-scoped because no production read path uses system context against this table. |

## Review checklist

At every sprint boundary, the master coordinator confirms:

- Every entry in "Entries" still corresponds to live code (no dead callers).
- Every entry in "Tables that opt in" still has its `OR ... = '-1'` clause
  intact (run `psql -c "\d+ audit.events"` and inspect the policy).
- No new `SetSystemContext()` callers exist that aren't in this register
  (`grep -r "SetSystemContext" --include='*.cs'`).
- No table outside the "Tables that opt in" list has the `'-1'` clause
  (this would be a silent posture broadening). Run a `pg_policies` audit:
  `SELECT schemaname, tablename, policyname FROM pg_policies WHERE qual LIKE '%''-1''%' OR with_check LIKE '%''-1''%';`.
