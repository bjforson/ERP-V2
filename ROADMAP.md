# NickERP v2 — Roadmap

> Independent v2 roadmap. v1 is **read-only** during this build. If we need
> something from v1 we copy it as a point-in-time port — we do not edit
> the v1 tree.
>
> This file replaces any v1-side roadmap reference. Edit it freely.
> For a source-backed snapshot of what is implemented vs. target design,
> see [`docs/architectural-design-analysis-2026-05-13.md`](docs/architectural-design-analysis-2026-05-13.md).

---

## 1. The vision (verbatim)

From the original direction:

> The NSCIM system needs an architectural modification. The system aggregates images from scanners in several locations and feeds in images + data from external data sources (ICUMS in this instance). It compares image + document for image analysts to decide, then resubmits data to the external data sources.
>
> When the system rolls out nationwide:
>
> 1. **Federation by location.** Group / tie activities of scanners in the same location. Tema can have more than one scanner; maybe even a dedicated external data source per location. Assign users to locations instead of one flat system. *"imagine we have 20 scanners nationwide across 5 locations we will need a way to federate the system to handle such a scenario."*
> 2. **Per-location setup.** Set the system up per location, then tie scanners to locations. External data sources can be tied to a location if the external API provides individual APIs per location.
> 3. **Generic nomenclature.** Don't use "ICUMS" for our external data source — have a module called "scanners" so every new scanner is set up from the scanner module; same for external data sources, then named appropriately.
> 4. **Greenfield rebuild.** Create a new folder, pull what source material we need from the current system, build out from there. *"not touching the current system we have."*
> 5. **Situated in the ERP context.**

**Amendments to the original direction (after-the-fact, dated):**

6. **Analysis service shape.** *(Added 2026-05-02 in plan-mode walk.)* The image-analysis function is organised into one or more `AnalysisService`s per tenant. Each service has a scope of one or more locations (location-scoped or federation-scoped — same entity shape, different cardinality of owned locations). **A location can belong to multiple AnalysisServices** (N:N). Users join services; permissions flow from membership.
   - **Built-in default:** every tenant has an immutable, un-deletable "All Locations" AnalysisService. Every location auto-joins it at creation. Admins can grant/revoke analyst access to it but cannot delete the service itself. Unrouted cases are impossible by construction.
   - **Tenant-configurable choices:** case visibility model (shared — case appears in all qualifying services; or exclusive — case routes to one service at intake) and user multi-service membership (allowed / one-only).
   - **Locked semantics:** under shared visibility, **first-claim-wins** — first analyst to open the case locks it; other services display "claimed by [user] in [service]" and cannot work it.

Open-question answers (locked):

- **External system bindings**: per-location, **subset-of-locations**, OR shared across all locations within the tenant — chosen at onboarding per instance via a junction table. *(Extended 2026-05-02: subset-of-locations added; was binary "single or all".)*
- **Online-first**, with an edge-for-backup design (offline + backfill on reconnect) baked into events. **v0 mandatory event set** *(locked 2026-05-02)*: `scan-captured`, `scanner-status-changed`, audit events. Audit events shipped Sprint 11; the other two are v2 must-have. Edge cardinality unconstrained per tenant — deploy as ops requires. Full degraded-mode (offline analyst decisions) remains design-supported, build-later.
- **Central Postgres** cluster. **Operational shape locked 2026-05-02:** primary + streaming standby with documented manual failover (Patroni deferred); pgbackrest backups (full + incremental + PITR); all reads from primary (standby is HA-only); single region (cross-region DR later); EF Core / Npgsql pooling only (no pgBouncer — preserves the `TenantConnectionInterceptor` session-state pattern); locked to PostgreSQL 17.
- **Multi-tenant from day 1.** **Operational shape locked 2026-05-02:** platform-admin-only provisioning (manual onboarding, no self-service); soft-delete with retention window (~90 days) then explicit hard-purge admin action; platform-admin-generated scoped exports on tenant request (audit-trailed); first-user invite via one-time email link (requires an email-sending capability — does not exist in v2 today; tracked as a gap).
- **Timeline**: 6-9 month internal target. *(Re-locked 2026-05-02; was "months, phase-gated, no hard deadline" then briefly "hard 3-6 months" mid-walk.)* Hybrid execution — phases F/D/V/G/P are conceptual buckets, sprints are the execution unit (rolling-master pattern). Cutover model: pilot location → parity-driven expansion (not big-bang). Lightweight Phase V (targeted security audit + perf test on pilot scope) before pilot.
- **Plugins**: always in-house. Customer one-offs = paid v2-team engagement. Filesystem trust today (cryptographic signing deferred until audit / customer demands). Authoring docs deferred (tribal knowledge for current team size).
- **More NickERP domains coming** (Finance, etc.). **Pilot strategy locked 2026-05-02:** three modules co-deployed under one v2 portal — inspection v2-native + NickFinance (v1-clone coexisting with the Sprint 10 G2 pathfinder) + NickHR (cloned now). **Post-pilot refactor arc** folds v1-clones into v2-native architecture (~6-10 sprints per module — NickFinance fold-into-G2 first, then NickHR). Per-tenant module activation (configurable by platform admin). Cross-module dependencies through platform layer only — no direct module↔module imports.

---

## 2. Architectural decisions (locked)

| | Decision | Why |
|---|---|---|
| **Hierarchy** | Tenant → (optional Region) → Location → Station → Device. `LocationId` and `TenantId` are orthogonal — both filter every query via Postgres RLS. | Federation needs Location as a first-class citizen, not a column buried in a flat schema. |
| **Domain language** | Core speaks `InspectionCase`, `ScannerDeviceInstance`, `ExternalSystemInstance`, `Finding`, `Verdict`. **Vendor and country names never appear in core.** ICUMS / FS6000 / regime codes / BOE / CMR live inside plugin adapters and per-country authority modules. | The "generic nomenclature" requirement, structurally enforced. |
| **Plugins** | `[Plugin("type-code")]` + `plugin.json` manifest. Three contracts: `IScannerAdapter`, `IExternalSystemAdapter`, `IAuthorityRulesProvider`. Drop a DLL in the host's plugins folder, restart, registered. | "Scanners module / external data sources module" — every new scanner or data source is one DLL + one config UI form. |
| **External system binding** | `ExternalSystemInstance.Scope ∈ { PerLocation, SubsetOfLocations, Shared }` + many-to-many join `ExternalSystemBinding` to locations. Per-scope cardinality enforced in the Application layer (PerLocation = 1 binding, SubsetOfLocations ≥ 2, Shared = 0). Picked at onboarding. | All three modes work; the choice is per-instance. *(SubsetOfLocations added Sprint 16, 2026-05-04.)* |
| **Repo** | Greenfield monorepo at `C:\Shared\ERP V2\` + `github.com/bjforson/ERP-V2` (private). v1 stays untouched. | The "new folder, don't touch v1" rule. |
| **Identity** | One canonical `IdentityUser` keyed on lowercased email. Cloudflare Access JWT validation against CF JWKS. Users assigned per-location with per-app role lists (no flat global permission). | "Assign users to locations instead of one flat system." |
| **Tenancy** | Multi-tenant from line 1. `TenantId` (long) on every entity via `ITenantOwned`. Stamping by EF SaveChanges interceptor. Postgres RLS as defense-in-depth via `app.tenant_id` session var. | Decision locked + structurally enforced now so no entity can opt out later. |
| **Image pipeline** | Pre-rendering baked into Inspection v2 from line 1 (thumbnails 256 px, previews 1024 px, Redis + disk tiers, ETag/`Cache-Control` streaming). **No base64 image marshalling, ever.** | At expected scale (~2000 images/day per location) base64-per-request fails — repeating v1's mistake is non-negotiable. Spec already in `docs/ARCHITECTURE.md` §7.7. |
| **Connectivity** | Online-first. Central API is the primary path. Every state change is a `DomainEvent` with idempotency key — that contract enables a future edge node to replay its log on reconnect. | Online clean today; offline-capable later without re-architecture. |
| **Web stack** | Blazor Server for the primary admin + analyst web. Shared chrome (TopNav / UserMenu / NotificationBell / AppSwitcher) lives in `NickERP.Platform.Web.Shared`. | Team familiarity + clean SignalR path. Edge offline UI later via separate thin client. |
| **Audit + events** | One append-only `audit.events` table. Every state change emits a `DomainEvent` via `IEventPublisher`. In-process `IEventBus` today; cross-process LISTEN/NOTIFY later. | Compliance audit trail + cross-app integration + idempotency, all from one record. |

---

## 3. Status — what's done vs. left, mapped to the vision *(saturation milestone — refreshed 2026-05-06)*

> **Pre-pilot saturation: 41/27-41 sprint-equivalents shipped (100-152% — past upper estimate).**
>
> All seven workstreams from §4.1 are either complete or
> operator-blocked. Inspection v1 parity Batches B1-B8 closed.
> Multi-tenant lifecycle (Pt 1+2+3) closed. Three-module co-deploy
> navigation closed. HA + pgbackrest + PG17 runbooks closed (operator
> stand-up pending). Phase V lightweight prep closed; Phase V
> execution waits on pilot site lock. Pilot acceptance correctness
> probe ACTIVE with 5 system-correctness gates including the
> `MultiTenantInvariantProbe`. Production scaling foundation in place
> (audit.events partitioned with 18 monthly partitions; perf seed
> tool + JWT mock + license audit + secret-scan tooling). 1099/1099
> tests passing across 11 projects.
>
> Remaining work is operator-side: apply staged migrations, stand up
> standby + pgbackrest, lock the pilot site, run Phase V execution.
> See §4.4. Post-pilot scope (image-analysis ML arc + v1-clone
> fold-into-v2-native) is intentionally deferred.

### 3.1 Platform — Track A

The shared layer every v2 module sits on. **Track A complete.**

| Layer | Status | What it gives the module |
|---|---|---|
| **A.1 Observability** (Logging + Telemetry) | ✅ shipped | One-line wireup → logs flow to Seq, traces + metrics flow over OTLP. Correlation id stamped on everything so a request → DB span → log line all link. |
| **A.2 Identity** | ✅ shipped | Canonical user store, CF Access JWT validation + dev bypass, app-scope assignments, service-token identities, admin REST API + admin UI. |
| **A.3 Tenancy** | ✅ shipped | `Tenant` entity, `ITenantOwned` marker, EF interceptors that stamp `TenantId` on insert and push `app.tenant_id` to Postgres for RLS, JWT-claim middleware. |
| **A.4 Plugins** | ✅ shipped | `[Plugin]` attribute + manifest + assembly-scanning loader + `IPluginRegistry`. Mock-echo plugin proves the round-trip. |
| **A.5 Audit & Events** | ✅ shipped (in-process bus) | `DomainEvent` record + idempotency-key helper + `audit.events` table + `DbEventPublisher` + in-process `IEventBus`. Cross-process LISTEN/NOTIFY deferred until needed. |
| **A.6 Web.Shared** | ✅ shipped | `tokens.css` design system + `TopNav` + `UserMenu` + `NotificationBell` + `AppSwitcher`. v2-only chrome by default — no v1 hostnames. |

Backend services running:

- **Seq** on `localhost:5341` (centralized logs + OTLP receiver)
- **Postgres**: `nickerp_platform` (schemas: `identity`, `tenancy`, `audit`) + `nickerp_inspection` (schema: `inspection`)
- All migrations applied. Bootstrap dev user `dev@nickscan.com` with `Identity.Admin` scope. Tenant 1 = `nick-tc-scan`.

### 3.2 Apps and modules — Track B *(refreshed 2026-05-06 — pilot-ready)*

| App / module | Status | Live URL / location | What's there |
|---|---|---|---|
| **Portal v2** (B.2) | ✅ pilot-ready | http://localhost:5400 | Launcher at `/` (Sprint 49); `/dashboard` + `/admin/pilot-readiness` (Sprint 43); `/admin/feature-flags` + `/admin/tenant-settings` (Sprint 35); `/admin/workers` (Sprint 50); `/admin/sla` (Sprint 31); `/admin/cross-record-scans` (Sprint 31); `/admin/exports` (Sprint 25); `/admin/posthoc-outcomes` (Sprint 13); EdgeKeys; Tenants; Audit log; Sprint dashboard. Three-module nav shipped Sprint 29. |
| **Inspection v2** (B.1.0) | ✅ v1-parity closed | http://localhost:5410 | All B1-B8 batches shipped: B1 case viewing (Sprint 20), B2 ICUMS UIs (Sprint 22), B3 BG services (Sprint 24), B4 validation rules (Sprint 28), B5 completeness + SLA + cross-record (Sprint 31), B6 specialised reviews (Sprint 34: BL/AI-triage/Audit/MyQueue), B7 monitoring + reports (Sprint 33), B8 admin + housekeeping (Sprint 35). 11 review pages, 17+ admin pages, full case viewer, manifest validation, retention classes, legal hold, queue tier escalation. |
| **NickFinance — Petty Cash** (B.3.1) | ✅ pathfinder + v1-clone co-deployed | `modules/nickfinance/` + `v1-clone/finance/` | G2 pathfinder shipped Sprint 10; v1-clone covers full v1 functionality; runbook 12 carries the deploy + module overview. v1-clone fold-into-G2 is **post-pilot**. |
| **NickHR** | ✅ v1-clone shipped Sprint 15 | `v1-clone/nickhr/` | Cloned for pilot co-deploy; v2-native refactor is **post-pilot** (~6-10 sprints, FU-nickhr-shared-chrome flagged). |
| **Comms v2** | n/a — adapted not rebuilt | — | Settings docs in runbook 13; comms.email.* keys catalogued. |
| **Edge node** | ✅ all v0 events shipped | `apps/edge-node` | SQLite buffer + `/api/edge/replay` + per-edge HMAC API keys + rotation (Sprint 13); manifest validation (Sprint 45 P2); cursor state persistence (Sprint 50); per-tenant inbox routing (Sprint 50); multi-event fan-out shipped Sprint 17 (audit + scan-captured + scanner-status-changed). |
| **Threshold calibration** (§6.5) | ✅ shipped Sprint 12 | `/admin/thresholds` | Idle until first scanner. (Note: `ScannerThresholdResolver` has a register-finding from Sprint 57 — see `docs/system-context-audit-register.md` "Pending opt-in" section; functional impact is dev-only since no operator-tuned profiles staged yet.) |
| **Post-hoc outcome adapter** (§6.11) | ✅ shipped Sprint 13 T3 | `/admin/posthoc-outcomes` + `OutcomePullWorker` | Manual entry stub live; real `IInboundOutcomeAdapter` plugin against ICUMS is **post-pilot**. |
| **Notifications inbox + bell** | ✅ shipped Sprint 35 | `/notifications` | Replaces Sprint 8 P3 page; bell + page share unread-count path (Sprint 35). |
| **ASE adapter** (Tema insurance plugin) | ✅ scaffold shipped Sprint 50 | `modules/inspection/plugins/.../Scanners.Ase/` | Stub-shaped contract conformance; vendor-protocol wireup when on-site. |
| **Outbound webhooks** | ✅ shipped Sprint 33 | `WebhookDispatchWorker` + `IOutboundWebhookAdapter` | Default-disabled per Sprint 24 architectural decision; per-adapter exception isolation; per-tenant cursor. |

### 3.3 Vision-element coverage *(refreshed 2026-05-06 — saturation)*

Each of the 6 vision points + 7 locked answers, mapped to current state:

#### 6 vision points (the original direction + the 2026-05-02 amendment)

| # | Vision element | Status | Where it lives |
|---|---|---|---|
| 1 | **Federation by location** | ✅ Done | `inspection.Location` + `Station` entities; `ScannerDeviceInstance.LocationId` constraint; admin pages `/locations` + `/stations`; `InspectionCase.LocationId` binds cases. |
| 2 | **Per-location setup flow** | ✅ Done | Admin pages `/locations` → `/stations` → `/scanners` → `/external-systems` end-to-end. Scanner onboarding wizard shipped Sprint 38. |
| 3 | **Generic nomenclature** | ✅ Done | `ScannerDeviceInstance.TypeCode` + `ExternalSystemInstance.TypeCode`; plugin contracts in `*.Abstractions` projects; core has zero vendor names; verified by tests asserting no ICUMS/FS6000/regime-code strings in `modules/inspection/src/Core/*`. |
| 4 | **Greenfield rebuild** | ✅ Done | Separate repos (`bjforson/ERP-V2` private), separate Postgres DBs, NickFinance v1-clone in `v1-clone/finance/` as point-in-time port pattern; NickHR cloned Sprint 15. v1 untouched. |
| 5 | **ERP context** | ✅ Done | Platform layers shared (Audit, Identity, Logging, Plugins, Telemetry, Tenancy, Web.Shared); NickFinance G2 pathfinder + NickHR clone + inspection v2 co-deploy under three-module nav (Sprint 29). Module activation per-tenant via `tenant_module_settings` (Sprint 29). |
| 6 | **AnalysisService (VP6)** *(2026-05-02)* | ✅ Done | Sprint 14 across 5 phases: storage + bootstrap + admin + claim + tests. N:N location↔service via `analysis_service_locations` join; immutable "All Locations" default; first-claim-wins under shared visibility; configurable case-visibility model + user multi-membership. `CaseVisibilityService` + `CaseClaimService` are the canonical query helpers. |
| 7 | **Wrecking-ball platform (Sprint 14 / B-queues)** *(2026-05-07)* | ⚠️ S+2 of 6 shipped; S+3 scaffold landed 2026-05-09 | Platform substrate live: `NickERP.Platform.Queueing/` (`PostgresQueue`, `OutboxRelay`, `PgNotifyListener`, `WorkItemStateMachine`), `WorkItem<TState>` + `WorkItemTransition` entities, integration tests, `InspectionStateMachine`, `SplitDetectionConsumer`, migration `20260507130546_Add_QueueingPlatform.cs` (S+2). S+3 added 5 consumer stubs (`ImageAnalysisConsumer`, `DecisionAgentConsumer`, `AuditAssignmentConsumer`, `AuditReviewConsumer`, `SubmissionConsumer`) + 5 queue tables (migration `20260509153236_Add_S3_QueueTables.cs`) — compile-clean placeholder bodies logging `TODO[Sprint S+3]`; real dispatch logic deferred. Remaining: real consumer bodies + producer enqueues from state-machine transitions (S+4), observability + DLQ UI (S+5), parallel-run cutover prep (S+6). |

#### 7 locked answers (from §1)

| Locked answer | Status | Sprint reference |
|---|---|---|
| **External system bindings: PerLocation / SubsetOfLocations / Shared** | ✅ Done | Sprint 16 — `ExternalSystemBindingScope` enum; per-scope cardinality enforcement in `ExternalSystemAdminService.RegisterAsync`; `ResolveServingInstancesAsync` canonical lookup. |
| **Online-first edge-for-backup; v0 mandatory event set (scan-captured + scanner-status-changed + audit)** | ✅ Done | Sprint 17 — all three event types live; `EdgeReplayEndpoint.HandleAsync` dispatches per `EventTypeHint` to payload-shape resolvers; edge-side typed helpers; manifest validation Sprint 45 P2; multi-tenant fan-out Sprint 50. |
| **Central Postgres operational shape (primary + standby manual failover, pgbackrest, PG17, no pgBouncer, single region)** | 🟡 Runbooks complete; operator stand-up pending | Sprint 27 — runbooks 09 (HA + manual failover) + 10 (pgbackrest full/incremental/PITR + quarterly drill) + 11 (PG17 lock + pg_upgrade); Sprint 52 added Windows-host postures (SSH-Linux / WSL2 / native v1). Operator actions in §4.4. |
| **Multi-tenant day 1: platform-admin provisioning, soft-delete + hard-purge, scoped exports, first-user invite via email** | ✅ Done | Sprint 18 (Pt 1: state + soft-delete + hard-purge admin) + Sprint 21 (Pt 2: `IEmailSender` + `InviteToken` + first-user invite + `AcceptInvite` page) + Sprint 25 (Pt 3: `TenantExportService` + `TenantExportRunner` + `Add_TenantExportRequests` migration + admin Exports card + `/api/tenant-exports/{id}/download`). 5-entry system-context audit register; 180+ RLS policies; `MultiTenantInvariantProbe` (Sprint 43) catches register drift at runtime. |
| **Timeline: 6-9 month internal target, hybrid execution (rolling-master sprints), pilot location → parity-driven expansion, lightweight Phase V before pilot** | ✅ Pre-pilot done | 41 sprint-equivalents shipped against 27-41 estimate (100-152% — past upper estimate). Phase V prep + execution-readiness shipped Sprint 30 + Sprint 39 + Sprint 52. Phase V execution awaits pilot site lock. |
| **Plugins always in-house (filesystem trust, no signing, no public API)** | ✅ Stable | Contract version 1.2 since Sprint 12 Phase B; cryptographic signing deferred per architectural decision; authoring docs deferred (small team). |
| **Three-module pilot strategy** *(2026-05-02)* | ✅ Done | Sprint 15 (NickHR clone) + Sprint 29 (three-module nav: `IModuleRegistry` + `ModuleRegistryService` + `/launcher` + `SharedHeader`/`Footer` + per-tenant `tenant_module_settings`). Inspection.Web + NickFinance.Web adopt shared chrome; NickHR shared-chrome deferred to v1-clone-to-v2-native refactor (FU-nickhr-shared-chrome). |

#### Remaining gaps (all post-pilot)

- **§6.x image-analysis arc**: §6.1 OCR moved to post-pilot 2026-05-04 after operational probe revealed v1 OCR is a verification check, not the linkage source — broken for years without affecting operations. §6.2 / 6.3 / 6.4 / 6.6 / 6.8 / 6.9 / 6.10 are post-pilot per locked plan §12. §6.7 dual-view stays deferred (no dual-view scanner in fleet). 2 of 11 shipped (§6.5 thresholds + §6.11 post-hoc); 8 post-pilot; 1 deferred.
- **v1-clone fold-into-v2-native**: NickFinance v1-clone fold-into-G2 + NickHR v2-native refactor = ~12-22 post-pilot sprints. The pilot ships with all three modules (inspection v2-native + NickFinance G2 + v1-clone + NickHR v1-clone) co-deployed.
- **User-to-location assignments**: `LocationAssignments.razor` exists but JWT principal does not yet carry location ids. Operator can use `Inspection.Admin` role for now; pilot doesn't strictly require per-location enforcement until multi-location rollout.

---

## 4. What's next — pilot deployment + Phase V execution *(refreshed 2026-05-06 — saturation milestone)*

**Pre-pilot saturated at 41/27-41 sprints (100-152% — past upper estimate).** Code-side rebuild is **complete**. Remaining work is operator-side deploy + the Phase V execution (security audit pass + perf test) before the pilot ships. Original plan-file §10 dispatch sequence is no longer the active sequence — it described pre-pilot work that has shipped. The durable plan file now serves as historical record + reference for the post-pilot ML arc; `docs/runbooks/14-pilot-cutover.md` (Sprint 54) is the operator-facing deliverable for cutover day.

### 4.1 What's complete

All seven pre-pilot workstreams (α through ζ from the original plan §4.1) closed:

| Workstream | Final state | Reference |
|---|---|---|
| **α** Foundational | ✅ Done | AnalysisService VP6 (Sprint 14, 5 phases); `ExternalSystemInstance` subset junction (Sprint 16) |
| **β** Tenant + ops | ✅ Done (operator stand-up pending) | Tenant lifecycle Pt 1+2+3 (Sprints 18 + 21 + 25); HA + pgbackrest + PG17 runbooks (Sprint 27); `P2-FU-multi-event-types` (Sprint 17) |
| **γ** Modules | ✅ Done | NickHR v1-clone (Sprint 15); three-module co-deploy navigation (Sprint 29) |
| **δ** ML | 🟡 §6.1 deferred to post-pilot 2026-05-04 | Sprint 19 phases 1+2 (eval harness + baseline) shipped as diagnostic tooling; phases 3-7 post-pilot per locked decision in `_resolvedThisSession[ocr-baseline-0pct-followups]` |
| **ε** v1 parity (long pole) | ✅ Done — **all 8 batches shipped** | B1 (Sprint 20) → B2 (Sprint 22) → B3 (Sprint 24) → B4 (Sprint 28) → B5 (Sprint 31) → B6 (Sprint 34) → B7 (Sprint 33) → B8 (Sprint 35) |
| **ζ** Pilot prep | ✅ Lightweight prep + execution-readiness done; full execution awaits pilot site | Phase V prep (Sprint 30); Phase V execution-readiness (Sprint 39 — secret detector + audit-correlation-id-stamper); production scaling foundation (Sprint 52 — audit.events partitioning + license audit + trufflehog) |

Plus the unscheduled-but-shipped extras since saturation (Sprints 36-50+):

- Outbound webhook contract + dispatcher (Sprint 33)
- Cross-record scan detection + splitting + SLA window tracking + completeness rollup engine (Sprint 31, v1 2.15.4 parity)
- Validation strict-mode + rule snapshots + CustomsGh completeness rules (Sprint 48)
- UI + nav polish (Sprint 49 — launcher at `/`, SLA sparkline, reviews CSS, feature-flag key validation)
- ASE adapter scaffold + edge worker hardening (Sprint 50)
- Export tooling matured: LISTEN/NOTIFY pickup + multi-host SKIP LOCKED + `ITenantExportStorage` abstraction with S3 path (Sprint 51)
- Production scaling foundation: audit.events partitioned with 18 monthly partitions; perf-seed tool; mock JWT bearer handler; license audit + trufflehog (Sprint 52)
- Pilot acceptance correctness probe with 5 system-correctness gates including `MultiTenantInvariantProbe` (Sprint 43)
- Retention classes + legal hold (Sprint 44)
- Canonical scan package + `EdgeReplayEndpoint` manifest validation (Sprint 45)
- Queue SLA tiers + auto-escalator (Sprint 47)
- Webhook contract + dispatcher (Sprint 33; Sprint 47 added queue-tier escalation)
- Scanner onboarding wizard + threshold-change audit (Sprint 38)
- Notifications inbox + bell unification (Sprint 35)
- Feature flags + tenant settings admin pages (Sprint 35)

### 4.2 Pilot-time deploy work (operator)

The operator drives this; Claude code-changes are zero. Tracked in `docs/sprint-progress.json` `prePilotProgress.operatorActions[]`.

| Step | Reference | Blocking |
|---|---|---|
| Apply 32 staged migrations to live (`nickerp_platform` + `nickerp_nickfinance`) | runbook `07-sprint-13-live-deploy.md` | any prod cutover |
| Provision second physical box / VM for streaming-standby Postgres | runbook 09 §3 | production HA cutover |
| Install pgbackrest 2.50+ on primary + choose repo location | runbook 10 §5 | production backup posture |
| Wire cron / scheduled-task for pgbackrest cadence + first quarterly restore drill | runbook 10 §6 + §8 | production backup posture |
| Wire HA + backup alerts into Seq / alerting layer | runbooks 09 §10 + 10 §10 | production HA monitoring |
| Pick pilot site | plan file §13 decision matrix; tentative front-runners Kotoka Cargo (KIA) or Takoradi | Phase V scoping |
| Execute Phase V proper (security audit + perf load test) | `docs/security/audit-checklist-2026.md` (~89 SEC-* items) + `docs/perf/test-plan.md` | pilot launch |

Optional GPU box availability (post-pilot blocker): confirm A100 / H100 / 4090 with 24 GB+ VRAM for §6.1 Florence-2 fine-tune. Not required for pilot launch — §6.1 is post-pilot.

### 4.3 Phase V execution (security + perf)

**Inputs.** `docs/security/audit-checklist-2026.md` (running checklist with ~89 SEC-* items across 11 categories); `docs/perf/test-plan.md` (load-test plan). Both shipped Sprint 30 / 39 / 52 as living docs. The perf scenarios run on the in-tree NickPerf runner at `tests/NickERP.Perf.Tests/Runner/` post-Sprint-58.

**Phase V gate status: no open P0/P1 blockers from this layer.** The previous SEC-DEP-3 NBomber-license P0 (Sprint 57 triage) was resolved in Sprint 58 by removing NBomber + NBomber.Http + NBomber.Contracts and replacing the runtime with a homegrown in-tree runner (`NickPerfScenario` / `NickPerfRunner` / `NickPerfStats` / `NickPerfReport` / `NickPerfHttp`). Behaviour parity preserved (same per-profile RPS, same skip-on-misconfigured semantics, same acceptance-gate thresholds). Audit re-run on 2026-05-06 shows 0 non-allowlisted licenses. See `tools/security-scan/license-allowlist-rationale.md` §2 + audit-checklist `SEC-DEP-3` for the full trail.

**Output.** Site-scoped `audit-{site}-{date}.md` artifact with each item ticked + per-finding `AUD-{n}` entries. Pilot doesn't ship until all P0 + P1 findings are resolved.

### 4.4 Post-pilot scope (deferred — by design)

- **Image-analysis ML arc**: §6.2 anomaly (DINOv2 + PatchCore) + §6.3 manifest×X-ray scoring + §6.4 active learning + §6.6 TIP + §6.8 beam-hardening + §6.9 threat library + §6.10 HS density = ~30+ sprints. Plus §6.1 OCR (Florence-2) which moved here 2026-05-04. §6.7 dual-view stays deferred (no dual-view scanner in fleet).
- **v1-clone fold-into-v2-native**: NickFinance fold-into-G2 first (~6-10 sprints) then NickHR refactor (~6-10 sprints) — total ~12-22 post-pilot sprints. The fold pattern proven by Sprint 10 G2 pathfinder.
- **User-to-location assignments**: `LocationAssignment` join table + JWT enrichment when multi-location rollout demands per-location enforcement (pilot doesn't require it; one site = one location set).
- **Cross-process LISTEN/NOTIFY event bus**: Sprint 51 shipped LISTEN/NOTIFY for export pickup; the pattern can extend to cross-app event distribution when the second-process consumer arrives.
- **Plugin cryptographic signing**: deferred per "in-house only" decision; revisit if customer demands or audit requires.

### 4.5 Critical sequencing for pilot launch

(Pure operator-side; engineering is done.)

1. Pilot site picked + written agreement signed (gates Phase V scoping)
2. Standby box provisioned + pgbackrest stood up (gates HA cutover)
3. 32 staged migrations applied to live (gates app deploy)
4. Phase V execution complete (P0 + P1 findings resolved) (gates pilot launch)
5. Pilot launch
6. Parity-driven expansion to second site after pilot proves stable

### 4.6 Image-analysis & ML modernization — design status

`docs/IMAGE-ANALYSIS-MODERNIZATION.md` (~2,490 lines as of 2026-04-29) is the design of record. **Eleven specs + scaffolded inference plugin family.**

| Sub-track | Status (2026-05-02) | Pilot scope? |
|---|---|---|
| §4 `IInferenceRunner` plugin contract | ✅ scaffolded Sprint 12; end-to-end smoke test passes | n/a (infrastructure) |
| §3 Container-split student model | spec locked; stub ONNX exported; real fine-tune is GPU-time | post-pilot |
| §5 DICOS readiness | design-ready, deploy-deferred per fleet adoption | n/a |
| §6.1 OCR replacement (Florence-2 / Donut, retiring Tesseract) | scaffolded; eval tool + Florence-2 fine-tune scheduled Sprint 19+ (plan §12) | **pilot-scope** |
| §6.2 HS-conditioned anomaly detection (DINOv2 + PatchCore) | spec locked; entity scaffold (`HsCommodityReference`) Sprint 12 | post-pilot |
| §6.3 Manifest × X-ray consistency scorer | spec locked; entity scaffolds in place | post-pilot |
| §6.4 Active learning loop | spec locked; depends on §6.11 having real data flowing | post-pilot |
| §6.5 Per-scanner threshold calibration | ✅ **shipped Sprint 12** — entity + migration + DI + admin UI at `/admin/thresholds` | n/a |
| §6.6 Threat Image Projection synthetic data | spec locked; tooling unbuilt | post-pilot |
| §6.7 Dual-view registration | contract type added Sprint 12; deploy deferred (no dual-view scanner in fleet) | deferred |
| §6.8 Beam-hardening / metal-streak correction | spec locked; depends on §6.2 | post-pilot |
| §6.9 In-house threat library capture pipeline | spec locked; entity scaffold Sprint 12 | post-pilot |
| §6.10 HS commodity density reference table | spec locked; entity scaffold Sprint 12 (table empty) | post-pilot |
| §6.11 Inbound post-hoc outcome adapter | ✅ **shipped Sprint 13 T3** — pull worker + 4-phase rollout state machine + manual-entry stub + reconciliation cursor | n/a |
| **OCR accuracy eval tool** *(new commitment 2026-05-02)* | not built | **pilot-scope** — gates Florence-2 deploy |

Phase 7.0 contract additions for the Inspection plugin surface (additive, no breakage): `ScannerCapabilities` gained `RawChannelsAvailable`, `SupportsDualView` + `DualViewGeometry`, `SupportsDicosExport` + `DicosFlavors`, `SupportsCalibrationMode`. `ParsedArtifact` gained `FormatVersion`. `ExternalSystemCapabilities` gained `SupportsOutcomePull` + `SupportsOutcomePush`. New `IInboundOutcomeAdapter` interface + supporting types. Contract versions bumped 1.1 to 1.2 on both Abstractions assemblies (Sprint 12 Phase B).

Operational tooling: `tools/v1-label-export/export_splits.py` (read-only export of v1 splitter labels) and `docs/runbooks/vendor-call-2026-04.md` (one-page vendor-call script for FS6000 + ICUMS information-gathering).

---

## 5. Open questions deferred (decide when forced)

| Q | When it bites |
|---|---|
| Conflict resolution on edge-node sync (last-writer vs field-merge) | Active now — edge node shipped Sprint 11 + 13 T2; v0 scope is single-writer, but multi-event fan-out (Sprint 17) may surface this. |
| Station-to-Device binding rotation policy | When stations rotate scanners mid-day (sooner if multi-shift) |
| Dual-review enforcement (two analysts on high-value cases) | Partially resolved 2026-05-02 — VP6 AnalysisService N:N + first-claim-wins under shared visibility supports this; tenant-configurable case-visibility model picks shared/exclusive. |
| Post-hoc outcome capture (customs seizure feedback for ML labels) | **Resolved 2026-04-29** — §6.11 inbound post-hoc adapter shipped Sprint 13 T3 (manual-entry stub live; awaits real `IInboundOutcomeAdapter` plugin against ICUMS). |
| Per-`ExternalSystemInstance` rate limiting / token-bucket | Before first real external-system call hits production scale |
| Data residency (per-tenant cluster?) | Before second tenant outside Ghana |
| Operator identity at the scanner (does the scanner know who's using it?) | When multi-operator shifts hit |
| Pilot-site selection | Decision matrix in plan file §13; final call due by Sprint 22-24 (gates Phase V scoping) |
| GPU box availability for Florence-2 training | Before Sprint 16-17 (ML training arc plan §12) |

---

## 6. Out of scope *(refreshed 2026-05-02)*

- v1 modifications. Period.
- ~~Rebuilding NickHR~~ — **moved into scope 2026-05-02.** NickHR will be v1-cloned (Sprint 15) for three-module pilot co-deploy. v2-native refactor is post-pilot (~6-10 sprints).
- Rebuilding NickComms — still adapted via shim, not rebuilt.
- Public plugin API (in-house only).
- Mobile native app (responsive web; revisit when field operators complain).
- ~~AI-driven analysis assistance~~ — **moved into scope** 2026-04-28. Specs in `docs/IMAGE-ANALYSIS-MODERNIZATION.md`. Sprint 12 + 13 shipped §6.5 + §6.11 (foundations); §6.1 OCR is pilot-scope (Sprint 19+); §6.2 / 6.3 / 6.4 / 6.6 / 6.8 / 6.9 / 6.10 are post-pilot.

### What remains genuinely external (cannot be planned within this session)

The four items previously in plan-file §9 (sprint dispatch, v1 parity, ML training arc, pilot-site selection) are **now planned** in plan file §10-§13. Two things still depend on external inputs:

- **GPU compute runtime** for Florence-2 fine-tune (~6-12 h per run × multiple iterations; ~48-72 h cumulative across 5-10 calendar days). The work is sequenced in plan §12; the wall-clock when it happens depends on GPU box availability.
- **Final pilot-site pick.** Decision framework + tentative front-runners (Kotoka or Takoradi) in plan §13; the actual call requires inputs only the user has (operator-cooperation status per site, contractual / political constraints, strategic visibility considerations). Due by Sprint 22-24.

---

## 7. How we track

This file is the source of truth for v2 planning. Edit freely.

- Per-task work → one git branch per layer or feature, merged to `main` via PR or fast-forward.
- Each shipped feature → tick the box here, add a line to the corresponding module's `*.md`.
- Architectural changes → update `docs/ARCHITECTURE.md` first, then implement.

Adjacent docs:

- `docs/architectural-design-analysis-2026-05-13.md` — latest source-backed architecture analysis and target design proposal.
- `docs/ARCHITECTURE.md` — the full design of record (entity model, plugin contracts, cross-cutting concerns).
- `docs/MIGRATION-FROM-V1.md` — cutover plan stub (grows as parallel-run gets closer).
- `TESTING.md` — how to run + click through what's built today.
- Per-package `*.md` files in each `platform/*` directory.

---

## 8. Glossary

| Term | Means |
|---|---|
| **Tenant** | One isolated platform deployment (one customer). Default tenant 1 = "Nick TC-Scan Operations." |
| **Location** | A physical inspection site (Tema Port, Kotoka Cargo). Federation unit. |
| **Station** | A scanning lane / post within a Location. |
| **ScannerDeviceInstance** | A physical scanner unit, owned by a Location, currently at zero or one Stations. |
| **ScannerDeviceType** | Plugin-defined kind of scanner (FS6000, ASE, mock). Lives in a `Scanners.<Vendor>` adapter. |
| **ExternalSystemInstance** | A configured authority endpoint (an ICUMS deployment, a GRA endpoint). |
| **ExternalSystemType** | Plugin-defined kind of authority system (icums-gh, gra-gh, mock). Lives in `ExternalSystems.<Vendor>`. |
| **InspectionCase** | One consignment going through inspection at a Location. |
| **AuthorityDocument** | Evidence from an external system attached to a case (BOE, CMR, IM in CustomsGh terms). |
| **AuthorityRulesProvider** | Country/authority-specific validation + inference (e.g. CustomsGh for Ghana). |
| **Verdict** | Composite decision on a case (Clear / HoldForInspection / Seize / Inconclusive). |
| **OutboundSubmission** | Dispatch of a verdict back to an external system, with idempotency key. |
