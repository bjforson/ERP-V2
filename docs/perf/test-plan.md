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

## 6. Test tooling decision — NickPerf in-tree runner

**Sprint 58 update (2026-05-06):** the harness was originally built on NBomber 6.1.0; Sprint 57's license audit revealed NBomber's bundled LICENSE is the **NBOMBER LICENSE AGREEMENT v2.0** (commercial subscription, paid Activation Key required), not MIT. SEC-DEP-3 was the only P0 license finding in `docs/security/audit-checklist-2026.md`. Rather than purchase a subscription or vendor-shop a permissive alternative we hand-rolled a small in-tree runner — the surface NBomber gave us was small enough that owning it was the cleaner answer.

**The replacement primitives live at `tests/NickERP.Perf.Tests/Runner/`:**

| File | Replaces | Surface |
|---|---|---|
| `Runner/NickPerfScenario.cs` | NBomber `ScenarioProps` | `Name` + `RunStep` async delegate + `LoadProfile` (rate / interval / duration) + `MaxConcurrent` cap |
| `Runner/NickPerfRunner.cs` | `NBomberRunner.RegisterScenarios(...).Run()` | `PeriodicTimer` rate scheduler + `SemaphoreSlim` concurrency cap; per-step latency captured via `Stopwatch` |
| `Runner/NickPerfStats.cs` | `NodeStats` / `ScenarioStats` / `LatencyCount` / HdrHistogram | Latency buffer + nearest-rank p50/p75/p95/p99 + ok/fail counts + RPS |
| `Runner/NickPerfReport.cs` | NBomber HTML / MD / TXT bundle | Single per-scenario `report.md` matching the NBomber-shape table |
| `Runner/Http/NickPerfHttp.cs` | NBomber.Http `Http.CreateRequest` / `Http.Send` | Direct `HttpClient` helpers (`GetAsync` / `PostJsonAsync` / `SendAsync`) |

**Why in-tree, not third-party:**

| Property | NickPerf (in-tree) | NBomber 6.1.0 | k6 | JMeter |
|---|---|---|---|---|
| License | n/a — first-party code | **PROPRIETARY (commercial subscription)** | AGPL-3.0 (post-2024) | Apache-2.0 |
| .NET-native | ✓ same .NET stack | ✓ same .NET stack | ✗ JS | ✗ Java |
| CI integration | ✓ exit code from `dotnet run` | ✓ NuGet-based | partial | partial |
| Reports | markdown only (audit trail) | HTML + MD + TXT | HTML + JSON | HTML + JSON |
| Maturity | small (Sprint 58) | mature | mature | very mature |
| Mixed-protocol support | HTTP only (extensible) | HTTP + custom | HTTP-focused | HTTP / TCP / JDBC / etc. |
| Lines of code we own | ~400 | 0 (vendored) | 0 | 0 |
| Surface we use | ~100% | ~5% | ~5% | ~5% |

**Trade-offs accepted:**
- We give up NBomber's HTML report. The audit trail uses markdown anyway.
- We give up NBomber's custom data feeds + cluster mode + WebSocket scenarios. We don't use those.
- We give up some maturity. The runner is small enough (`PeriodicTimer` + `SemaphoreSlim` + `List<double>` + `Array.Sort`) that the maturity gap is manageable; the selftest in `dotnet run -- selftest` covers the percentile math + scheduler + report formatting.

**Behaviour parity with NBomber (per Sprint 58 verification):**
- Same per-profile RPS targets per §3.1.
- Same skip-on-misconfigured semantics (empty target, missing HMAC key, missing JWT subject).
- Same per-scenario acceptance-gate thresholds.
- `MockJwtBearerHandler`, `ContainerNumberGenerator`, payload builders unchanged.

**Out-of-scope for Phase V (revisit post-pilot):**
- JMeter for full protocol coverage (DB-direct stress, JMS, etc.)
- Chaos engineering (gremlin, chaos-mesh)
- Multi-region perf testing
- Live HTML/streaming reports (markdown is enough for the audit trail)

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

- **Auth latency in tests.** CF Access JWT-validate path adds ~10-50ms per request; do we mock this in load tests, or hit real CF? **RESOLVED — Sprint 52 + Sprint 55:** mock JWKS validation for rep-volume tests + spot-check with real auth at 1/10 the rate. Wiring is shipped + live.
   - **Mock path:** `tests/NickERP.Perf.Tests/Auth/MockJwtBearerHandler.cs` produces signed-but-CF-Access-shaped JWTs against a per-run RSA-2048 key pair. Sprint 55 added `tests/NickERP.Perf.Tests/Auth/MockJwtBearerHandlerSingleton.cs` so both `CaseCreateScenario` and `EdgeReplayScenario` share a single `kid` per process; the matching API-side JWKS-mock is a Phase V kickoff prerequisite (see baseline-2026-05-06.md).
   - **Real path (spot-check):** when `NICKERP_PERF_BEARER_TOKEN` env var is set, scenarios use that token verbatim against the real CF Access JWKS path. Operator obtains the token via an out-of-band CF Access login. The `CaseCreateScenario.ResolveBearerToken` resolver picks env var first, falls back to mock signer.
   - **Decision rationale:** real JWKS validation in NBomber against pilot RPS bombards CF Access's edge, which (a) breaks the SLA we trust them to maintain and (b) doesn't measure pilot reality (CF Access caches public keys; the second token validates against the cache). Mock-rep-volume + real-spot-check captures both shapes without the cost.
   - **Production path is unchanged:** the API host always validates real CF Access JWTs in production; the mock path only exists for the perf rig.
   - **See also:** §12 below for the day-1 operator playbook.
- **Image-volume realism.** Pilot scanners produce ~50-200MB per case; do we run perf with real scan artifacts (slower, more realistic) or synthetic placeholders (faster, less realistic)? Recommend hybrid: synthetic for hot-path RPS measurement; real for image-gallery latency.
- **Tenant data shape.** Need realistic row counts in `audit.events`, `inspection.cases`, etc. before perf testing. **Decided 2026-05-05 (Sprint 52 / FU-perf-tenant-data-shape):** `tools/perf-seed/` console seeds N tenants × M cases each with the brief's distribution (10% open / 70% closed / 10% verdict-rendered / 10% submitted). All seeded rows carry `IsSynthetic = true` so the pilot probe `gate.analyst.decisioned_real_case` ignores them.

---

## 12. Operator playbook

Sprint 55 made the harness runnable end-to-end. Sprint 58 replaced the NBomber runtime with the in-tree NickPerf runner (per §6); the entry-point command + flags + skip-on-misconfigured behaviour stay identical so existing playbooks just keep working.

### 12.1 Prerequisites

- .NET 10 SDK installed.
- Built artifact: `dotnet build tests/NickERP.Perf.Tests/NickERP.Perf.Tests.csproj -c Release`.
- Target host running and reachable. Default targets:
  - Portal at `http://localhost:5400` (Razor analyst UI + `/api/inspection/cases` once it lands).
  - Inspection.Web at `http://localhost:5410` (`/api/edge/replay`).

### 12.2 Run scenarios

Smoke (always works against any portal):

```powershell
dotnet run --project tests/NickERP.Perf.Tests -c Release -- health
dotnet run --project tests/NickERP.Perf.Tests -c Release -- health --profile 5x
```

Case-create:

```powershell
# Real CF Access spot-check path (preferred for ad-hoc auth-correctness checks):
$env:NICKERP_PERF_BEARER_TOKEN = "<real-CF-Access-token-from-out-of-band-login>"
dotnet run --project tests/NickERP.Perf.Tests -c Release -- case-create

# Mock-JWT rep-volume path (preferred for hot-path RPS):
Remove-Item Env:\NICKERP_PERF_BEARER_TOKEN -ErrorAction SilentlyContinue
dotnet run --project tests/NickERP.Perf.Tests -c Release -- case-create --profile 5x
```

Edge-replay (steady) + backlog reconnect:

```powershell
$env:NICKERP_PERF_EDGE_HMAC_KEY = "<per-edge-key-issued-by-admin-flow>"
dotnet run --project tests/NickERP.Perf.Tests -c Release -- edge-replay
dotnet run --project tests/NickERP.Perf.Tests -c Release -- edge-replay-backlog
```

### 12.3 Run against staging / pilot

Override the target via env var (the `NICKERP_PERF_` prefix is stripped by `IConfiguration` and double-underscore separates nested keys):

```powershell
$env:NICKERP_PERF_TargetBaseUrl = "https://staging.example.com"
$env:NICKERP_PERF_Endpoints__InspectionWebBaseUrl = "https://api.staging.example.com"
dotnet run --project tests/NickERP.Perf.Tests -c Release -- case-create --profile 1x
```

Per-scenario overrides (granular):

```powershell
$env:NICKERP_PERF_CaseCreate__TargetBaseUrl = "https://api.staging.example.com"
$env:NICKERP_PERF_EdgeReplay__TargetBaseUrl = "https://api.staging.example.com"
$env:NICKERP_PERF_EdgeReplay__EdgeNodeId = "edge-takoradi-01"
$env:NICKERP_PERF_EdgeReplay__TenantId = "42"
```

### 12.4 Skip-on-misconfigured behaviour

Each live scenario inspects required config + auth and gracefully exits 0 if missing. CI green-on-noop is the design — no broken pipelines from environments that don't have the perf rig wired:

| Scenario | Skip trigger | Exit code | Log line |
| --- | --- | --- | --- |
| `case-create` | no target URL OR no bearer source | 0 | `case-create: skipping — <reason>` |
| `edge-replay` | no target URL OR `NICKERP_PERF_EDGE_HMAC_KEY` unset | 0 | `edge-replay: skipping — <reason>` |
| `edge-replay-backlog` | same as `edge-replay` | 0 | `edge-replay-backlog: skipping — <reason>` |

### 12.5 Acceptance gates

Each scenario's `CheckAcceptanceGate` method asserts p99 latency against the §3.1 thresholds. If a scenario completes with `0 OK` requests, the dispatcher fails (the gate cannot evaluate). The exit code maps:

| Exit code | Meaning |
| --- | --- |
| 0 | Scenario passed (or skipped on missing config). |
| 1 | Acceptance gate breached, OR all requests failed, OR unknown scenario name. |
| 2 | FATAL — uncaught exception. |

CI hooks `dotnet run … --` and uses the exit code as the deploy gate. The HTML/markdown reports under `tests/NickERP.Perf.Tests/bin/<config>/<tfm>/reports/<date>/<scenario>/` capture the detail.

### 12.6 Reading reports

NickPerf writes a single markdown report per scenario:

- `report.md` — table-per-scenario (Total / OK / Fail / RPS / latency p50/p75/p95/p99 / min / mean / max).

(Sprint 55's NBomber bundle had `report.html` + `report.txt` in addition; those were never consumed downstream. Markdown only is the post-Sprint-58 shape.)

The Phase V auditor copies the relevant runs into `docs/perf/runs/{date}-{site}/` for the audit trail (post-pilot pattern).

### 12.7 First-run baseline

See `docs/perf/baseline-2026-05-06.md` for the Sprint 55 first-baseline against the dev portal — it captures what's deferred to Phase V kickoff (mostly endpoint exposure + host orchestration), and the expected first-real-baseline shape once those prerequisites land.
