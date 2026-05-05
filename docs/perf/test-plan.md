# NickERP v2 — Phase V perf test plan

**Status:** Plan-of-record for the perf testing that runs as part of Phase V (post-pilot-site-lock). Companion to `docs/security/audit-checklist-2026.md`. The harness lives at `tests/NickERP.Perf.Tests/`; this document defines what to measure, what to expect, and what blocks pilot.

**Scope:** Pilot site (Kotoka or Takoradi per plan §13). Single-region, single-pilot-tenant. Multi-tenant load testing post-pilot.

---

## 1. Concurrency profile (pilot expectations)

Per plan §13, the realistic pilot candidates are Kotoka (KIA Cargo, low-medium volume) or Takoradi (medium maritime). Tema's volume profile is too aggressive for first-pilot blast radius.

### Estimated pilot daily volumes

| Site | Containers / day | Peak hour ratio | Concurrent analysts | Edge nodes |
|---|---|---|---|---|
| Kotoka (KIA Cargo) | 50-150 | 3x average | 3-7 | 1-2 |
| Takoradi | 100-300 | 2.5x average | 5-12 | 2-3 |
| Tema (post-pilot) | 500-1000 | 2x average | 15-30 | 4-8 |

### Translated to RPS

- Kotoka peak: 150 cases/day × 3x peak ratio / 6h day-shift = **~12.5 cases/hour peak**, ~0.2 RPS for case-create alone
- Takoradi peak: 300 × 2.5 / 6h = **~125 cases/hour**, ~0.35 RPS
- Edge replay (per node, every 30s buffer flush, ~5 events/flush): ~0.17 RPS per node × 3 = **~0.5 RPS** at Takoradi peak
- Analyst page loads (typical workflow): ~5 page loads per case-decision × 12 cases/hour = **~1 RPS** of analyst HTTP traffic at Takoradi peak

### Headroom multipliers

The system must comfortably hit pilot peak. Phase V tests load at:
- **1x** — pilot peak — must pass acceptance gates
- **5x** — tema-shaped projection — should pass with degraded but acceptable latency
- **10x** — stress / breaking-point discovery — finds where it falls over (informative; not a gate)

---

## 2. Endpoint catalogue

### 2.1 Hot path (pilot-critical)

| ID | Endpoint | Method | Auth | Pilot RPS (1x) |
|---|---|---|---|---|
| EP-001 | `/api/inspection/cases` | POST | CF Access JWT | 0.35 |
| EP-002 | `/api/inspection/cases/{id}` | GET | CF Access JWT | 1.5 |
| EP-003 | `/api/inspection/cases/{id}/scans` | GET | CF Access JWT | 1.0 |
| EP-004 | `/api/inspection/cases/{id}/decision` | POST | CF Access JWT | 0.35 |
| EP-005 | `/api/edge/replay` | POST | per-edge HMAC | 0.5 |
| EP-006 | `/api/audit/events` (POST direct write — internal use) | POST | service auth | 1.0 |
| EP-007 | `/api/inspection/annotations` | POST | CF Access JWT | 0.5 |
| EP-008 | `/healthz` | GET | none | 0.05 (probe) |

### 2.2 Admin path (lower frequency, higher latency tolerance)

| ID | Endpoint | Method | Auth | Pilot RPS (1x) |
|---|---|---|---|---|
| EP-101 | `/admin/icums/submission-queue` | GET | admin | 0.05 |
| EP-102 | `/admin/icums/download-queue` | GET | admin | 0.05 |
| EP-103 | `/admin/icums/dashboard` | GET | admin | 0.02 |
| EP-104 | `/admin/rules` | GET | admin | 0.02 |
| EP-105 | `/tenants/{id}` | GET | platform-admin | 0.01 |
| EP-106 | `/api/tenant-exports/{id}/download` | GET | platform-admin | 0.005 |

### 2.3 Razor page hot paths

| ID | Page | Auth |
|---|---|---|
| RP-001 | `/cases/{id}` (case detail with tabs) | analyst |
| RP-002 | `/cases/{id}?tab=image-gallery` | analyst |
| RP-003 | `/launcher` | any auth |
| RP-004 | `/sprint` | any auth |

---

## 3. Baseline targets (latency budget)

Targets are p50 / p95 / p99 milliseconds, measured at the load-balancer / Kestrel edge. NOT roundtrip from the analyst's PC.

### 3.1 At pilot peak (1x)

| Endpoint | p50 budget | p95 budget | p99 budget | Acceptance gate (p99) |
|---|---|---|---|---|
| EP-001 case-create | 200 ms | 500 ms | 1000 ms | **2000 ms = BLOCK** |
| EP-002 case-detail | 100 ms | 300 ms | 600 ms | **1500 ms = BLOCK** |
| EP-003 case-scans | 150 ms | 400 ms | 800 ms | 2000 ms = BLOCK |
| EP-004 decision | 250 ms | 600 ms | 1200 ms | 2500 ms = BLOCK |
| EP-005 edge-replay | 100 ms | 250 ms | 500 ms | 1500 ms = BLOCK |
| EP-006 audit-events | 50 ms | 150 ms | 300 ms | 1000 ms = BLOCK |
| EP-008 healthz | 5 ms | 15 ms | 30 ms | 100 ms = WARN |
| RP-001 case-detail page | 300 ms | 700 ms | 1500 ms | 3000 ms = BLOCK |
| RP-002 image-gallery tab | 500 ms | 1200 ms | 2500 ms | 5000 ms = BLOCK (lazy-load helps) |

### 3.2 At 5x (Tema-shaped projection)

p99 budgets relax 50%. p50/p95 relax 25%. The acceptance gate is the same — p99 over-budget at 5x = pilot ships, but we pre-buy capacity / scale-up in the post-pilot expansion plan.

### 3.3 At 10x (stress)

Informative only. The point is to find which dependency saturates first: DB pool, image-decode CPU, edge-replay SQLite IO, etc. Output of 10x is a written report identifying the bottleneck for scale planning.

---

## 4. Database load profile

### Connection pool

- Npgsql default pool size: 100
- Pilot expected concurrent connections: 5-15 (analysts) + 2-3 (edge replay batches) + N background workers
- Headroom: 50%+ idle even at 5x peak

### Query patterns

| Pattern | Expected per case | RLS overhead estimate |
|---|---|---|
| Case detail (single row + scans + findings) | 4-6 queries | ~5-10ms total RLS |
| Case create | 3-5 queries (transactional) | ~5ms |
| Case list (paged, filtered) | 1 query (with covering index) | ~10-30ms |
| Audit insert | 1 query | <1ms |
| Edge replay batch (5 events) | 5 inserts | <5ms |

### Index health

- Validate every hot-path query hits an index (not seq-scan) via `EXPLAIN ANALYZE` at expected row counts.
- Document where `pg_stat_user_indexes.idx_scan` should bump after each test run.

---

## 5. Edge node load profile

- Edge buffer flush: every 30s (configurable)
- Per-flush event count: 1-20 typical (5 mean)
- Per-flush replay request size: ~5KB-50KB (multipart-style envelope)
- Multi-tenant batches (when applicable): up to 10 tenants in one flush
- SQLite buffer disk usage: ~10MB / day / scanner at peak (rolling)

Tests must simulate:
- A long-offline (24h) edge reconnecting and flushing the backlog (rate-limit verification — see SEC-EDGE-7)
- Concurrent flushes from N edges (N=4 at 5x test)
- Mixed event-type batches (audit + scan-captured + scanner-status-changed)

---

## 6. Test tooling decision — NBomber

We use **NBomber** (https://nbomber.com — github.com/PragmaticFlow/NBomber) for these reasons:

| Property | NBomber | k6 | JMeter | Locust |
|---|---|---|---|---|
| .NET-native | ✓ — scenarios in C# | ✗ JS | ✗ Java | ✗ Python |
| CI integration | ✓ NuGet-based | partial | partial | partial |
| HTML / json reports | ✓ built-in | ✓ | ✓ | ✓ |
| Open-source license | MIT | AGPLv3 | Apache-2 | MIT |
| Maturity | mature, active | mature | very mature | mature |
| Mixed-protocol support | HTTP + custom | HTTP-focused | HTTP / TCP / JDBC / etc. | HTTP-focused |
| Familiarity to v2 team | ✓ same .NET stack | ✗ JS context-switch | ✗ Java context-switch | ✗ Python |

**Trade-off accepted:** JMeter has the deepest reporting + the largest community. NBomber wins on team-context (zero language switch) + license (MIT vs AGPLv3 for k6) + maturity-to-relevance ratio.

**Out-of-scope for Phase V (revisit post-pilot):**
- JMeter for full protocol coverage (DB-direct stress, JMS, etc.)
- Chaos engineering (gremlin, chaos-mesh)
- Multi-region perf testing

---

## 7. Acceptance gates (block-pilot criteria)

The pilot does NOT ship until:

1. Every P0 endpoint in §3.1 hits its p99 acceptance gate at 1x load.
2. EP-005 (edge-replay) handles the 24h-backlog reconnection scenario without dropping events or DOS'ing the central DB.
3. RP-002 (image-gallery) lazy-load completes within p99 5000ms even on the slowest pilot scanner site (image volumes can be 50-200 MB per case).
4. DB pool never exhausts at 5x load. Connection-acquire wait p99 < 200ms.
5. Background workers (B3 — 7 of them) do NOT contend with online-traffic latency. Worker poll cycles must complete within their poll-interval at 5x load.
6. Memory + CPU on the API host stay below 75% utilization at 1x peak. 5x can climb to 95% but must not OOM.

---

## 8. What we're NOT testing pre-pilot

- Chaos / fault-injection (network partitions, slow-disk simulations) — post-pilot
- DR / cross-region failover — post-pilot (single-region locked v0)
- Multi-tenant noisy-neighbor — post-pilot (pilot is single-tenant)
- ML inference perf (§6.1 OCR) — deferred to post-pilot per the 2026-05-04 OCR decision
- Long-haul (multi-day) sustained load — informative only; 4-hour soak is the longest planned run
- Auth-system perf (CF Access) — black-box; we trust their SLA

---

## 9. Test execution shape

Each Phase V test run produces:

```
tools/security-scan/reports/{date}-perf-summary.md
tests/NickERP.Perf.Tests/reports/{date}/
├── 1x-acceptance.html       (NBomber HTML)
├── 5x-projection.html
├── 10x-stress.html
├── edge-backlog-replay.html
└── test-plan-summary.md     (which gates passed/failed)
```

The summary feeds back into the security audit — `SEC-DB-9 Connection pool tuned` confirms via the perf reports.

---

## 10. Maintenance

- Every Sprint that adds a new hot-path endpoint adds a row to §2.1.
- Every change to a hot-path query adds a row to §4 with new EXPLAIN ANALYZE evidence.
- Every B-batch sprint (B4 validation rules, B5 completeness, etc.) updates §3 if new endpoints land.
- Pilot peak numbers in §1 update once the actual pilot site is locked + measured.

---

## 11. Open questions (deferred to Phase V kickoff)

- **Auth latency in tests.** CF Access JWT-validate path adds ~10-50ms per request; do we mock this in load tests, or hit real CF? **Decided 2026-05-05 (Sprint 52 / FU-perf-auth-mocking-decision):** mock JWKS validation for rep-volume tests + spot-check with real auth at 1/10 the rate.
   - **Mock path:** `tests/NickERP.Perf.Tests/Auth/MockJwtBearerHandler.cs` produces signed-but-CF-Access-shaped JWTs against a per-run RSA-2048 key pair. The matching API-side JWKS-mock is a Phase V follow-up; the seam is wired via `MockJwksEndpoint`.
   - **Real path (spot-check):** when `NICKERP_PERF_BEARER_TOKEN` env var is set, scenarios use that token verbatim against the real CF Access JWKS path. Operator obtains the token via an out-of-band CF Access login.
   - **Decision rationale:** real JWKS validation in NBomber against pilot RPS bombards CF Access's edge, which (a) breaks the SLA we trust them to maintain and (b) doesn't measure pilot reality (CF Access caches public keys; the second token validates against the cache). Mock-rep-volume + real-spot-check captures both shapes without the cost.
   - **Production path is unchanged:** the API host always validates real CF Access JWTs in production; the mock path only exists for the perf rig.
- **Image-volume realism.** Pilot scanners produce ~50-200MB per case; do we run perf with real scan artifacts (slower, more realistic) or synthetic placeholders (faster, less realistic)? Recommend hybrid: synthetic for hot-path RPS measurement; real for image-gallery latency.
- **Tenant data shape.** Need realistic row counts in `audit.events`, `inspection.cases`, etc. before perf testing. **Decided 2026-05-05 (Sprint 52 / FU-perf-tenant-data-shape):** `tools/perf-seed/` console seeds N tenants × M cases each with the brief's distribution (10% open / 70% closed / 10% verdict-rendered / 10% submitted). All seeded rows carry `IsSynthetic = true` so the pilot probe `gate.analyst.decisioned_real_case` ignores them.
