# Pilot acceptance checklist

> **Companion to [`14-pilot-site-standup.md`](14-pilot-site-standup.md).**
> This checklist is the operator's running document during pilot
> stand-up — copy this file to `pilots/{site}/acceptance-{date}.md`
> and tick each box as you go. The pilot is signed off when **every**
> item is ticked AND the five gates on `/admin/pilot-readiness` have
> been Pass for **14 consecutive days**.
>
> Each item references:
> - The **runbook 14 section** that walks the underlying procedure.
> - A **verification command / UI** the operator runs to prove the
>   item.
> - The **expected** outcome.
>
> Items that map to a system-side gate cite the gate ID; the gate
> being Pass is the proof that the item passes. Items that map to a
> security audit (SEC-*) item are proven by the corresponding entry
> in the per-pilot `audit-{site}-{date}.md` file (see runbook 14
> §10.1).
>
> Three columns throughout: **Item** / **Verification** / **Expected**.

---

## Section 1 — Pre-flight (runbook 14 §2)

| ☐ | Item | Verification | Expected |
|---|---|---|---|
| ☐ | PG cluster on PG17 | `psql -U postgres -h $PGPRI_HOST -d postgres -c "SELECT version();"` | `PostgreSQL 17.x` |
| ☐ | Streaming standby replicating | `psql -U postgres -h $PGPRI_HOST -d postgres -c "SELECT state FROM pg_stat_replication;"` | `streaming` row present |
| ☐ | Replication lag bounded | runbook 09 §6.3 query | `apply_lag_time < 1 s` |
| ☐ | pgbackrest stanza configured | `pgbackrest --stanza=nickerp info` | Recent full + WAL archive timestamps |
| ☐ | Edge node hardware on hand | Operator inventory | One box per pilot scanner site, ≥ §2.3 minimum spec |
| ☐ | Cooperation MOU signed | Paper document | Signed by named customs counterpart, includes graceful-failure clause |
| ☐ | Connectivity baseline measured | 14-day probe log | Uptime ≥ 95%; latency p99 ≤ 500 ms; packet loss ≤ 0.5% |

## Section 2 — Site selection (runbook 14 §3)

| ☐ | Item | Verification | Expected |
|---|---|---|---|
| ☐ | Hard-gate matrix completed for each candidate | §3 output document | Each candidate marked Pass / Fail per gate 1-4 |
| ☐ | Weighted scoring run on gate-passers | §3 output document | Score table with 8 criteria × weights |
| ☐ | Pilot site chosen | §3 output document | Single named site; signed by §2.4 customs counterpart |

## Section 3 — Hardware provisioning (runbook 14 §4)

| ☐ | Item | Verification | Expected |
|---|---|---|---|
| ☐ | Postgres primary box racked + on PG17 | `psql -U postgres -h $PGPRI_HOST -d postgres -c "SELECT version();"` | PG17.x |
| ☐ | Postgres standby box racked + on PG17 | `psql -U postgres -h $PGSTBY_HOST -d postgres -c "SELECT version();"` | PG17.x |
| ☐ | Same OS family on both nodes | `uname -a` on both (or `Get-ComputerInfo`) | Same OS family + bit-width |
| ☐ | Edge node(s) racked + powered | Edge `/edge/healthz` | `status=Healthy` |
| ☐ | Linux backup VM provisioned (if SSH posture) | `ssh pgbackrest-backup hostname` | Returns hostname |
| ☐ | Sizing review countersigned by v2 dev team | §3.5 output document | Signed sizing review attached |

## Section 4 — Network setup (runbook 14 §5)

| ☐ | Item | Verification | Expected |
|---|---|---|---|
| ☐ | Real CA-signed cert deployed | `openssl s_client -connect <host>:443 -servername <host>` | Valid cert chain to a trusted CA |
| ☐ | TLS 1.0/1.1 disabled | `openssl s_client -connect <host>:443 -tls1` | Connection refused |
| ☐ | HSTS header set | `curl -sI https://<host>/healthz` | `Strict-Transport-Security: max-age=31536000; includeSubDomains` |
| ☐ | CF Access application configured | `curl -fsSL https://<pilot>.cloudflareaccess.com/cdn-cgi/access/certs` | JSON keyset returned |
| ☐ | Edge → central encrypted path verified | `curl -fsSL https://<central>:5410/healthz` from edge | 200 + "Healthy" |
| ☐ | Backup-host SSH key works (if SSH posture) | `sudo -u postgres ssh pgbackrest-backup pgbackrest --version` | Returns pgbackrest version string |
| ☐ | Standby reachable from primary | `nc -vz <standby-host> 5432` | Connection succeeds |

## Section 5 — Tenant provisioning (runbook 14 §6)

| ☐ | Item | Verification | Expected |
|---|---|---|---|
| ☐ | Pilot tenant exists | `SELECT "DisplayName","State" FROM tenancy.tenants WHERE "Id" = <pilot-tenant-id>;` | One row, `State=Active` (0) |
| ☐ | First-user invite issued | `SELECT "Email","IssuedAt" FROM identity.invite_tokens WHERE "TenantId" = <pilot-tenant-id>;` | At least one row |
| ☐ | First-user invite redeemed | Same query, `RedeemedAt IS NOT NULL` | RedeemedAt populated |
| ☐ | Customs operator can sign in | Customs operator logs in to portal | Reaches `/launcher` without 403 |
| ☐ | MOU mirrored as tenant settings | `/admin/tenant-settings` | `pilot.cooperation_mou.signed_at` + counterpart_name + counterpart_email + location_uri + expires_at all populated |

## Section 6 — Scanner + adapter onboarding (runbook 14 §7)

| ☐ | Item | Verification | Expected |
|---|---|---|---|
| ☐ | One ScannerDeviceInstance per physical scanner | `/scanners` page or `SELECT * FROM inspection.scanner_device_instances WHERE "TenantId" = <id>;` | One row per physical box, `IsActive=true` |
| ☐ | Each scanner has registered plugin | Same query, `TypeCode` column | Matches an in-tree plugin (`fs6000` / `ase`) — never `mock` |
| ☐ | Annex B questionnaire completed per device-type | `SELECT DISTINCT "ScannerDeviceTypeId" FROM inspection.scanner_onboarding_responses WHERE "TenantId" = <id>;` | One row-set per device-type (12 fields each) |
| ☐ | Per-edge HMAC API key issued | `SELECT "KeyPrefix","IssuedAt" FROM audit.edge_node_authorizations WHERE "TenantId" = <id>;` | One row per edge box; no shared keys |
| ☐ | Edge box authenticates to central | Edge replays test event | 200 from `/api/edge/replay`, audit row written |

## Section 7 — First-pass smoke (runbook 14 §8)

Maps to runbook 14 §8 + drives gates 1, 2, 5 to Pass.

| ☐ | Item | Verification | Expected |
|---|---|---|---|
| ☐ | Synthetic scan triggered per scanner | `SELECT COUNT(*) FROM inspection.scan_artifacts WHERE "TenantId" = <id>;` | > 0; one or more per scanner |
| ☐ | Edge round-trip audit row present | `SELECT COUNT(*) FROM audit.events WHERE "TenantId" = <id> AND "EventType" = 'inspection.scan.captured' AND "Payload"->>'replay_source' = 'edge';` | > 0 |
| ☐ | Analyst inbox shows synthetic case | `/reviews/queue` as customs operator | At least one row, the synthetic case |
| ☐ | Six-event audit chain complete | runbook 14 §8.4 audit-row table | All six event types present per case |
| ☐ | Synthetic case decisioned | `SELECT COUNT(*) FROM audit.events WHERE "EventType" = 'nickerp.inspection.verdict_set' AND "TenantId" = <id>;` | > 0 |
| ☐ | Gate `gate.scanner.adapter` Pass | `/admin/pilot-readiness` | Pass with proof event |
| ☐ | Gate `gate.edge.roundtrip` Pass | `/admin/pilot-readiness` | Pass with proof event |
| ☐ | Gate `gate.multi_tenant.invariants` Pass | `/admin/pilot-readiness` | All 3 sub-pills Pass |

## Section 8 — Phase V execution (runbook 14 §10)

Each SEC-* item is proven by the corresponding entry in the
per-pilot `audit-{site}-{date}.md` (see
[`../security/audit-checklist-2026.md`](../security/audit-checklist-2026.md)
for the full ~89 SEC-* set). The summary entries below are the
high-watermark items the operator confirms even without re-walking
the full audit.

### 8.1 Phase V security audit (runbook 14 §10.1)

| ☐ | Item | Verification | Expected |
|---|---|---|---|
| ☐ | Per-pilot audit file created + signed off | `pilots/{site}/audit-{date}.md` | All P0 items Pass; all P1 either Pass or have ticket; P2/P3 have backlog tickets |
| ☐ | SEC-AUTH-1 — CF Access JWT validation enabled | Audit file | Pass |
| ☐ | SEC-AUTH-7 — per-edge HMAC key validates before tenant resolution | Audit file | Pass — `Bad_per_node_key_does_not_downgrade_to_legacy` test passes |
| ☐ | SEC-AUTHZ-2 — per-tenant scope enforcement | Audit file | Cross-tenant test confirms tenant A cannot see tenant B's data |
| ☐ | SEC-TENANT-1 — TenantConnectionInterceptor registered for every DbContext | Audit file | All 5 DbContexts |
| ☐ | SEC-TENANT-3 — FORCE ROW LEVEL SECURITY on every tenant table | runbook 14 §9.2 multi-tenant gate | All eligible tables `forcerowsecurity=true` |
| ☐ | SEC-SECRETS-1 — no secrets in appsettings | `tools/security-scan/run-trufflehog.ps1 -Mode history` | Zero `Verified` findings |
| ☐ | SEC-DB-4 — pgbackrest configured + first full backup taken | `pgbackrest info` | Recent full + WAL timestamps |
| ☐ | SEC-DB-6 — streaming standby online | `pg_stat_replication` query | Standby row present, lag bounded |
| ☐ | SEC-AUDIT-3 — PII redaction in logs | Log scrub spot-check | No tokens / passwords / invite-token plaintext in any log |
| ☐ | SEC-EDGE-5 — per-site edge keys | runbook 14 §7.4 query | Distinct keys per edge box; no sharing |
| ☐ | SEC-DEP-1 — vulnerability scan clean | `tools/security-scan/run-vulnerability-scan.ps1` | Zero High / Critical |
| ☐ | system-context-audit-register reviewed + countersigned | `docs/system-context-audit-register.md` | Second-engineer signature attached |

### 8.2 Phase V perf load test (runbook 14 §10.2)

| ☐ | Item | Verification | Expected |
|---|---|---|---|
| ☐ | 1x load test passes acceptance gates | `tests/NickERP.Perf.Tests/` results report | Every endpoint p99 within budget per `docs/perf/test-plan.md` §3.1 |
| ☐ | 5x load test runs and degrades gracefully | Same results report | p99 within degraded-mode tolerance per perf plan §3.2 |
| ☐ | 10x stress test characterised | Same results report | Breaking-point latency / error rate documented (informative only) |
| ☐ | No pool exhaustion at 1x | Npgsql pool metrics | Active connections < `Maximum Pool Size` throughout test |

### 8.3 Backup + restore drill (runbook 14 §10.3)

| ☐ | Item | Verification | Expected |
|---|---|---|---|
| ☐ | Full restore drill executed | runbook 10 §7 walk | Restored cluster passes runbook 10 §6 verification |
| ☐ | PITR drill executed | runbook 10 §8 walk | Cluster restored to a chosen recovery target |
| ☐ | Drill log committed | `pilots/{site}/restore-drill-{date}.md` | Operator-written walkthrough archived |

## Section 9 — Real-traffic cutover (runbook 14 §11)

### 9.1 Feature-flag ramp

| ☐ | Item | Verification | Expected |
|---|---|---|---|
| ☐ | Day 1 — `pilot.real_traffic.percent_routed = 10` set | `/admin/feature-flags` | Flag exists, value `10` |
| ☐ | Day 1 — `pilot.real_traffic.scan_capture_enabled = true` set | Same | true |
| ☐ | Day 3 — flag value advanced to `50` | Same | value `50` |
| ☐ | Day 7 — flag value advanced to `100` | Same | value `100` |
| ☐ | Each flip audited | `SELECT * FROM audit.events WHERE "EventType" = 'nickerp.tenancy.feature_flag_toggled' AND "TenantId" = <id>;` | Three rows minimum (one per ramp step) |

### 9.2 Operator + analyst training

| ☐ | Item | Verification | Expected |
|---|---|---|---|
| ☐ | Day 1 trained operators count ≥ floor | Pilot documentation | At least 2 trained analysts on day 1 (no single point of failure) |
| ☐ | Each trained analyst demonstrated case decisioning | Synthetic case demonstration log | One demo per trained analyst, captured in pilot doc |
| ☐ | Pages walked: launcher / cases / reviews / admin / notifications | Training session record | Training material covers all surfaces from runbook 14 §11.2 |

### 9.3 Seven-day soak

| ☐ | Item | Verification | Expected |
|---|---|---|---|
| ☐ | Daily check-in occurred (day 7-14) | Pilot doc | One log entry per day |
| ☐ | All 5 gates Pass every day for 14 consecutive days | `/admin/pilot-readiness` daily snapshot | No single gate Fail; soak does not reset |
| ☐ | Gate `gate.analyst.decisioned_real_case` Pass | `/admin/pilot-readiness` | Pass with proof event referencing a non-synthetic case |
| ☐ | Gate `gate.external_system.roundtrip` Pass | `/admin/pilot-readiness` | Pass with proof event referencing accepted submission |
| ☐ | Seq dashboards reviewed daily | Pilot doc | Daily anomaly review log |
| ☐ | Weekend abbreviated drill executed | Pilot doc | At least one drill log per weekend in soak |

### 9.4 Sign-off

| ☐ | Item | Verification | Expected |
|---|---|---|---|
| ☐ | Customs operator signed off in writing | Paper document | Signed by named §2.4 counterpart |
| ☐ | Customs sign-off mirrored to setting | `/admin/tenant-settings` | `pilot.signoff.customs_signed_at` populated |
| ☐ | Operator signed off | Same | `pilot.signoff.operator_signed_at` populated within 24 h |
| ☐ | v2 dev team lead signed off | Same | `pilot.signoff.dev_team_signed_at` populated within 24 h |
| ☐ | All three signatures within a 24 h window | Compare timestamps | Max(times) - min(times) < 24 h |

---

## Final acceptance gate

When **every box above is ticked** AND the five gates on
`/admin/pilot-readiness` have been Pass for 14 consecutive days,
the pilot is **signed off** and the tenant transitions to "live"
per [`14-pilot-site-standup.md`](14-pilot-site-standup.md) §12.1.

| ☐ | Final gate | Confirmation |
|---|---|---|
| ☐ | All Section 1-9 boxes ticked | Walk this document; every box is `☐` → `☑` |
| ☐ | Five gates Pass for 14 consecutive days | `/admin/pilot-readiness` history |
| ☐ | No P0 or P1 finding open | Per-pilot audit file |
| ☐ | Pilot tenant lifecycle transitioned | runbook 14 §12.1 — flags removed; runbook 01-13 active |

If any final-gate box stays `☐`, the pilot is **not** signed off.
Walk back through the failing item; resolve; re-tick.

---

## Failure path

If the pilot fails per [`14-pilot-site-standup.md`](14-pilot-site-standup.md)
§12.2, switch to the rollback section instead of completing this
checklist.

| ☐ | Rollback step | Reference |
|---|---|---|
| ☐ | Cutover flags flipped off | runbook 14 §12.3 step 1 |
| ☐ | Tenant soft-deleted | runbook 14 §12.3 step 2 |
| ☐ | Final tenant export delivered | runbook 14 §12.3 step 3 |
| ☐ | Edge nodes decommissioned + keys revoked | runbook 14 §12.3 step 4 |
| ☐ | Postmortem written | runbook 14 §12.3 step 5 |
| ☐ | Hard-purge after retention window | runbook 14 §12.3 step 6 |

A completed-but-failed checklist is also archived in the pilot
documentation — the failure record is the input to the next
attempt's §3 site re-selection.
