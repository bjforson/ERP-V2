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
| `FxRatePublishService.PublishAsync` | `modules/nickfinance/src/NickERP.NickFinance.Web/Services/FxRatePublishService.cs` (full method body) | Insert / update rows in `nickfinance.fx_rate` for suite-wide rates (NULL `TenantId`). FX rates are published daily by a finance admin and apply to every tenant's ledger writes; a normal per-tenant insert would fail the policy's WITH CHECK clause. SetSystemContext flips the session into `app.tenant_id = '-1'`; the OR clause on `tenant_isolation_fx_rate` admits the NULL-tenant write. The service captures the prior tenant id and restores it in a `finally` block. | `nickfinance.fx_rate` opt-in (`tenant_isolation_fx_rate`) added G2 (`20260429131858_Add_RLS_And_Grants`). | 2026-04-29 | G2 / NickFinance pathfinder |
| `EdgeReplayEndpoint.HandleAsync` | `modules/inspection/src/NickERP.Inspection.Web/Endpoints/EdgeReplayEndpoint.cs` (per-batch processing block) | A single edge replay batch can carry events for multiple tenants. The endpoint flips into system context for the batch processing and writes per-event rows into `audit.events` with the captured `OccurredAt` (= `edgeTimestamp`); the Sprint 5 opt-in clause on `audit.events` admits the writes via `OR app.tenant_id = '-1'`. Per-event tenant authorization is enforced by `audit.edge_node_authorizations` lookup (suite-wide reference, no RLS) BEFORE the audit row is written. | None new — `audit.events` already opts in (Sprint 5). | 2026-04-29 | Sprint 11 / P2 |
| `EdgeAuthHandler.TryAuthenticatePerNodeAsync` | `modules/inspection/src/NickERP.Inspection.Web/Services/EdgeAuthHandler.cs:174` | Per-edge-node API key lookup runs PRE-tenant-resolution — the tenant is on the row itself, not in session state, so the handler cannot set `app.tenant_id` to the right value before the SELECT. SetSystemContext flips `app.tenant_id = '-1'`; the OR clause on `tenant_isolation_edge_node_api_keys` admits the read. After the row is found the handler uses the row's `TenantId` for downstream auth decisions. Bad-key path does NOT fall through to legacy `X-Edge-Token` (verified in `Bad_per_node_key_does_not_downgrade_to_legacy`). | `audit.edge_node_api_keys` opt-in (`tenant_isolation_edge_node_api_keys`) added in `20260430105510_Add_EdgeNodeApiKeys`. | 2026-04-30 | Sprint 13 / P2-FU-edge-auth |
| `InviteService.RedeemInviteAsync` | `platform/NickERP.Platform.Identity.Database/Services/InviteService.cs` (lookup-by-hash block) | Invite-token redemption runs PRE-tenant-resolution. The invitee is anonymous; the token's tenant is on the row itself. SetSystemContext flips `app.tenant_id = '-1'` so the lookup against `identity.invite_tokens` succeeds; the OR clause on `tenant_isolation_invite_tokens` admits the read. Validation (revoked / redeemed / expired) runs before the row's `TenantId` is surfaced to the caller. | `identity.invite_tokens` opt-in (`tenant_isolation_invite_tokens`) added in `20260504160000_Add_InviteTokens`. | 2026-05-04 | Sprint 21 / Tenant-Pt-2 |
| `InviteService.MarkRedeemedAsync` | `platform/NickERP.Platform.Identity.Database/Services/InviteService.cs` (mark-redeemed block) | Same posture as `RedeemInviteAsync` — the mark-redeemed UPDATE happens during the bootstrap window where no tenant context exists yet. The unique partial index on `TokenHash` (filtered to active rows) is what makes concurrent redemptions race-safe; the system-context flip is the gate that lets the UPDATE itself succeed. | None new — same `tenant_isolation_invite_tokens` opt-in covers UPDATE via the WITH CHECK clause. | 2026-05-04 | Sprint 21 / Tenant-Pt-2 |
| `AcceptInvite.razor` (page lifecycle) | `apps/portal/Components/Pages/AcceptInvite.razor` (`OnInitializedAsync` + `ConfirmAsync`) | Invitee is anonymous up to and through the redemption page. The page reads `tenancy.tenants` (no RLS — root of the tenant graph) for the tenant name, then writes the new `IdentityUser` + `UserScope` rows under system context because the user has no tenant scope yet (it's exactly what we're adding). Both writes carry the row's correct `TenantId` so the WITH CHECK passes via the standard tenant-equals-row clause; the `'-1'` opt-in is what admits the read of `identity.invite_tokens` indirectly through `InviteService` and is also what admits the UPDATE on the invite row when marking redeemed. | `identity.invite_tokens` opt-in (above). `identity.identity_users` already admits system-context writes from Sprint 9 / FU-userid (the projector pattern); the new caller is documented here for completeness. | 2026-05-04 | Sprint 21 / Tenant-Pt-2 |

## Tables that opt in to system context

| Table | Migration | Sprint | Rationale |
|---|---|---|---|
| `audit.events` | `20260429061910_AddSystemContextOptInToEvents` | Sprint 5 | Suite-wide events (FX rate, GL chart-of-accounts) need NULL-tenant inserts; G1 #4 dropped NOT NULL but the RLS policy blocked the write. |
| `audit.notifications` | `20260429114858_Promote_Notifications_UserIsolation_To_Rls` | Sprint 9 / FU-userid | The combined `tenant_user_isolation_notifications` policy compares `"UserId"` against `app.user_id`; the projector (a background worker) has no current user so `app.user_id` resolves to the zero UUID. The system-context OR clause admits the projector's writes. Reads stay user-scoped because no production read path uses system context against this table. |
| `nickfinance.fx_rate` | `20260429131858_Add_RLS_And_Grants` | G2 / NickFinance pathfinder | Suite-wide FX rates carry NULL `TenantId`; a per-tenant insert would fail WITH CHECK. The system-context OR clause admits NULL-tenant writes from `FxRatePublishService.PublishAsync`. Reads are intentionally permissive (the policy USING clause also admits NULL-tenant rows) so every per-tenant ledger write can resolve the rate without a system-context hop. |
| `audit.edge_node_api_keys` | `20260430105510_Add_EdgeNodeApiKeys` | Sprint 13 / P2-FU-edge-auth | Edge node auth runs pre-tenant-resolution: the request arrives with only an opaque API key, the row carries the `TenantId`. SetSystemContext + the OR clause is the only path to look up the row before the tenant is known. Reads under system context are limited to the auth handler's lookup-by-hash + the issuance/revocation admin flow. |
| `identity.invite_tokens` | `20260504160000_Add_InviteTokens` | Sprint 21 / Tenant-Pt-2 | Invite redemption runs pre-tenant-resolution: the invitee is anonymous; the row carries the `TenantId`. SetSystemContext + the OR clause is the only path for `InviteService.RedeemInviteAsync` and `InviteService.MarkRedeemedAsync` to succeed. Single-use semantics enforced via the unique partial index on `(TokenHash) WHERE RedeemedAt IS NULL AND RevokedAt IS NULL`. |

## Sprint 36 / FU-sla-state-refresher-worker — considered, not added

The Sprint 36 `SlaStateRefresherWorker`
(`modules/inspection/src/NickERP.Inspection.Web/Services/SlaStateRefresherWorker.cs`)
was specified to use system-context discovery for cross-tenant
enumeration of tenants with open SLA windows, mirroring the
`AuditNotificationProjector` pattern. After review the implementation
**deliberately does NOT call `SetSystemContext()`** — pattern matches
`ScannerHealthSweepWorker` instead:

- Tenant discovery via `TenancyDbContext.Tenants` (no RLS — root of the
  tenant graph).
- Per-tenant `SetTenant(tenantId)` flip for the inspection-DB reads
  (`inspection.sla_window` + `ISlaTracker.RefreshStatesAsync`).

System-context discovery would require an `OR app.tenant_id = '-1'`
opt-in clause on `tenant_isolation_sla_window`, broadening the table's
read surface for marginal efficiency gain (an extra "is the tenant
active?" check per tick on a small `tenancy.tenants` table is cheap).
Per `feedback_confirm_before_weakening_security.md`, broadening RLS
posture for ergonomic gain requires explicit user confirmation; the
non-broadening alternative was chosen.

**No new register entry for this worker.** If pilot data shows the
per-tenant fan-out is actually expensive enough to warrant
cross-tenant discovery, the change would require: (1) a new RLS opt-in
migration on `inspection.sla_window`, (2) a register entry here, (3)
user confirmation per the security-posture rule.

## Sprint 25 / Tenant-Pt-3 — non-system-context cross-tenant reads

The Sprint 25 `TenantExportService` + `TenantExportRunner` +
`TenantExportBundleBuilder` are platform-admin tooling that reads
across tenants but **does NOT call `SetSystemContext()`**. Pattern
mirrors `TenantPurgeOrchestrator` (Sprint 18): each per-DB read opens
its own raw `NpgsqlConnection` and `SET app.tenant_id = '<tenantId>'`
explicitly so the existing per-table RLS USING clauses admit reads of
THAT tenant's rows. No new opt-in clause is required, no new register
entry is required.

The two new tables (`tenancy.tenant_export_requests`,
`tenancy.tenant_purge_log` from Sprint 18) live in the `tenancy`
schema and are intentionally NOT under RLS — same posture as the
`tenancy.tenants` table itself (root of the tenant graph). Cross-tenant
admin queries against these tables succeed without any system-context
flip.

The export download endpoint (`/api/tenant-exports/{id}/download` in
`apps/portal/Program.cs`) gates on `Status = Completed && !Revoked &&
!Expired` server-side via `ITenantExportService.DownloadExportAsync` —
direct artifact paths are not exposed on disk to the client; every
download bumps a counter and emits a `tenant_export_downloaded` audit
event.

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
