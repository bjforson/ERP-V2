# System-Context Audit Register

Append-only register of every code path that calls
`ITenantContext.SetSystemContext()`. Reviewed at every sprint boundary by
the rolling master and at every security review by the user.

**Last reviewed:** 2026-05-06 (Sprint 57 — full sweep across sprints
8-52). Previous full sweep was Sprint 21 / Tenant-Pt-2.

## Format

| Caller | File:Line | Why | RLS opt-in clauses needed | Date | Sprint |
|---|---|---|---|---|---|

## Entries

| Caller | File:Line | Why | RLS opt-in clauses needed | Date | Sprint |
|---|---|---|---|---|---|
| `AuditNotificationProjector.ProjectOnceAsync` | `platform/NickERP.Platform.Audit.Database/Services/AuditNotificationProjector.cs:142` (`discoveryScope` block) | Discover the set of tenant ids that have new `audit.events` rows since the projector's checkpoint, before fanning out per-tenant. Reads `audit.events` only — already opted in (Sprint 5). | None new — `audit.events` already opts in (Sprint 5). | 2026-04-29 | Sprint 8 / P3 |
| `AuditNotificationProjector.ProjectTenantAsync` | `platform/NickERP.Platform.Audit.Database/Services/AuditNotificationProjector.cs:217` (per-tenant insert block) | INSERT notification rows for users in this tenant. The projector has no current user, so `app.user_id` resolves to the zero UUID; the new `tenant_user_isolation_notifications` policy would otherwise fail WITH CHECK against the row's real `UserId`. System context lets the OR clause admit the write. Per-tenant fan-out is preserved via a LINQ `e.TenantId == tenantId` filter on the read side. | `audit.notifications` opt-in (`tenant_user_isolation_notifications`) added FU-userid. | 2026-04-29 | Sprint 9 / FU-userid |
| `FxRatePublishService.PublishAsync` | `modules/nickfinance/src/NickERP.NickFinance.Web/Services/FxRatePublishService.cs:72` (full method body) | Insert / update rows in `nickfinance.fx_rate` for suite-wide rates (NULL `TenantId`). FX rates are published daily by a finance admin and apply to every tenant's ledger writes; a normal per-tenant insert would fail the policy's WITH CHECK clause. SetSystemContext flips the session into `app.tenant_id = '-1'`; the OR clause on `tenant_isolation_fx_rate` admits the NULL-tenant write. The service captures the prior tenant id and restores it in a `finally` block. | `nickfinance.fx_rate` opt-in (`tenant_isolation_fx_rate`) added G2 (`20260429131858_Add_RLS_And_Grants`). | 2026-04-29 | G2 / NickFinance pathfinder |
| `EdgeReplayEndpoint.HandleAsync` | `modules/inspection/src/NickERP.Inspection.Web/Endpoints/EdgeReplayEndpoint.cs:180` (per-batch processing block) | A single edge replay batch can carry events for multiple tenants. The endpoint flips into system context for the batch processing and writes per-event rows into `audit.events` with the captured `OccurredAt` (= `edgeTimestamp`); the Sprint 5 opt-in clause on `audit.events` admits the writes via `OR app.tenant_id = '-1'`. Per-event tenant authorization is enforced by `audit.edge_node_authorizations` lookup (suite-wide reference, no RLS) BEFORE the audit row is written. | None new — `audit.events` already opts in (Sprint 5). | 2026-04-29 | Sprint 11 / P2 |
| `EdgeReplayEndpoint.EmitManifestFailureAsync` | `modules/inspection/src/NickERP.Inspection.Web/Endpoints/EdgeReplayEndpoint.cs:524` (manifest-validation-failure audit emission) | Sprint 45 / Phase B helper inside `HandleAsync` that emits a `ManifestValidationFailedAudit` row when an event's manifest fails validation. Same posture as the parent `HandleAsync` flip — multi-tenant batch needs system context for the audit-side INSERT. The emission is best-effort: if the audit DB is unavailable, the per-entry response still carries the load-bearing diagnostic. | None new — `audit.events` already opts in (Sprint 5). Sub-case of the `HandleAsync` entry above; documented separately for sprint-traceability of Sprint 45's manifest-failure path. | 2026-05-06 | Sprint 45 / Phase B (registered Sprint 57) |
| `EdgeReplayEndpoint.PersistScanPackageAsync` | `modules/inspection/src/NickERP.Inspection.Web/Endpoints/EdgeReplayEndpoint.cs:665` (success-path audit emission) | Sprint 45 / Phase B helper inside `HandleAsync` that emits the success audit (manifest-verified scan persisted). Same posture as the parent `HandleAsync` flip — multi-tenant batch needs system context for the audit-side INSERT. Idempotency key = manifest-derived (deterministic across replays). | None new — `audit.events` already opts in (Sprint 5). Sub-case of the `HandleAsync` entry above; documented separately for sprint-traceability of Sprint 45's success-audit path. | 2026-05-06 | Sprint 45 / Phase B (registered Sprint 57) |
| `EdgeAuthHandler.TryAuthenticatePerNodeAsync` | `modules/inspection/src/NickERP.Inspection.Web/Services/EdgeAuthHandler.cs:180` | Per-edge-node API key lookup runs PRE-tenant-resolution — the tenant is on the row itself, not in session state, so the handler cannot set `app.tenant_id` to the right value before the SELECT. SetSystemContext flips `app.tenant_id = '-1'`; the OR clause on `tenant_isolation_edge_node_api_keys` admits the read. After the row is found the handler uses the row's `TenantId` for downstream auth decisions. Bad-key path does NOT fall through to legacy `X-Edge-Token` (verified in `Bad_per_node_key_does_not_downgrade_to_legacy`). | `audit.edge_node_api_keys` opt-in (`tenant_isolation_edge_node_api_keys`) added in `20260430105510_Add_EdgeNodeApiKeys`. | 2026-04-30 | Sprint 13 / P2-FU-edge-auth |
| `InviteService.RedeemInviteAsync` | `platform/NickERP.Platform.Identity.Database/Services/InviteService.cs:247` (lookup-by-hash block) | Invite-token redemption runs PRE-tenant-resolution. The invitee is anonymous; the token's tenant is on the row itself. SetSystemContext flips `app.tenant_id = '-1'` so the lookup against `identity.invite_tokens` succeeds; the OR clause on `tenant_isolation_invite_tokens` admits the read. Validation (revoked / redeemed / expired) runs before the row's `TenantId` is surfaced to the caller. | `identity.invite_tokens` opt-in (`tenant_isolation_invite_tokens`) added in `20260504160000_Add_InviteTokens`. | 2026-05-04 | Sprint 21 / Tenant-Pt-2 |
| `InviteService.MarkRedeemedAsync` | `platform/NickERP.Platform.Identity.Database/Services/InviteService.cs:324` (mark-redeemed block) | Same posture as `RedeemInviteAsync` — the mark-redeemed UPDATE happens during the bootstrap window where no tenant context exists yet. The unique partial index on `TokenHash` (filtered to active rows) is what makes concurrent redemptions race-safe; the system-context flip is the gate that lets the UPDATE itself succeed. | None new — same `tenant_isolation_invite_tokens` opt-in covers UPDATE via the WITH CHECK clause. | 2026-05-04 | Sprint 21 / Tenant-Pt-2 |
| `AcceptInvite.razor` (page lifecycle) | `apps/portal/Components/Pages/AcceptInvite.razor` (`OnInitializedAsync` + `ConfirmAsync`) | Invitee is anonymous up to and through the redemption page. The page reads `tenancy.tenants` (no RLS — root of the tenant graph) for the tenant name, then writes the new `IdentityUser` + `UserScope` rows under system context because the user has no tenant scope yet (it's exactly what we're adding). Both writes carry the row's correct `TenantId` so the WITH CHECK passes via the standard tenant-equals-row clause; the `'-1'` opt-in is what admits the read of `identity.invite_tokens` indirectly through `InviteService` and is also what admits the UPDATE on the invite row when marking redeemed. | `identity.invite_tokens` opt-in (above). `identity.identity_users` already admits system-context writes from Sprint 9 / FU-userid (the projector pattern); the new caller is documented here for completeness. | 2026-05-04 | Sprint 21 / Tenant-Pt-2 |
| `WebhookDispatchWorker.ReadNewAuditEventsAsync` | `modules/inspection/src/NickERP.Inspection.Web/Services/WebhookDispatchWorker.cs:393` | Cross-tenant fan-out worker discovers new audit events per tenant since the per-tenant cursor and dispatches them through registered `IOutboundWebhookAdapter` plugins. SetSystemContext is needed for the `audit.events` read because the worker is a singleton background service, not a per-tenant request — the per-tenant context is built fresh each tick from `TenancyDbContext.Tenants` discovery. The LINQ `e.TenantId == tenantId` filter narrows the read to the current tenant, defending against a future RLS misconfiguration. Pattern matches `AuditNotificationProjector` exactly. | None new — `audit.events` already opts in (Sprint 5). | 2026-05-06 | Sprint 24 / B3 webhook dispatch (registered Sprint 57) |
| `ScannerThresholdResolver.LoadFromDbAsync` | `modules/inspection/src/NickERP.Inspection.Application/Thresholds/ScannerThresholdResolver.cs:137` | **GAP — see "Pending opt-in: scanner_threshold_profiles" section below.** Cross-tenant resolver opens a fresh DI scope on cache miss and reads the active threshold profile by scanner-id under system context. The resolver is shared infrastructure (singleton + LISTEN/NOTIFY-driven cache); the request-scoped tenant context isn't available in the resolver's scope. | **MISSING** — `inspection.scanner_threshold_profiles` policy `tenant_isolation_scanner_threshold_profiles` does NOT have an `OR app.tenant_id = '-1'` clause. The SetSystemContext call effectively sets `app.tenant_id = '-1'` and the SELECT then fails the policy's USING test. Triage section below. | 2026-04-29 (caller landed Sprint 12); 2026-05-06 (gap surfaced Sprint 57) | Sprint 12 — Phase R3 / §6.5 thresholds (registered Sprint 57) |

## Tables that opt in to system context

| Table | Migration | Sprint | Rationale |
|---|---|---|---|
| `audit.events` | `20260429061910_AddSystemContextOptInToEvents` | Sprint 5 | Suite-wide events (FX rate, GL chart-of-accounts) need NULL-tenant inserts; G1 #4 dropped NOT NULL but the RLS policy blocked the write. |
| `audit.notifications` | `20260429114858_Promote_Notifications_UserIsolation_To_Rls` | Sprint 9 / FU-userid | The combined `tenant_user_isolation_notifications` policy compares `"UserId"` against `app.user_id`; the projector (a background worker) has no current user so `app.user_id` resolves to the zero UUID. The system-context OR clause admits the projector's writes. Reads stay user-scoped because no production read path uses system context against this table. |
| `nickfinance.fx_rate` | `20260429131858_Add_RLS_And_Grants` | G2 / NickFinance pathfinder | Suite-wide FX rates carry NULL `TenantId`; a per-tenant insert would fail WITH CHECK. The system-context OR clause admits NULL-tenant writes from `FxRatePublishService.PublishAsync`. Reads are intentionally permissive (the policy USING clause also admits NULL-tenant rows) so every per-tenant ledger write can resolve the rate without a system-context hop. |
| `audit.edge_node_api_keys` | `20260430105510_Add_EdgeNodeApiKeys` | Sprint 13 / P2-FU-edge-auth | Edge node auth runs pre-tenant-resolution: the request arrives with only an opaque API key, the row carries the `TenantId`. SetSystemContext + the OR clause is the only path to look up the row before the tenant is known. Reads under system context are limited to the auth handler's lookup-by-hash + the issuance/revocation admin flow. |
| `identity.invite_tokens` | `20260504160000_Add_InviteTokens` | Sprint 21 / Tenant-Pt-2 | Invite redemption runs pre-tenant-resolution: the invitee is anonymous; the row carries the `TenantId`. SetSystemContext + the OR clause is the only path for `InviteService.RedeemInviteAsync` and `InviteService.MarkRedeemedAsync` to succeed. Single-use semantics enforced via the unique partial index on `(TokenHash) WHERE RedeemedAt IS NULL AND RevokedAt IS NULL`. |

## Pending opt-in: `inspection.scanner_threshold_profiles` (Sprint 57 sweep finding)

`ScannerThresholdResolver.LoadFromDbAsync` (Sprint 12 / Phase R3, file
`modules/inspection/src/NickERP.Inspection.Application/Thresholds/ScannerThresholdResolver.cs:137`)
calls `tenant.SetSystemContext()` for the cross-tenant lookup of the
active threshold profile. The intent is correct — the resolver is a
singleton with a LISTEN/NOTIFY-driven cache, the request-scoped
`ITenantContext` is unavailable in the resolver's own scope, and only
the scanner-id is needed to find the row. **However** the policy
created by `20260429062458_Add_PhaseR3_TablesInferenceModernization`
(`tenant_isolation_scanner_threshold_profiles`) is the standard
`"TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint`
shape with NO `OR app.tenant_id = '-1'` clause. So under
SetSystemContext (`app.tenant_id = '-1'`), the policy USING test
evaluates `"TenantId" = -1` for every row and yields zero results;
`LoadFromDbAsync` then returns `ScannerThresholdSnapshot.V1Defaults()`
on every cache miss.

**Operational impact today**: the resolver always returns the v1
defaults for any cache miss, regardless of what's in the table. Any
operator-tuned threshold profile is invisible to the resolver. The
warning log "No active threshold profile for scanner {ScannerId} —
falling back to v1 defaults" fires on every cache miss. **In dev,
where no operator-tuned profiles exist yet, this is invisible — the
v1 defaults match what the bootstrap migration seeded as `Source =
Bootstrap`.** The bug surfaces only when an operator stages a
non-default profile and discovers it is silently ignored.

**Resolution paths** (operator decision; do NOT implement without
explicit user confirmation per `feedback_confirm_before_weakening_security.md`):

- **(a) Add the `'-1'` opt-in clause** to
  `tenant_isolation_scanner_threshold_profiles` via a new migration
  matching the `audit.events` shape. Adds `inspection.scanner_threshold_profiles`
  to the "Tables that opt in" list above. Reads under system context
  would return all tenants' rows, but the resolver narrows by
  scanner-id (which carries the tenant indirectly via the FK to
  `scanner_device_instances`). **Trade-off:** broadens the table's
  read surface from "current tenant only" to "any tenant via system
  context" — same posture as `audit.events` and `nickfinance.fx_rate`
  already accept. **Likely best fit** since the resolver pattern is
  the same as the existing opt-in tables.
- **(b) Refactor the resolver to use per-scanner tenant resolution.**
  Pre-fetch the `(scannerDeviceInstanceId → tenantId)` map at startup
  via the bootstrap migration's seed rows; on cache miss use
  `SetTenant(tenantId)` instead of system context. The resolver no
  longer needs cross-tenant access; the table's RLS posture stays
  unchanged. **Trade-off:** the LISTEN/NOTIFY invalidation has to
  carry the tenant id, and the startup pre-fetch is itself a
  cross-tenant read that needs system context (same problem moved up
  a level).
- **(c) Refactor to a tenant-aware resolver per request scope.** Move
  the cache from singleton to `IServiceProvider` per-tenant scoped;
  drop SetSystemContext entirely. **Trade-off:** loses the in-process
  cache benefit; cache TTL becomes per-tenant which multiplies the DB
  hit rate.

**Recommendation:** option (a) is the smallest delta and matches the
pattern the rest of the audit register already accepts. A new
migration `Add_SystemContextOptIn_ScannerThresholdProfiles` adds the
`'-1'` clause; the register entry above moves out of "Pending opt-in"
into the standard table; the gap closes. **Operator must confirm
before implementation** — broadening RLS posture is on the
"confirm before weakening security" list.

This finding is recorded here (audit register), in
`docs/security/audit-checklist-2026.md` (next sprint's reviewer should
add it as a SEC-TENANT entry), and in `DEFERRED_ACTIONS.md` if the
project tracks one. **Until resolution: the `ScannerThresholdResolver`
silently no-ops** and falls back to v1 defaults; this matches the v1
behaviour during the rollout window so nothing visible breaks, but
operator-tuned thresholds will have no effect.

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

## Pattern: per-tenant fan-out without `SetSystemContext` (Sprints 36 / 43 / 44)

Background workers and probes that need to operate on every active
tenant per tick generally pick this pattern over system-context
discovery:

1. Discover tenants via `TenancyDbContext.Tenants` (the tenant table
   has no RLS — it is the root of the tenant graph).
2. For each tenant, `SetTenant(tenantId)` and run the per-tenant work
   under standard RLS narrowing.
3. Force-close the per-DB connection at scope exit so the
   `TenantConnectionInterceptor` re-pushes `app.tenant_id` on the next
   open.

Workers that follow this pattern **deliberately do NOT add a register
entry** because they don't call `SetSystemContext`:

- **Sprint 36 — `SlaStateRefresherWorker`**: per-tenant fan-out from
  `TenancyDbContext.Tenants` + per-tenant `ISlaTracker.RefreshStatesAsync`.
- **Sprint 43 — `MultiTenantInvariantProbe`**: the probe's three
  sub-checks each open their own tenant-scoped (NOT system-scoped)
  connection. The probe DOES enumerate `SetSystemContext` callers in
  the source tree as part of sub-check 2 (register integrity) — but
  enumeration is not a call. The cross-tenant-export-refusal
  sub-check synthesises a foreign user-id under the existing tenant
  context; no system-context flip needed.
- **Sprint 44 — `RetentionEnforcerWorker`**: per-tenant fan-out from
  `TenancyDbContext.Tenants` + per-tenant retention-class lookup +
  audit emission. The worker explicitly documents in its
  remarks-comment that it follows the `SlaStateRefresherWorker`
  pattern and does NOT introduce a `SetSystemContext` caller.
- **Sprint 50 — `AuthorityDocumentInboxWorker` per-tenant routing
  variant**: the multi-tenant inbox routing path uses per-tenant
  `SetTenant` flips, not system context. Single-tenant fallback path
  also stays under standard tenant scope.

Treat this list as the canonical "considered system-context, picked
per-tenant fan-out" set; the matching workers' XML doc comments
explicitly call out the choice for each sprint.

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
- The `MultiTenantInvariantProbe` (Sprint 43) sub-check 2 enforces this
  same drift detection at runtime when the
  `Pilot:SourceRoot` setting points at the source tree; the dashboard
  flips the gate red on register-vs-source drift.

## Sprint 57 sweep notes (2026-05-06)

Full sweep ran by Sprint 57 master against
`grep -r "\.SetSystemContext\s*(" --include='*.cs'` to verify the
register matches live code. Findings:

- **3 production callers added** to the Entries table that were
  missing as standalone rows: (i) Sprint 24
  `WebhookDispatchWorker.ReadNewAuditEventsAsync`, (ii) Sprint 45
  Phase B `EdgeReplayEndpoint.EmitManifestFailureAsync` +
  `PersistScanPackageAsync` (sub-cases of the parent `HandleAsync`
  entry, documented for sprint-traceability), (iii) Sprint 12
  `ScannerThresholdResolver.LoadFromDbAsync` (with the gap-finding
  flagged separately — see "Pending opt-in" section).
- **All 5 existing entries verified** to still match live code at
  exact file:line locations as of `9d77caec` on main.
- **All 5 opt-in tables verified** to still have their `'-1'` OR
  clauses intact in `tools/migrations/sprint-13-deploy/*.sql` and the
  matching EF migration files.
- **1 gap surfaced** — `inspection.scanner_threshold_profiles` is a
  caller without an opt-in. Triage notes in the "Pending opt-in"
  section above; operator decision required for resolution path.
- **Pattern documentation added** for per-tenant fan-out workers
  (Sprints 36 / 43 / 44 / 50) that deliberately don't register.
- **No zombie entries** — every register entry corresponds to a real
  code path.
