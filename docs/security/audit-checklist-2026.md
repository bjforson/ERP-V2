# NickERP v2 — Phase V security audit checklist (2026)

**Status:** This is a CHECKLIST for Phase V execution, not a one-time audit. Phase V proper triggers when the pilot site is locked (~Sprint 22-24 per plan §13). Until then, this document is the running spec — items get added/refined as the platform evolves.

**Companion documents:**
- `docs/system-context-audit-register.md` — every code path that calls `ITenantContext.SetSystemContext()` and the table opt-in clauses that admit those writes. Reviewed at every sprint boundary; this checklist confirms no NEW callers exist outside the register.
- `docs/perf/test-plan.md` — load-test plan that complements this security audit.
- `docs/runbooks/` — operational procedures the audit references for posture.

**How to use:**
1. Auditor copies this file → `audit-{site}-{date}.md` and ticks items as they go.
2. Each item has an ID (`SEC-{cat}-{n}`), a description, a `verify` command/SQL/manual step, and an `expect` result.
3. Failures get a finding ID (`AUD-{n}`) with severity (P0 BLOCK pilot / P1 fix-before-launch / P2 fix-by-launch+1mo / P3 backlog).
4. The pilot doesn't ship until all P0 + P1 findings are resolved.

---

## Category index

| Cat | Section | Item count |
|---|---|---|
| AUTH | Authentication + identity | 8 |
| AUTHZ | Authorization | 7 |
| TENANT | Tenant isolation (RLS) | 12 |
| SECRETS | Secrets management | 8 |
| TLS | Network + transport security | 7 |
| DB | Database posture | 10 |
| AUDIT | Audit + observability | 8 |
| EDGE | Edge node posture | 7 |
| MOD | Module-specific (inspection / nickfinance / nickhr) | 10 |
| DEP | Dependency hygiene | 6 |
| HEAD | HTTP headers + cookies | 6 |

**Total: ~89 items.**

---

## AUTH — Authentication + identity

### SEC-AUTH-1 — CF Access JWT validation enabled in non-Development
**Verify:** Inspect `apps/portal/Program.cs` (and every other web entry point) for `AddAuthentication(...).AddJwtBearer(...)` configuration. Confirm `RequireHttpsMetadata = true` outside Development. Confirm `Issuer`, `Audience`, signing-key source (CF Access JWKS).
**Expect:** JWT bearer scheme registered; CF Access JWKS URI present; `RequireHttpsMetadata=true` for Staging/Production environments.
**Severity if missing:** P0.

### SEC-AUTH-2 — DevBypass disabled in non-Development
**Verify:** Search for `DevBypass`, `AllowAnonymous` (whole-controller scope), or any "always-true" auth handler. Confirm any dev-bypass is gated behind `IsDevelopment()` or an environment-specific config flag that is FALSE in pilot env.
**Expect:** Pilot environment runs with no dev-bypass branch reachable.
**Severity:** P0.

### SEC-AUTH-3 — Token lifetime + rotation
**Verify:** CF Access token lifetime config; refresh-token / re-auth flow; idle-session timeout.
**Expect:** Documented + < 24h. Re-auth required after configurable idle window.
**Severity:** P1.

### SEC-AUTH-4 — Claim mapping
**Verify:** Every endpoint that reads `ClaimTypes.NameIdentifier` / `nickerp:id` / `sub` is consistent. Inspection.Web, NickFinance.Web, Portal all map the same claim → user-id semantics.
**Expect:** Single canonical claim used across modules; no `MapInboundClaims` surprises (v1 hit `sid` → `ClaimTypes.Sid` rename — v2 must avoid).
**Severity:** P1.

### SEC-AUTH-5 — Anonymous endpoints inventory
**Verify:** `grep -rn "[AllowAnonymous]" apps/ modules/ platform/`. Each match is documented + justified.
**Expect:** Only `/healthz`, `/healthz/workers`, `/api/edge/replay` (HMAC-authenticated separately), `AcceptInvite.razor` (token-authenticated separately), Cloudflare Access pre-auth pages. No "anonymous controllers".
**Severity:** P0 if undocumented anonymous endpoint exposes data.

### SEC-AUTH-6 — Invite token posture
**Verify:** `InviteService` HMAC hashing (no raw tokens stored); single-use enforced via unique partial index on `TokenHash WHERE active`; expiry honored; revocation path tested.
**Expect:** Per-user single-use; expired tokens rejected; revoked tokens rejected; tokens never logged in plaintext.
**Severity:** P0.

### SEC-AUTH-7 — Edge node auth (per-edge HMAC API keys)
**Verify:** `EdgeAuthHandler` validates per-node HMAC API key BEFORE tenant resolution; bad keys do NOT downgrade to legacy `X-Edge-Token`. Test from `Bad_per_node_key_does_not_downgrade_to_legacy`.
**Expect:** Test passes; manual curl with bad key returns 401, no fallback path observed.
**Severity:** P0.

### SEC-AUTH-8 — Logout clears session state
**Verify:** Logout endpoint exists; clears auth cookies; revokes refresh token if applicable; redirects to CF Access logout.
**Expect:** Post-logout, all subsequent requests return 401.
**Severity:** P2.

---

## AUTHZ — Authorization

### SEC-AUTHZ-1 — `[Authorize]` default
**Verify:** Top-level `RequireAuthorization()` on the endpoint pipeline (or `[Authorize]` on every controller); explicit `[AllowAnonymous]` for the SEC-AUTH-5 inventory only.
**Expect:** No untested endpoint silently allows anonymous.
**Severity:** P0.

### SEC-AUTHZ-2 — Per-tenant scope enforcement
**Verify:** Every entity query path resolves `app.tenant_id` from the request pipeline (`TenantConnectionInterceptor`). Cross-tenant queries (export, purge) use the documented register pattern.
**Expect:** Unit + integration tests confirm a user from tenant A cannot read tenant B's data via API or Razor page.
**Severity:** P0.

### SEC-AUTHZ-3 — Admin-only endpoint coverage
**Verify:** Tenant management (`/tenants`, `/tenants/{id}`), rules admin (`/admin/rules`), ICUMS admin (`/admin/icums/*`), platform-admin endpoints all require admin scope/role. `RequireAuthorization("PlatformAdmin")` or equivalent.
**Expect:** Non-admin users get 403 on every admin route.
**Severity:** P0.

### SEC-AUTHZ-4 — Antiforgery on form posts
**Verify:** All Razor pages with `<EditForm>` honor antiforgery tokens. AcceptInvite.razor is `[AllowAnonymous]` but still antiforgery-protected.
**Expect:** Form posts without antiforgery token return 400.
**Severity:** P1.

### SEC-AUTHZ-5 — Sensitive operation audit
**Verify:** Every mutation that crosses tenant boundary, modifies tenant lifecycle, revokes/issues tokens, exports data — emits an `audit.events` row with the actor's user-id.
**Expect:** `SELECT COUNT(*) FROM audit.events WHERE EventType IN (...) GROUP BY EventType` shows volume consistent with operations.
**Severity:** P0.

### SEC-AUTHZ-6 — File / artifact access controls
**Verify:** `/api/tenant-exports/{id}/download` gates on `Status=Completed && !Revoked && !Expired` server-side; never serves files via direct path; check counter increments.
**Expect:** Revoked/expired exports return 410 Gone; download counter increments only on successful response.
**Severity:** P0.

### SEC-AUTHZ-7 — Per-tenant module activation
**Verify:** `tenancy.tenant_module_settings` (added Sprint 29) enforced at the launcher level — disabled modules don't render tiles + return 403 if the user navigates directly.
**Expect:** Disabling a module immediately hides it from the launcher; deep links return 403.
**Severity:** P1 (defense-in-depth; CF Access can also gate at the network level).

---

## TENANT — Tenant isolation (RLS)

### SEC-TENANT-1 — `TenantConnectionInterceptor` registered for every DbContext
**Verify:** `grep -rn "TenantConnectionInterceptor" platform/ modules/`. Every DbContext that touches tenant data registers the interceptor.
**Expect:** All five v2 DbContexts (Audit, Identity, Inspection, NickFinance, Tenancy where applicable) register the interceptor.
**Severity:** P0.

### SEC-TENANT-2 — `app.tenant_id` set on every connection open
**Verify:** Light-weight integration test that opens a connection without a tenant context and confirms `current_setting('app.tenant_id', true)` returns `'0'` (fail-closed sentinel) — NOT empty, NOT NULL, NOT `'1'`.
**Expect:** Default unset = `'0'`. RLS USING clauses fail-closed.
**Severity:** P0.

### SEC-TENANT-3 — `FORCE ROW LEVEL SECURITY` on every tenant-owned table
**Verify:**
```sql
SELECT schemaname, tablename, rowsecurity, forcerowsecurity
FROM pg_tables
WHERE schemaname IN ('audit','identity','inspection','nickfinance','tenancy')
  AND tablename NOT IN ('__EFMigrationsHistory','tenants','tenant_purge_log','tenant_export_requests')
ORDER BY 1,2;
```
**Expect:** `rowsecurity=true AND forcerowsecurity=true` for every row. Exceptions are root tenancy tables (documented).
**Severity:** P0.

### SEC-TENANT-4 — System-context register up-to-date
**Verify:** Every code-path match of `grep -r "SetSystemContext" --include='*.cs' platform/ modules/ apps/` corresponds to an entry in `docs/system-context-audit-register.md`. No new unregistered callers.
**Expect:** Register count == grep count. Sprint 25 export tooling is correctly NOT in the register (uses raw connection + per-tenant SET pattern).
**Severity:** P0.

### SEC-TENANT-5 — System-context table opt-in clauses present
**Verify:**
```sql
SELECT schemaname, tablename, policyname, qual, with_check
FROM pg_policies
WHERE qual LIKE '%''-1''%' OR with_check LIKE '%''-1''%'
ORDER BY 1,2,3;
```
**Expect:** Output matches the "Tables that opt in to system context" section of the register. No table outside that list has the `'-1'` clause.
**Severity:** P0.

### SEC-TENANT-6 — Sentinel `'-1'` is never the user's tenant ID
**Verify:** `tenancy.tenants` has no row with `Id = -1`. Provisioning blocks attempts to allocate id=-1.
**Expect:** `SELECT COUNT(*) FROM tenancy.tenants WHERE "Id" = -1` returns 0.
**Severity:** P0.

### SEC-TENANT-7 — Soft-deleted tenant data is invisible to non-platform-admin queries
**Verify:** A user from tenant A whose tenant is `SoftDeleted` cannot read any of A's data via API. Platform admin can.
**Expect:** Test passes; soft-delete state respected by RLS or by application-layer filter.
**Severity:** P1.

### SEC-TENANT-8 — Hard-purge audit trail
**Verify:** Every `TenantPurgeOrchestrator.PurgeAsync` call writes a `tenancy.tenant_purge_log` row with rowcount-per-table breakdown.
**Expect:** Recent purges have a corresponding log row.
**Severity:** P1.

### SEC-TENANT-9 — Cross-tenant export gating
**Verify:** `/api/tenant-exports/{id}/download` rejects users not in the export's `RequestedByUserId` AND not platform-admin (if scoped). `TenantExportService.DownloadExportAsync` checks `requestingUserId` server-side.
**Expect:** A user from tenant B cannot download tenant A's export even with the export ID.
**Severity:** P0.

### SEC-TENANT-10 — Edge node tenant isolation
**Verify:** Edge replay endpoint resolves tenant from the per-edge API key's row; per-event handlers use that tenant for the audit row WITH CHECK.
**Expect:** A compromised edge key for tenant A cannot inject events for tenant B (different keys, different tenants per row).
**Severity:** P0.

### SEC-TENANT-11 — Validation rule settings per-tenant
**Verify:** Sprint 28's `tenancy.tenant_validation_rule_settings` enforces per-tenant Enabled flag; no cross-tenant flag bleed.
**Expect:** Disabling a rule for tenant A leaves it active for tenant B.
**Severity:** P1.

### SEC-TENANT-12 — Tenant module activation per-tenant
**Verify:** Sprint 29's `tenancy.tenant_module_settings` enforces per-tenant module flags.
**Expect:** Disabling NickHR for tenant A leaves it active for tenant B.
**Severity:** P2 (CF Access can also gate).

---

## SECRETS — Secrets management

### SEC-SECRETS-1 — No secrets in `appsettings.json` (any environment)
**Verify:** `grep -rEn "(password|secret|key|token)\s*[:=]\s*[\"'][A-Za-z0-9]{8,}" --include='appsettings*.json' apps/ modules/ platform/`
**Expect:** No matches with real-looking secrets. Placeholders / dev-only test values acceptable; flag for review.
**Severity:** P0.

### SEC-SECRETS-2 — Secrets in environment variables only
**Verify:** `ConnectionStrings__Platform`, `ConnectionStrings__Inspection`, `ConnectionStrings__NickFinance`, `Email__Smtp__Password`, `EdgeNode:HmacKey`, etc. all sourced from machine env vars.
**Expect:** Service registry env vars + machine env vars hold real secrets; appsettings has only placeholder names.
**Severity:** P0.

### SEC-SECRETS-3 — DB role separation
**Verify:** `nscim_app` is non-superuser, runs the app; superuser (`postgres` / equivalent) only used for migrations + admin tools.
**Expect:** `\du nscim_app` shows `Cannot login: false`, `Superuser: false`, `Create DB: false`, `Create role: false`.
**Severity:** P0.

### SEC-SECRETS-4 — `nickerp_repl` replication role
**Verify:** Per runbook 09 §5.3, replication role exists with `LOGIN REPLICATION` only. No CRUD on app schemas.
**Expect:** `\du nickerp_repl` shows replication-only.
**Severity:** P1.

### SEC-SECRETS-5 — pgbackrest cipher pass (cloud repos)
**Verify:** If pgbackrest configured with cloud-backed repo, `repo1-cipher-pass` is set + stored in operator password manager (not in appsettings).
**Expect:** Backup files at the cloud repo are encrypted; restoring without the cipher pass fails.
**Severity:** P0 (if cloud-backed).

### SEC-SECRETS-6 — Cloudflare Access JWT signing key sourcing
**Verify:** CF Access public keys fetched from JWKS URI at runtime (with caching), not embedded.
**Expect:** Key rotation works without redeploy.
**Severity:** P1.

### SEC-SECRETS-7 — Edge node HMAC key issuance / rotation
**Verify:** Per-edge keys issued via admin flow; revocation flips a row flag; rotation procedure documented.
**Expect:** Admin can revoke an edge key; subsequent edge requests fail.
**Severity:** P1.

### SEC-SECRETS-8 — Run secret-scan tool
**Verify:** `tools/security-scan/check-secrets.ps1` executed against current tree; report shows zero unexpected matches.
**Expect:** Clean report (or every match documented as a false-positive).
**Severity:** P1.

---

## TLS — Network + transport security

### SEC-TLS-1 — HTTPS-only on all entry points
**Verify:** `apps/portal/Program.cs` + each module Program.cs enforces `UseHttpsRedirection()` + `UseHsts()` for non-Development.
**Expect:** HTTP → 308 to HTTPS in pilot env; HSTS header present.
**Severity:** P0.

### SEC-TLS-2 — HSTS posture
**Verify:** `Strict-Transport-Security: max-age=31536000; includeSubDomains` (or comparable).
**Expect:** Header set on every HTTPS response.
**Severity:** P1.

### SEC-TLS-3 — TLS version
**Verify:** Kestrel allows TLS 1.2 + 1.3 only; TLS 1.0 / 1.1 disabled.
**Expect:** `openssl s_client -connect host:443 -tls1` fails.
**Severity:** P1.

### SEC-TLS-4 — SMTP TLS posture
**Verify:** `SmtpEmailSender` defaults to `StartTlsWhenAvailable`; `UseTls=false + AllowInsecure=true` is opt-in only.
**Expect:** Default config requires TLS; warning logged for plaintext sends.
**Severity:** P0.

### SEC-TLS-5 — DB connection encryption
**Verify:** Connection strings include `Ssl Mode=Require` (or equivalent) for production targets.
**Expect:** Connections to PG fail if server doesn't offer TLS.
**Severity:** P1.

### SEC-TLS-6 — Cert pinning (where applicable)
**Verify:** Cross-service HTTPS calls pin certs/thumbprints (per v1 lesson — `NICKSCAN_API_CERT_THUMBPRINT` must be the leaf cert thumbprint).
**Expect:** Pinning configured; rotation procedure documented.
**Severity:** P2 (v2-specific call: skip if not used).

### SEC-TLS-7 — Edge node TLS
**Verify:** Edge nodes communicate with central over TLS; no plaintext fallback.
**Expect:** Edge replay endpoint rejects non-TLS connections.
**Severity:** P0.

---

## DB — Database posture

### SEC-DB-1 — PostgreSQL version meets locked target
**Verify:** Per runbook 11, production is PG17. Run `SELECT version();` on every node.
**Expect:** All nodes report PG17.x.
**Severity:** P1.

### SEC-DB-2 — `pg_hba.conf` posture
**Verify:** Replication line scoped to standby IP only; app connections require password (not `trust`); no wildcard hosts.
**Expect:** No `trust` for non-localhost; replication restricted.
**Severity:** P0.

### SEC-DB-3 — Minimum grants
**Verify:**
```sql
SELECT grantor, grantee, table_schema, table_name, privilege_type
FROM information_schema.table_privileges
WHERE grantee = 'nscim_app'
ORDER BY 1,2,3,4,5;
```
**Expect:** SELECT/INSERT/UPDATE only (DELETE only on documented allowlists). No DDL.
**Severity:** P1.

### SEC-DB-4 — pgbackrest configured + first full backup taken
**Verify:** Per runbook 10, stanza created; full backup done; recurring cadence wired.
**Expect:** `pgbackrest info` shows recent full + WAL archive timestamps.
**Severity:** P0 (block pilot if no backup).

### SEC-DB-5 — Quarterly restore drill scheduled
**Verify:** First drill done; calendar event for next drill.
**Expect:** Drill log + scheduled-task / cron entry.
**Severity:** P1.

### SEC-DB-6 — Streaming standby online
**Verify:** Per runbook 09, primary + standby both online; replication lag < 60s.
**Expect:** `pg_stat_replication` on primary shows the standby; lag is bounded.
**Severity:** P0.

### SEC-DB-7 — RLS forced (cross-check with SEC-TENANT-3)
See SEC-TENANT-3.

### SEC-DB-8 — Audit trail tables retain history
**Verify:** `audit.events` partitioning + retention policy in place; no auto-delete < 12 months.
**Expect:** Retention documented; partitioning if applicable (FU-audit-events-partitioning is on the deferred list — note current state).
**Severity:** P1.

### SEC-DB-9 — Connection pool tuned
**Verify:** Npgsql max pool size + lifetime configured per connection string. No pool exhaustion under expected load (cross-check with perf plan).
**Expect:** Pool size matches expected concurrency profile.
**Severity:** P1.

### SEC-DB-10 — Migration catalog reconciled
**Verify:** `nickerp_platform.tenancy.__EFMigrationsHistory`, `nickerp_inspection.public.__EFMigrationsHistory`, `nickerp_nickfinance.public.__EFMigrationsHistory` (and other module schemas) all match the source tree's `Migrations/` directories.
**Expect:** No unapplied migrations on disk; no rows in DB without source.
**Severity:** P0.

---

## AUDIT — Audit + observability

### SEC-AUDIT-1 — Audit-events writes are non-bypassable
**Verify:** Every state-change endpoint writes an `audit.events` row inside the same transaction or via a guaranteed-delivery pattern.
**Expect:** Events have `OccurredAt`, `TenantId`, `UserId` (or system-zero), `EventType`, `EntityType`, `EntityId`, `Properties`.
**Severity:** P0.

### SEC-AUDIT-2 — Audit-events retention
**Verify:** No purge job deletes audit events < 12 months. Hard-purge of a tenant DOES purge their audit rows (intentional, per `TenantPurgeOrchestrator`).
**Expect:** Retention documented.
**Severity:** P1.

### SEC-AUDIT-3 — PII redaction in logs
**Verify:** Tokens, JWT bearer values, passwords, plaintext invite tokens NEVER appear in logs (even at Trace level). Log-scrubbers in place.
**Expect:** Searching production logs for known-bad patterns returns nothing.
**Severity:** P0.

### SEC-AUDIT-4 — Correlation IDs propagate
**Verify:** Every cross-service HTTP call passes a correlation ID; Seq dashboards show end-to-end traces.
**Expect:** A pilot-scope request can be traced across portal → inspection → audit DB.
**Severity:** P1.

### SEC-AUDIT-5 — Sensitive event coverage
**Verify:** Audit events emitted for: tenant create/suspend/soft-delete/hard-purge, invite issue/redeem/revoke, export request/download/revoke, rule enable/disable, edge-key issue/revoke, validation failures.
**Expect:** Each audit type has at least one row in production after exercising the corresponding flow.
**Severity:** P0.

### SEC-AUDIT-6 — Failed-auth audit
**Verify:** Failed auth attempts logged (rate-limited to avoid log floods); IP + UA captured.
**Expect:** Brute-force detection feasible from logs.
**Severity:** P1.

### SEC-AUDIT-7 — System-context call audit
**Verify:** Every `SetSystemContext()` call site emits a structured log entry with reason + caller. The register backs this up.
**Expect:** Production logs show `SystemContext: <reason>` for projector / export / edge-auth flows.
**Severity:** P1.

### SEC-AUDIT-8 — Alerting wired
**Verify:** Per runbooks 09 §10 + 10 §10, alerts on: replication lag, slot disk-fill, standby disconnect, backup-lag, archive-failure, repo-disk, backup-corruption, anomalous failed-auth rate.
**Expect:** Alert receivers configured (Seq / email / etc.); test alert fires successfully.
**Severity:** P0.

---

## EDGE — Edge node posture

### SEC-EDGE-1 — Per-edge HMAC API key rotation
See SEC-SECRETS-7.

### SEC-EDGE-2 — Edge SQLite buffer protection
**Verify:** Edge node SQLite at-rest protection — file permissions restrict to local service account; encryption-at-rest if data sensitivity warrants.
**Expect:** Buffer file readable only by the edge service account.
**Severity:** P1.

### SEC-EDGE-3 — Edge replay event-type allowlist
**Verify:** Edge replay endpoint accepts only the documented event-type hints (`audit.event.replay`, `inspection.scan.captured`, `inspection.scanner.status.changed`). Unknown hints rejected.
**Expect:** Test from Sprint 17 confirms unsupported hints fail gracefully.
**Severity:** P1.

### SEC-EDGE-4 — Edge-replay timestamp sanity
**Verify:** Future timestamps rejected (clock-skew protection).
**Expect:** Replay events with `OccurredAt > now() + tolerance` rejected.
**Severity:** P2.

### SEC-EDGE-5 — Edge node identity per scanner location
**Verify:** Each pilot scanner location has its own edge identity row in `audit.edge_node_authorizations`. No shared keys across sites.
**Expect:** Inventory of edge keys === inventory of edge boxes.
**Severity:** P0.

### SEC-EDGE-6 — Edge → central network posture
See SEC-TLS-7.

### SEC-EDGE-7 — Edge buffer flush windowing
**Verify:** Buffer-flush rate-limited so a long-offline edge doesn't DOS the central pipeline on reconnect.
**Expect:** Rate-limit configured; documented in runbook.
**Severity:** P2.

---

## MOD — Module-specific

### SEC-MOD-INSP-1 — Validation rules engine in place
**Verify:** Sprint 28's `IValidationRule` engine + per-tenant rule settings + audit on every result. CustomsGh rules registered (port-match, Fyco-direction, CMR-port).
**Expect:** A test case triggers validation; results persist as Findings; audit row written.
**Severity:** P1.

### SEC-MOD-INSP-2 — Case detail authorization
**Verify:** A user can only access cases their `AnalysisService` membership permits. VP6 first-claim-wins under shared visibility.
**Expect:** Cross-service access blocked by both DB-side (RLS via tenant) and app-side (service membership check).
**Severity:** P0.

### SEC-MOD-INSP-3 — ICUMS submission gating
**Verify:** Manual ICUMS pull / requeue / dashboard endpoints require admin scope.
**Expect:** Non-admins get 403.
**Severity:** P0.

### SEC-MOD-INSP-4 — External system bindings authorized
**Verify:** External system instance creation / scope changes are admin-only + audit-trailed.
**Expect:** Audit row + admin scope check on every binding mutation.
**Severity:** P1.

### SEC-MOD-INSP-5 — OCR / inference plugin trust
**Verify:** Inference plugins loaded from filesystem (not network); manifest reviewed; signing posture (currently deferred — note as known gap).
**Expect:** Pilot uses only the in-tree plugins; no third-party drop-ins.
**Severity:** P1.

### SEC-MOD-FIN-1 — FX rate publish gated
**Verify:** Only finance admins can call `FxRatePublishService.PublishAsync`. System-context flip is documented in register.
**Expect:** Non-admin call returns 403.
**Severity:** P0.

### SEC-MOD-FIN-2 — Voucher disbursement audit
**Verify:** Every voucher disbursement emits an audit event with actor + amount + recipient tenant.
**Expect:** Audit trail complete.
**Severity:** P0.

### SEC-MOD-HR-1 — NickHR data isolation
**Verify:** NickHR clone shares CF Access auth + Tenancy + Identity + Web.Shared with the suite. Per-tenant data boundaries hold.
**Expect:** Cross-tenant HR data access blocked.
**Severity:** P0.

### SEC-MOD-HR-2 — Biometric attendance posture
**Verify:** v1's `/api/attendance/biometric/*` no longer leaks anonymous; v2 inherits the strict-auth fix.
**Expect:** All HR endpoints require auth.
**Severity:** P0.

### SEC-MOD-HR-3 — JWT key rotation (NickHR-specific)
**Verify:** Per v1 lesson (NickHR JWT key in env var; signing + validation read same source). v2 NickHR clone preserves this; if refactor lands post-pilot, confirm rotation procedure.
**Expect:** Key rotation works without code change.
**Severity:** P1.

---

## DEP — Dependency hygiene

### SEC-DEP-1 — Run vulnerability scan
**Verify:** `tools/security-scan/run-vulnerability-scan.ps1` — produces `tools/security-scan/reports/{date}-vulnerabilities.md`.
**Expect:** Zero High / Critical findings on transitive deps.
**Severity:** P0 if any High / Critical at pilot time.

### SEC-DEP-2 — Run outdated-package scan
**Verify:** `tools/security-scan/run-dependency-audit.ps1` — produces outdated report.
**Expect:** No package > 12 months behind on a major version without justification.
**Severity:** P2.

### SEC-DEP-3 — License compatibility
**Verify:** Every third-party NuGet license is MIT / Apache-2 / BSD / similar permissive (or commercial with payment in place).
**Expect:** No GPL / AGPL / unknown-license deps.
**Severity:** P1.

### SEC-DEP-4 — MailKit version
**Verify:** MailKit ≥ 4.16 (NU1902 CVE GHSA-9j88-vvj5-vhgr resolved Sprint 21).
**Expect:** All projects on 4.16+.
**Severity:** P0.

### SEC-DEP-5 — .NET runtime
**Verify:** .NET 10 across all projects + production hosts.
**Expect:** No .NET 8 / 9 / older binaries in publish output.
**Severity:** P1.

### SEC-DEP-6 — Npgsql version
**Verify:** Npgsql 9.x (PG17-compatible per runbook 11).
**Expect:** All projects on 9.x.
**Severity:** P1.

---

## HEAD — HTTP headers + cookies

### SEC-HEAD-1 — CSP configured
**Verify:** `Content-Security-Policy` header set; no `unsafe-eval`; `unsafe-inline` only where necessary (Blazor Server requires some inline). Per v1 audit (2026-04-28), `unsafe-eval` was removed — v2 must not regress.
**Expect:** CSP present; no `unsafe-eval`; restricted sources.
**Severity:** P1.

### SEC-HEAD-2 — `X-Content-Type-Options`
**Verify:** `X-Content-Type-Options: nosniff` set.
**Expect:** Header present on every response.
**Severity:** P2.

### SEC-HEAD-3 — `X-Frame-Options` or CSP `frame-ancestors`
**Verify:** Anti-clickjacking header set.
**Expect:** Either `X-Frame-Options: DENY` or CSP `frame-ancestors 'none'`.
**Severity:** P2.

### SEC-HEAD-4 — `Referrer-Policy`
**Verify:** `Referrer-Policy: same-origin` or `strict-origin-when-cross-origin`.
**Expect:** Header present.
**Severity:** P3.

### SEC-HEAD-5 — Cookie attributes
**Verify:** Auth cookies have `Secure`, `HttpOnly`, `SameSite=Lax` (or stricter); non-auth cookies similarly secured.
**Expect:** All cookies secured per modern defaults.
**Severity:** P1.

### SEC-HEAD-6 — `Permissions-Policy`
**Verify:** Restrict access to dangerous browser features (camera, microphone, geolocation, etc.) where module doesn't need them.
**Expect:** Header denies what the module doesn't use.
**Severity:** P3.

---

## How findings get filed

For each failure during execution:

```
### AUD-{n} — {short description}
**Item:** SEC-{cat}-{n}
**Severity:** P0 | P1 | P2 | P3
**Observed:** {what you found}
**Expected:** {what should be true}
**Evidence:** {command output, screenshots, file refs}
**Owner:** {who fixes}
**Status:** Open | Investigating | Fixing | Verified
```

---

## Phase V exit criteria

- All P0 items pass.
- All P1 items either pass OR have a documented fix-before-launch ticket.
- P2 + P3 items have backlog tickets.
- The `system-context-audit-register.md` is reviewed + countersigned by a second engineer.
- The pilot site's edge keys are issued + tested.
- Backup + restore drill executed once on the pilot's data shape.

When all five lines are checked, Phase V is complete; pilot can ship.

---

## Maintenance

- Every Sprint that adds a new endpoint, table, system-context caller, or external dependency adds a checklist item here.
- Every quarterly drill updates `SEC-DB-5` with the drill log reference.
- Every dependency audit updates the running `SEC-DEP-1` baseline.
