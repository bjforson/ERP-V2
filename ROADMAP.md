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

Open-question answers (locked):

- **External system bindings**: per-location AND shared — chosen at onboarding per instance.
- **Online-first**, with an edge-for-backup design (offline + backfill on reconnect) baked into events but built later.
- **Central Postgres** cluster.
- **Multi-tenant from day 1.**
- **Timeline**: months, phase-gated, no hard deadline.
- **Plugins**: always in-house.
- **More NickERP domains coming** (Finance, etc.).

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

### 3.2 Apps and modules — Track B

| App / module | Status | Live URL | Next |
|---|---|---|---|
| **Portal v2** (B.2) | ✅ skeleton shipped | http://localhost:5400 | Real federated search; live notification stream once audit-projection lands; tenants edit + delete. |
| **Inspection v2 admin** (B.1.0) | ✅ skeleton shipped | http://localhost:5410 | The actual case lifecycle (B.1.1) — see §4 below. |
| **NickFinance — Petty Cash** (B.3.1) | ☐ not started | — | Pathfinder finance module; needs Identity + Tenancy + Audit (all done) + a Money type + a basic ledger. |
| **HR v2** | n/a — adapted not rebuilt | — | When v1 NickHR adopts the new platform via adapter shims. Out of scope here. |
| **Comms v2** | n/a — adapted not rebuilt | — | Same — adapter shim, no rewrite. |

### 3.3 Vision-element coverage

How each item of the vision is reflected in code today:

| Vision element | Where it lives | Status |
|---|---|---|
| **Federation by location** | `inspection.Location` + `inspection.Station` entities; `ScannerDeviceInstance.LocationId` constraint; admin UI to add Locations and Stations. | Schema + admin in. **Cases / scans not yet bound to locations** (those entities don't exist yet). |
| **Per-location setup flow** | Admin pages: `/locations` → `/stations` → `/scanners` → `/external-systems`. Each step references the previous. | Working end-to-end as a scaffold. Refining once real cases flow through. |
| **Generic nomenclature** | `ScannerDeviceInstance.TypeCode` (string, e.g. `mock-scanner`, future `fs6000`). `ExternalSystemInstance.TypeCode` (e.g. `mock-external`, future `icums-gh`). Plugin contracts in `*.Abstractions` projects. Core has zero vendor names. | ✅ Structurally enforced. The compiler refuses to leak vendor names into core. |
| **External system bindings (per-location OR shared)** | `ExternalSystemBindingScope` enum on `ExternalSystemInstance`; `ExternalSystemBinding` join table for shared mode. | ✅ Schema in. Admin UI exposes the choice. |
| **User-to-location assignments** | `IdentityUser` + `UserScope` + `AppScope` carry per-app role lists. Per-location user assignment is **not yet wired** — currently a user belongs to a tenant, not a location. | 🟡 To add: `LocationAssignment` (UserId × LocationId × Roles) join table. |
| **Greenfield, no v1 mingling** | Separate repo, separate Postgres DBs, separate Seq instance, no v1 references. v2 chrome links nowhere into v1. | ✅ Locked + memory-pinned. |
| **ERP context** | Platform layers shared across future modules (Finance, etc.). `IPluginRegistry` lets modules register their domain-specific plugin contracts without core changes. | ✅ Structurally in place. Real cross-module test happens when Finance lands. |
| **Online-first, edge-for-backup** | Online: live on TEST-SERVER. Edge: every state change is a `DomainEvent` with idempotency key — the contract supports replay; the edge node implementation is deferred. | 🟡 Online done. Edge deferred to B.1.6. |
| **Central Postgres** | Single cluster. Three databases: `nickerp_platform`, `nickerp_inspection`, future `nickerp_finance` etc. RLS-ready. | ✅ |
| **Multi-tenant day 1** | `ITenantOwned` enforced via interceptor; `TenantId` on every business entity; RLS templates documented. | ✅ |
| **Plugins always in-house** | Plugin manifest + loader trusts the assemblies in `plugins/`. No signature checks, no isolation — explicitly an in-house-only choice. | ✅ |

---

## 4. What's next — concrete

The next chunk of work, ordered by what unblocks what.

### 4.1 Inspection v2 — case lifecycle (B.1.1)

The skeleton today shows scanners and external systems in a vacuum. Real value is the case lifecycle. New entities:

- `InspectionCase` — one consignment going through inspection at a Location. Subject (Container / Truck / Parcel / Bag), opened/closed timestamps, current workflow state, correlation id.
- `Scan` — one capture event by a `ScannerDeviceInstance` against a case.
- `ScanArtifact` — one image/channel/side-view per scan; storage URI, hash, dimensions.
- `AuthorityDocument` — evidence pulled from an `ExternalSystemInstance` (BOE, CMR, IM, etc.) tied to a case.
- `InspectionWorkflow` — state machine: open → validated → assigned → reviewed → verdict → submitted → closed.
- `ReviewSession` + `AnalystReview` — analyst's work product, including ML telemetry (time-to-decision, ROI interactions, confidence).
- `Finding` + `Verdict` — observations + composite decision.
- `OutboundSubmission` — dispatch to an external system with idempotency key.

Plus the **image pre-rendering pipeline** baked in from this phase per `docs/ARCHITECTURE.md` §7.7: thumbnails (256 px), previews (1024 px), Redis + disk tiers, ETag/Cache-Control streaming, predictive prefetch, SignalR `AssetReady` push. **No base64 anywhere.**

### 4.2 First real adapters

To make B.1.1 actually run a case end-to-end:

- **`NickERP.Inspection.Scanners.FS6000`** — port the v1 FS6000 decoder (point-in-time copy from v1, restructured). Concrete `IScannerAdapter`. Reads from local disk staging, parses BMP/TIFF, returns `ParsedArtifact`.
- **`NickERP.Inspection.ExternalSystems.IcumsGh`** — port the v1 ICUMS ingestion + outbox. Concrete `IExternalSystemAdapter`. Pulls BOE/CMR/IM, submits verdicts.
- **`NickERP.Inspection.Authorities.CustomsGh`** — port v1's Ghana customs rules (port-match, Fyco, regime validation, CMR→IM upgrade). Concrete `IAuthorityRulesProvider`.

All three are in-house plugins; the inspection host loads them from its `plugins/` folder.

### 4.3 Analyst review UI

The page where an analyst sees a case → its scans → the rendered image → the authority documents → records a finding → submits a verdict. Ports the v1 viewer arc (W/L sliders, 16-bit client-side decode, pixel probe, ROI inspector) into Blazor components inside Inspection v2 Web.

### 4.4 User-to-location assignments

Add `LocationAssignment` (User × Location × Roles) to inspection. Wire admin UI to assign a user to one or many locations. Update the JWT principal to carry the user's accessible location ids; the inspection module filters every query by both `TenantId` and `LocationId IN (allowed)`.

### 4.5 Multi-location tooling

Once one Location works end-to-end (Tema), prove the model with a second one (Kotoka). Each can use its own ICUMS endpoint OR a shared national one — both via the existing binding model.

### 4.6 NickFinance — Petty Cash (B.3.1)

The pathfinder finance module. Needs:
- Money value type + currency conversion contract
- Ledger kernel (immutable journals, period locks)
- Petty Cash domain (floats, vouchers, custodians, approvals)
- WhatsApp / MoMo touchpoints (deferred adapters)

Same pattern as Inspection: `modules/finance/petty-cash/` with `src/`, `plugins/`, etc.

### 4.7 Audit-events projection + notifications inbox

Right now `audit.events` is queryable but no derived views. Build a notifications-inbox projection (per-user actionable subset) so `NotificationBell` lights up with real content.

### 4.8 Edge node (B.1.6 — post-cutover)

Lightweight per-location node that buffers scans during WAN outages and replays the event log on reconnect. Designed-for since the audit-event idempotency-key contract was set; built when Inspection v2 has live traffic.

### 4.9 Image-analysis & ML modernization (B.1.5)

`docs/IMAGE-ANALYSIS-MODERNIZATION.md` (~2,490 lines as of 2026-04-29) is the design of record for the ML / calibration / standards layer that sits on top of the rendering pipeline. **Eleven specs + scaffolded inference plugin family.**

| Sub-track | Status |
|---|---|
| §4 `IInferenceRunner` plugin contract — `Inference.Abstractions` + `Inference.OnnxRuntime` + `Inference.Mock` | scaffolded 2026-04-28; end-to-end smoke test passes 2026-04-29 |
| §3 Container-split student model (replaces v1's per-scan Anthropic round-trip) | spec locked 2026-04-28; first stub model exported to ONNX 2026-04-29 |
| §5 DICOS readiness | design-ready, deploy-deferred per fleet adoption |
| §6.1 OCR replacement (Florence-2 / Donut, retiring Tesseract) | spec locked |
| §6.2 HS-conditioned anomaly detection (DINOv2 + PatchCore) | spec locked — first published cargo-X-ray application |
| §6.3 Manifest x X-ray consistency scorer | spec locked |
| §6.4 Active learning loop (Label Studio + SAM 2 + MLflow on-prem) | spec locked — closes §5's "Post-hoc outcome capture" open question |
| §6.5 Per-scanner threshold calibration (`ScannerThresholdProfile`) | spec locked; entity + migration landed 2026-04-29; admin UI + DI wiring in Sprint 12 |
| §6.6 Threat Image Projection synthetic data | spec locked |
| §6.7 Dual-view registration | design-ready, deploy-deferred per fleet |
| §6.8 Beam-hardening / metal-streak correction | spec locked |
| §6.9 In-house threat library capture pipeline | spec locked; entity + migration landed 2026-04-29 |
| §6.10 HS commodity density reference table | spec locked; entity + migration landed 2026-04-29 |
| §6.11 Inbound post-hoc outcome adapter | spec locked; entity + migration landed 2026-04-29 |

Phase 7.0 contract additions for the Inspection plugin surface (additive, no breakage): `ScannerCapabilities` gained `RawChannelsAvailable`, `SupportsDualView` + `DualViewGeometry`, `SupportsDicosExport` + `DicosFlavors`, `SupportsCalibrationMode`. `ParsedArtifact` gained `FormatVersion`. `ExternalSystemCapabilities` gained `SupportsOutcomePull` + `SupportsOutcomePush`. New `IInboundOutcomeAdapter` interface + supporting types. Contract versions bumped 1.1 to 1.2 on both Abstractions assemblies (Sprint 12 Phase B).

Operational tooling: `tools/v1-label-export/export_splits.py` (read-only export of v1 splitter labels) and `docs/runbooks/vendor-call-2026-04.md` (one-page vendor-call script for FS6000 + ICUMS information-gathering).

---

## 5. Open questions deferred (decide when forced)

| Q | When it bites |
|---|---|
| Conflict resolution on edge-node sync (last-writer vs field-merge) | Before edge node is built (4.8) |
| Station-to-Device binding rotation policy | When stations rotate scanners mid-day (sooner if multi-shift) |
| Dual-review enforcement (two analysts on high-value cases) | When compliance demands it |
| Post-hoc outcome capture (customs seizure feedback for ML labels) | **Resolved 2026-04-29** — `docs/IMAGE-ANALYSIS-MODERNIZATION.md` §6.4 + §6.11 (inbound `IInboundOutcomeAdapter`, supersession-chain idempotency, manual-entry fallback) |
| Per-ExternalSystemInstance rate limiting / token-bucket | Before first real external-system call (B.1.2) |
| Data residency (per-tenant cluster?) | Before second tenant outside Ghana |
| Operator identity at the scanner (does the scanner know who's using it?) | When multi-operator shifts hit |

---

## 6. Out of scope

- v1 modifications. Period.
- Rebuilding NickHR or NickComms — adapted via shims later, not rebuilt.
- Public plugin API (in-house only).
- Mobile native app (responsive web for v1; revisit when field operators complain).
- ~~AI-driven analysis assistance~~ — **moved into scope** as B.1.5 in §4.9 (2026-04-28). Designed via the ML telemetry capture in `AnalystReview`; specs in `docs/IMAGE-ANALYSIS-MODERNIZATION.md`; first scaffold landed Sprint 12 (2026-04-29).

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
