# NickERP v2 — Roadmap

> Independent v2 roadmap. v1 is **read-only** during this build. If we need
> something from v1 we copy it as a point-in-time port — we do not edit
> the v1 tree.
>
> This file replaces any v1-side roadmap reference. Edit it freely.

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
| **External system binding** | `ExternalSystemInstance.Scope ∈ { PerLocation, Shared }` + many-to-many join `ExternalSystemBinding` to locations. Picked when adding the instance. | Both modes work; the choice is per-instance, made at onboarding. |
| **Repo** | Greenfield monorepo at `C:\Shared\ERP V2\` + `github.com/bjforson/ERP-V2` (private). v1 stays untouched. | The "new folder, don't touch v1" rule. |
| **Identity** | One canonical `IdentityUser` keyed on lowercased email. Cloudflare Access JWT validation against CF JWKS. Users assigned per-location with per-app role lists (no flat global permission). | "Assign users to locations instead of one flat system." |
| **Tenancy** | Multi-tenant from line 1. `TenantId` (long) on every entity via `ITenantOwned`. Stamping by EF SaveChanges interceptor. Postgres RLS as defense-in-depth via `app.tenant_id` session var. | Decision locked + structurally enforced now so no entity can opt out later. |
| **Image pipeline** | Pre-rendering baked into Inspection v2 from line 1 (thumbnails 256 px, previews 1024 px, Redis + disk tiers, ETag/`Cache-Control` streaming). **No base64 image marshalling, ever.** | At expected scale (~2000 images/day per location) base64-per-request fails — repeating v1's mistake is non-negotiable. Spec already in `docs/ARCHITECTURE.md` §7.7. |
| **Connectivity** | Online-first. Central API is the primary path. Every state change is a `DomainEvent` with idempotency key — that contract enables a future edge node to replay its log on reconnect. | Online clean today; offline-capable later without re-architecture. |
| **Web stack** | Blazor Server for the primary admin + analyst web. Shared chrome (TopNav / UserMenu / NotificationBell / AppSwitcher) lives in `NickERP.Platform.Web.Shared`. | Team familiarity + clean SignalR path. Edge offline UI later via separate thin client. |
| **Audit + events** | One append-only `audit.events` table. Every state change emits a `DomainEvent` via `IEventPublisher`. In-process `IEventBus` today; cross-process LISTEN/NOTIFY later. | Compliance audit trail + cross-app integration + idempotency, all from one record. |

---

## 3. Status — what's done vs. left, mapped to the vision

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

### 3.2 Apps and modules — Track B *(refreshed 2026-05-02)*

| App / module | Status | Live URL / location | Next |
|---|---|---|---|
| **Portal v2** (B.2) | ✅ shipped | http://localhost:5400 | EdgeKeys page (Sprint 13 T2), Sprint dashboard, Tenants, Audit log, Health all live. Three-module navigation pending (Sprint 23 in §10.3). |
| **Inspection v2 admin** (B.1.0) | ✅ skeleton shipped | http://localhost:5410 | 17 Razor pages including `/admin/thresholds` (Sprint 12 §6.5), `/admin/posthoc-outcomes` (Sprint 13 §6.11), case viewer, scanners, locations, etc. v1 parity batches B1-B8 (§11) close the rest. |
| **NickFinance — Petty Cash** (B.3.1) | ✅ pathfinder shipped (Sprint 10 G2) | `modules/nickfinance/` | 5 pages live: `/finance/petty-cash/boxes` + `/{id}`, `/periods`, `/vouchers/{id}`, `/fx-rates`. Coexists with `v1-clone/finance/` (full v1 functionality). v1-clone fold-into-G2 is post-pilot. |
| **HR v2** | 🟡 v1-clone planned for pilot | — | Clone scheduled Sprint 15 (§10.3). Co-deploys with inspection + NickFinance. v2-native refactor is post-pilot. |
| **Comms v2** | n/a — adapted not rebuilt | — | Shim later, no rewrite. |
| **Edge node** | ✅ shipped (Sprints 11 + 13 T2) | `apps/edge-node` | SQLite buffer + `/api/edge/replay` + per-edge HMAC API keys + rotation. v0 = audit events only; multi-event fan-out (P2-FU-multi-event-types) now must-have-pilot, scheduled Sprint 17. |
| **Threshold calibration** (§6.5) | ✅ shipped Sprint 12 | `/admin/thresholds` | Idle until first scanner instance lands; activates on onboarding. |
| **Post-hoc outcome adapter** (§6.11) | ✅ shipped Sprint 13 T3 | `/admin/posthoc-outcomes` + `OutcomePullWorker` | Manual entry stub live; awaits real `IInboundOutcomeAdapter` plugin against ICUMS. |
| **Audit notifications projection** | ✅ shipped Sprint 8 P3 | `audit.notifications` table | Inbox UI deferred to parity Batch B8.1. |

### 3.3 Vision-element coverage *(refreshed 2026-05-02)*

How each item of the vision is reflected in code today:

| Vision element | Where it lives | Status |
|---|---|---|
| **Federation by location** | `inspection.Location` + `inspection.Station` entities; `ScannerDeviceInstance.LocationId` constraint; admin UI to add Locations and Stations. | ✅ Schema + admin in. Cases / scans bound to locations via `InspectionCase.LocationId`. |
| **AnalysisService (VP6)** *(new 2026-05-02)* | Not yet built. N:N location↔service, immutable "All Locations" default, first-claim-wins under shared visibility. | 🔴 **Gap — Sprint 14.** Blocks inspection v1 parity work that touches case visibility. |
| **Per-location setup flow** | Admin pages: `/locations` → `/stations` → `/scanners` → `/external-systems`. Each step references the previous. | ✅ Working end-to-end as a scaffold. |
| **Generic nomenclature** | `ScannerDeviceInstance.TypeCode` + `ExternalSystemInstance.TypeCode`. Plugin contracts in `*.Abstractions` projects. Core has zero vendor names. | ✅ Structurally enforced. |
| **External system bindings (per-location / subset / shared)** | `ExternalSystemBindingScope` enum on `ExternalSystemInstance`; `ExternalSystemBinding` join table backs all three scopes. | ✅ All three scopes shipped Sprint 16 (2026-05-04): PerLocation = 1 binding, SubsetOfLocations = 2+ bindings, Shared = 0 bindings. `ExternalSystemAdminService.RegisterAsync` enforces per-scope cardinality + tenant location resolution; `ResolveServingInstancesAsync` is the canonical lookup helper. |
| **User-to-location assignments** | `LocationAssignments.razor` page exists. JWT principal does not yet carry location ids. | 🟡 To add: `LocationAssignment` join table + JWT enrichment. |
| **Greenfield, no v1 mingling** | Separate repos, separate Postgres DBs, no v1 references in core. NickFinance v1-clone in `v1-clone/finance/` (point-in-time port pattern). NickHR clone scheduled Sprint 15. | ✅ Locked + memory-pinned. |
| **ERP context** | Platform layers shared. NickFinance G2 pathfinder proves the second-module shape works. Three-module pilot: inspection + NickFinance + NickHR. | ✅ Structurally proven. |
| **Online-first, edge-for-backup** | Edge node SQLite buffer + `/api/edge/replay` + per-edge HMAC API keys (Sprints 11 + 13 T2 + 17). v0 mandatory event set: scan-captured + scanner-status-changed + audit — all three shipping. | ✅ All three event types shipped Sprint 17 (2026-05-04). Server-side `EdgeReplayEndpoint.HandleAsync` dispatches per `EventTypeHint` to payload-shape resolvers (`AuditEventReplayPayload` / `ScanCapturedPayload` / `ScannerStatusChangedPayload`). Edge-side typed helpers: `CaptureAuditEventAsync` / `CaptureScanCapturedAsync` / `CaptureScannerStatusChangedAsync`. Inspection-side projector consuming the two new event types deferred to a follow-on sprint — the durable record is the audit row. |
| **Central Postgres** | 3 physical DBs × 5 schemas. App role `nscim_app` (NOSUPERUSER, NOBYPASSRLS). Single-host dev today. **HA + pgbackrest + PG17 lock = Sprint ~Late (post-parity, pre-pilot).** | 🟡 Logical layout in. Operational HA pending. |
| **Multi-tenant day 1** | `ITenantOwned` + interceptor + 180+ RLS policies + 5-entry system-context audit register. **Tenant lifecycle (state, soft-delete, email service for first-user invite) not yet built.** | 🟡 RLS mature. Lifecycle = Sprints 18-21. |
| **Plugins always in-house** | Filesystem trust, no signing. Contract version pinned at 1.2 since Sprint 12. | ✅ Stable. |
| **Inspection v1 parity** *(new 2026-05-02)* | v2 inspection has 17 Razor pages + scaffolding. v1 NSCIM has ~40+ pages, 17 endpoint areas, 15+ background services. | 🔴 **Big gap — 14-20 sprints across Batches B1-B8 (§11).** Pilot needs all-v1-parity locked. |
| **§6.x image-analysis arc** | Threshold calibration (§6.5) + Post-hoc adapter (§6.11) shipped. §6.1 OCR (Florence-2) is pilot-scope; §6.2 / 6.3 / 6.4 / 6.6 / 6.8 / 6.9 / 6.10 are post-pilot. §6.7 deferred (no dual-view scanner). | 🟡 2 of 11 shipped; 1 pilot-scope; 7 post-pilot; 1 deferred. |

---

## 4. What's next — concrete *(refreshed 2026-05-02)*

**Pre-pilot scope ~31-45 sprints. Pilot deadline locked at 6-9 months.** Detailed dispatch sequence + v1 parity breakdown + ML training arc + pilot-site decision matrix live in `~/.claude/plans/tingly-launching-quasar.md` (the durable plan file).

### 4.1 Pre-pilot workstreams (Sprint 14 onwards)

Six parallel-safe groups:

| Group | Workstreams | Sprints |
|---|---|---|
| **α** Foundational | AnalysisService VP6 (Sprint 14), `ExternalSystemInstance` subset junction (Sprint 16) | 3-5 |
| **β** Tenant + ops | Tenant lifecycle pts 1-3 (state + soft-delete + email service + scoped export), HA + backups + PG17, P2-FU-multi-event-types | 6-10 |
| **γ** Modules | NickHR clone (Sprint 15), three-module co-deploy navigation (Sprint 23) | 2-3 |
| **δ** ML | OCR eval harness (Sprint 19), §6.1 Florence-2 integration (Sprint 23-25) | 4-6 |
| **ε** v1 parity (long pole) | Inspection Batches B1-B8 — case viewing, ICUMS UIs, background services, validation rules, completeness, specialised review workflows, monitoring/reporting, admin/housekeeping | 13-18 |
| **ζ** Pilot prep | Phase V lightweight (security audit + perf test), pilot site deployment + monitoring | 3-5 |

### 4.2 Critical sequencing

- AnalysisService VP6 (Sprint 14) blocks parity work touching case visibility (most batches)
- Tenant state (Sprint 18) blocks email + first-user (Sprint 21) which blocks pilot
- OCR eval harness + v1 baseline (Sprint 19) blocks Florence-2 production deploy
- Florence-2 GPU fine-tune must START by Sprint 16-17 (out-of-band; ~6-12h per run × multiple iterations)

### 4.3 Pilot-site call

Decision framework in plan file §13. Two-step: hard gates (scanner, connectivity, operator cooperation, written agreement) + weighted scoring across 8 criteria. Tentative front-runners: **Kotoka Cargo (KIA) or Takoradi** — moderate traffic, good connectivity, lower complexity than Tema. Border sites are post-pilot expansion (need edge-node hardening first). Final call due by Sprint 22-24 (Phase V scopes against the pilot site).

### 4.4 Operator action waiting

- **Apply 32 staged migrations to live** (`tools/migrations/sprint-13-deploy/*.sql`, runbook `07-sprint-13-live-deploy.md`). Inspection DB is currently 5 migrations ahead of platform / nickfinance which haven't been applied at all.
- **Confirm GPU box availability** for Sprint 16+ Florence-2 training. Without it, the OCR pilot scope slips.

### 4.5 Image-analysis post-pilot arc

§6.2 anomaly + §6.3 consistency + §6.4 active learning + §6.6 TIP + §6.8 beam-hardening + §6.9 threat library + §6.10 HS density = ~30+ sprints of post-pilot ML work. §6.7 dual-view stays deferred (no dual-view scanner in fleet). v1-clones refactor (NickFinance fold-into-G2, NickHR refactor to v2-native) = ~12-22 additional post-pilot sprints.

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
