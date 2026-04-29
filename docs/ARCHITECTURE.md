# NickERP.Inspection — Architecture (v2)

> **Status:** design of record. Edit freely; this doc and the code evolve together.
> **Repo:** `C:\Shared\ERP V2\` — separate git repo from v1.
> **Parent roadmap:** `C:\Shared\NSCIM_PRODUCTION\ROADMAP.md` → Phase 7 (lives in the v1 repo).
> **Migration companion:** [`MIGRATION-FROM-V1.md`](MIGRATION-FROM-V1.md)
> **First written:** 2026-04-24 · **Relocated to standalone repo:** 2026-04-25

---

## 1. Purpose

Rebuild the NSCIM scan / analysis / authority-submission pipeline as a **vendor-neutral, location-federated, plugin-driven** platform that:

- Treats *location* as a first-class citizen — every user, scanner, external system, case, and finding lives under a Location.
- Treats hardware vendors and external authorities as **plugins**, not core concepts — "FS6000" and "ICUMS" do not appear in the domain model.
- Lives inside the NickERP platform — consumes shared Identity, Tenancy, Comms, and Audit. Does **not** re-implement them.
- Ships online-first with a clean contract for an offline edge node to be added later without core changes.
- Grows cleanly alongside future NickERP modules (Finance, Fleet, Customs per-country).

---

## 2. Guiding decisions (locked 2026-04-24)

| # | Decision | Implication |
|---|---|---|
| D1 | **Online-first, edge-for-backup.** Central API is the primary path. An optional edge node per location buffers scans when WAN is down and backfills on reconnect. | All state changes are **events with idempotency keys** from day 1. Sync protocol is designed in the domain events model even though the edge binary is built later. |
| D2 | **Blazor Server** for the primary web app. | Fast iteration, familiar to the team. Clean REST + SignalR API underneath means a future WebAssembly or native edge client can be added without domain rework. |
| D3 | **Central Postgres** cluster (same as v1 + NickHR + Comms). | One ops footprint. Row-level security (RLS) does the isolation, not separate DBs. Edge nodes later carry their own embedded store (SQLite / Postgres-lite), sync via the event log. |
| D4 | **Multi-tenant from day 1.** | `tenant_id` on every row. Tenancy and Location are **orthogonal** — both filters apply. Single-customer for now doesn't mean single-tenant-schema. |
| D5 | **Timeline: months, flexible.** | No Big Bang deadline. Phase gates drive progression. Cutover is a deliberate event, not a target date. |
| D6 | **In-house plugins.** | No public contract versioning pressure. Plugin interfaces can evolve with the core — but still treat the boundary as a contract; **do not leak domain entities through plugin interfaces.** |
| D7 | **More NickERP domains coming** (Finance, Customs modules, Fleet placeholder). | Shared concepts (`Tenant`, `Location`, `User`, `AuditEvent`, `AuthorityDocument`) live in the NickERP platform, not inside Inspection. Inspection *consumes* them. |
| D8 | **External system bindings: both modes, choose at onboarding.** A binding declares itself either `scope: per-location` (one instance per Location) or `scope: shared` (one instance serves many Locations, scoped by query parameter). | `ExternalSystemInstance` ↔ `Location` is many-to-many with a `bindingScope` enum on the join row. The admin UI asks "Does this Tema ICUMS endpoint belong only to Tema, or is it a shared national endpoint?" during onboarding. |

---

## 3. Domain vocabulary (the naming contract)

**Rule:** vendor names, country-specific authority names, and v1 jargon belong in adapters or country modules — **never in core domain entities, interfaces, or URLs.**

| v1 term | v2 term | Lives where |
|---|---|---|
| ICUMS | `ExternalSystemInstance` of `ExternalSystemType.Customs` | Core domain (instance); adapter named `NickERP.Inspection.ExternalSystems.IcumsGh` |
| FS6000, ASE, Nuctech, HeimannSmith | `ScannerDeviceInstance` of `ScannerDeviceType` | Core (instance); adapters in `NickERP.Inspection.Scanners.{Vendor}` |
| "container" | `InspectionCase` with `CaseSubjectType` (Container, Truck, Parcel, Bag) | Core |
| BOE, declaration, CMR, IM | `AuthorityDocument` with `DocumentType` enum + typed payload | Core holds the shape; CustomsGh module defines the concrete types |
| regime code, importer, exporter, port-of-origin | Fields on `AuthorityDocument.CustomsPayload` | `NickERP.Inspection.Authorities.CustomsGh` |
| Scan | `Scan` | Core |
| Scan image / artefact | `ScanArtifact` (one row per image/channel/side-view) | Core |
| ImageProcessing / ImageAnalysis pipeline | `InspectionWorkflow` | Core |
| AnalystCompleted, AnalysisGroup | `ReviewSession`, `AnalystReview` | Core |
| verdict / analysis result | `Finding` (many per review) + `Verdict` (one per case, composite) | Core |
| ContainerCompleteness | `CaseCompleteness` | Core |
| "submit to ICUMS" | `OutboundSubmission` to `ExternalSystemInstance` | Core dispatches, adapter shapes the payload |
| port-match, Fyco, regime-80 | `IAuthorityRulesProvider.ValidateAsync(case)` | `Authorities.CustomsGh` |
| CMR→IM upgrade | Rule inside `Authorities.CustomsGh.InferMissingFieldsAsync` | Same |

**If a field name contains a vendor or country code, it belongs in an adapter. Period.**

---

## 4. Organizational hierarchy

```
Tenant                      (customer — e.g. "Ghana Revenue Authority")
  │
  └─ Region                 (optional grouping — "Greater Accra")
       │
       └─ Location          (physical site — "Tema Port")
            │
            ├─ Station      (scanning lane — "Tema Port / Lane 1")
            │    │
            │    └─ DeviceBinding  (which scanner is at this lane today)
            │         │
            │         └─ ScannerDeviceInstance  (FS6000 serial #42)
            │
            └─ ExternalSystemBinding
                 │
                 └─ ExternalSystemInstance  (Tema ICUMS endpoint, OR a shared national one)
```

**Key invariants:**

- Every domain row carries `tenant_id` and (where applicable) `location_id`.
- A `ScannerDeviceInstance` is owned by a `Location`; it can be reassigned between Stations within that Location, but not across Locations (prevents accidental cross-location data contamination).
- `ExternalSystemInstance` can be `scope=perLocation` (owned by exactly one Location) or `scope=shared` (joined to N Locations via `ExternalSystemBinding` rows). Chosen at onboarding; changeable with explicit migration.
- `User` has zero-to-many `LocationAssignment`s. Role is scoped per-location. A user with no Location assignments sees nothing. A Tenant Admin sees the whole Tenant.
- `Region` is optional and used only for rollup reports; business logic never branches on Region.

---

## 5. Core domain model

### 5.1 Case-level entities

| Entity | Purpose | Key fields |
|---|---|---|
| `InspectionCase` | One consignment going through inspection at one Location | `tenant_id`, `location_id`, `station_id`, `subject_type`, `subject_identifier`, `status`, `opened_at`, `closed_at`, `correlation_id` |
| `CaseSubject` | Polymorphic — what's being inspected | `subject_type` ∈ {Container, Truck, Parcel, Bag, Other}; type-specific fields in a `jsonb` payload |
| `AuthorityDocument` | Evidence from an external authority linked to the case | `case_id`, `external_system_instance_id`, `document_type`, `reference_number`, `received_at`, `payload jsonb` |
| `Scan` | One scanning event — a device capturing a subject | `case_id`, `scanner_device_instance_id`, `captured_at`, `operator_user_id`, `mode`, `correlation_id` |
| `ScanArtifact` | One image / channel / side-view produced by a scan | `scan_id`, `artifact_kind` (Primary, SideView, Material, IR, ROI), `storage_uri`, `mime_type`, `width`, `height`, `hash` |
| `InspectionWorkflow` | State machine — open → validated → assigned → reviewed → verdict → submitted → closed | `case_id`, `current_state`, `entered_state_at`, `history jsonb` |
| `ReviewSession` | An analyst picking up a case | `case_id`, `analyst_user_id`, `started_at`, `ended_at`, `outcome` |
| `AnalystReview` | The analyst's work product within a session | `session_id`, `findings[]`, `verdict_id`, `confidence_score`, `time_to_decision_ms`, `roi_interactions jsonb` |
| `Finding` | A single observation | `review_id`, `finding_type`, `severity`, `location_in_image jsonb` (ROI box), `note` |
| `Verdict` | Composite decision on a case | `case_id`, `decision` ∈ {Clear, HoldForInspection, Seize, Inconclusive}, `decided_at`, `decided_by_user_id`, `basis` |
| `OutboundSubmission` | Dispatch to an external system | `case_id`, `external_system_instance_id`, `payload jsonb`, `submitted_at`, `idempotency_key`, `status`, `response_jsonb` |
| `CaseCompleteness` | Validation state for completeness rules | `case_id`, `rule_set_version`, `violations jsonb`, `ready_for_review bool` |

### 5.2 Plugin & configuration entities

| Entity | Purpose |
|---|---|
| `ScannerDeviceType` | Registered plugin — populated at startup from loaded adapter assemblies |
| `ScannerDeviceInstance` | A configured device at a Station — references a `ScannerDeviceType` + carries config matching the type's schema |
| `ExternalSystemType` | Registered plugin — e.g. `Customs` category with specific implementations |
| `ExternalSystemInstance` | Configured external system — type + config + `scope` ∈ {perLocation, shared} |
| `ExternalSystemBinding` | Join row between `ExternalSystemInstance` and `Location`, with `role` (primary, secondary) |
| `AuthorityRulesProvider` | Plugin — country/authority-specific validation and inference |

### 5.3 Identity, audit, observability

| Entity | Purpose | Provenance |
|---|---|---|
| `Tenant` | Customer | `NickERP.Platform.Tenancy` |
| `User` | Human actor | `NickERP.Platform.Identity` (Phase 1 of main roadmap) |
| `LocationAssignment` | User ↔ Location with role | `NickERP.Inspection.Core` |
| `DomainEvent` | Append-only audit record for every state change | Inspection writes, central `audit.events` table |
| `MlTelemetry` | Per-review analyst behavior (zoom patterns, time-to-decision, confidence) | Inspection writes, feeds future ML |

---

## 6. Plugin contracts

Three plugin types. Each lives in an `*.Abstractions` project that the core references. Concrete adapters live in separate projects that only reference `*.Abstractions` + domain DTOs.

### 6.1 `IScannerAdapter`

```csharp
public interface IScannerAdapter
{
    string TypeCode { get; }                      // "FS6000", "ASE"
    string DisplayName { get; }
    ScannerCapabilities Capabilities { get; }
    JsonSchema ConfigSchema { get; }              // drives the admin UI form

    Task<ConnectionTestResult> TestAsync(
        ScannerDeviceConfig cfg, CancellationToken ct);

    IAsyncEnumerable<RawScanArtifact> StreamAsync(
        ScannerDeviceConfig cfg, CancellationToken ct);

    Task<ParsedArtifact> ParseAsync(
        RawScanArtifact raw, CancellationToken ct);
}
```

- `TypeCode` is the stable identifier persisted in `ScannerDeviceInstance.type_code`. Renames require a migration.
- `ConfigSchema` is JSON Schema; the admin UI generates the form from it.
- `StreamAsync` yields raw artifacts as they land (file-watch, DB poll, SDK callback — adapter's concern).
- `ParseAsync` returns a normalized artifact the core can store — no vendor concepts leak through.

### 6.2 `IExternalSystemAdapter`

```csharp
public interface IExternalSystemAdapter
{
    string TypeCode { get; }                      // "ICUMS-GH", "GRA-GH"
    string DisplayName { get; }
    ExternalSystemCapabilities Capabilities { get; }   // poll/push, supported doc types
    JsonSchema ConfigSchema { get; }

    Task<ConnectionTestResult> TestAsync(
        ExternalSystemConfig cfg, CancellationToken ct);

    // Pull side — get authority docs for a case
    Task<IReadOnlyList<AuthorityDocument>> FetchDocumentsAsync(
        ExternalSystemConfig cfg, CaseLookupCriteria lookup, CancellationToken ct);

    // Push side — submit a verdict back
    Task<SubmissionResult> SubmitAsync(
        ExternalSystemConfig cfg, OutboundSubmissionRequest req, CancellationToken ct);
}
```

- **Idempotency is mandatory.** Every `SubmitAsync` call carries an `idempotency_key`; adapter must guarantee at-most-once semantics per key even across retries.
- Pull can be polled (by the core on a schedule) or pushed (adapter exposes a webhook endpoint, core consumes). Capability flag decides.

### 6.3 `IAuthorityRulesProvider`

```csharp
public interface IAuthorityRulesProvider
{
    string AuthorityCode { get; }                 // "GH-CUSTOMS"
    Task<ValidationResult> ValidateAsync(
        InspectionCase @case, CancellationToken ct);
    Task<InferenceResult> InferAsync(
        InspectionCase @case, CancellationToken ct);
}
```

- `ValidateAsync` is where `port-match`, `Fyco import/export`, `regime validation` logic from v1 lives — ported into `Authorities.CustomsGh`.
- `InferAsync` is where the CMR→IM upgrade lives — an authority-specific rule that mutates case state before review.
- Every Case has exactly one `AuthorityRulesProvider` assigned (derived from its `External System Instance`'s declared authority).

### 6.4 Plugin manifest & discovery

Each adapter assembly ships a `plugin.json` sidecar:

```json
{
  "pluginType": "scannerAdapter",
  "typeCode": "FS6000",
  "displayName": "Smiths Heimann FS6000",
  "assembly": "NickERP.Inspection.Scanners.FS6000.dll",
  "adapterClass": "NickERP.Inspection.Scanners.FS6000.Fs6000Adapter",
  "version": "1.0.0",
  "configSchema": { "$schema": "http://json-schema.org/draft-07/schema#", "…": "…" }
}
```

Discovery at startup: scan a configured plugins directory, load assemblies, register concrete adapters into DI keyed by `TypeCode`. Hot-reload is **not** a goal for v1.

---

## 7. Cross-cutting concerns — wired from line 1

Each of these was flagged in the architecture review as "painful to retrofit later." None of them are optional in v2.

### 7.1 Tenant + Location isolation (Postgres RLS)

Every domain table has `tenant_id uuid NOT NULL` and, where applicable, `location_id uuid`. RLS policies:

```sql
-- Run on every table with tenant_id:
ALTER TABLE inspection.cases ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON inspection.cases
    USING (tenant_id = current_setting('app.current_tenant_id', true)::uuid);
CREATE POLICY location_isolation ON inspection.cases
    USING (location_id = ANY(string_to_array(
            current_setting('app.current_location_ids', true), ',')::uuid[]));
```

- API middleware sets both session variables per request based on the authenticated user.
- **Tenant admins** get all `location_ids` for their tenant.
- A bug in app code cannot leak cross-tenant or cross-location data — RLS is the backstop.
- Separate Postgres role for the app (`nickerp_inspection_app`) — does not own schemas, no BYPASS RLS.

#### The `identity.identity_users` carve-out (Sprint H2)

`identity.identity_users` is the **only** tenanted table NOT under `FORCE ROW LEVEL SECURITY`. Every other tenanted table in `nickerp_platform` (`app_scopes`, `user_scopes`, `service_token_identities`, `service_token_scopes`) and every domain table in `nickerp_inspection` keeps its `tenant_isolation_*` policy and `FORCE` flag.

The reason is that `identity_users` is the table that *establishes* tenant context. The auth flow (`DbIdentityResolver.FindUserByEmailAsync`) has to read it to discover the calling principal's `TenantId` *before* the tenancy middleware can push `app.tenant_id` to Postgres. RLS on this table is fundamentally circular: under the production posture (`nscim_app`, NOBYPASSRLS), `app.tenant_id` is unset at auth-resolution time, the `COALESCE` falls through to `'0'`, and every row is filtered out — auth returns 401, the demo dies. Sprint H2's migration `20260428104421_RemoveRlsFromIdentityUsers` lifts FORCE RLS and drops the policy on this one table.

What still protects tenant data: the `users` row carries a `TenantId` column, but a code path that reads it directly cannot reach tenant data through it — every join into another tenanted table hits *that* table's RLS. The user-discovery hop is the single intentional carve-out; defense-in-depth holds for the data behind it. A startup guard (`IdentityUsersRlsGuard.EnsureCarveOutAsync`) wired into the host bootstrap logs a structured `IDENTITY-USERS-RLS-RE-ENABLED` warning if a future migration silently re-enables FORCE RLS on this table.

### 7.2 Event-driven state — offline-ready from day 1

Every domain state change emits a `DomainEvent`:

- Append-only `audit.events` (shared NickERP table — see main roadmap Phase 4).
- Fields: `event_id`, `tenant_id`, `location_id`, `actor_user_id`, `correlation_id`, `event_type`, `entity_type`, `entity_id`, `payload jsonb`, `occurred_at`, `ingested_at`, `idempotency_key`, `prev_event_hash` (optional tamper-evident chain).
- `REVOKE UPDATE, DELETE` on the app role — append-only at the DB level.
- Every outbound submission, verdict flip, review assignment — all go through the event log.
- **The future edge node replays its local event log on reconnect.** Idempotency keys dedupe. This is why events are the source of truth, not just a side-audit.

### 7.3 Plugin boundary discipline

- Adapters **never see domain entities**. They see DTOs on the boundary (`ParsedArtifact`, `AuthorityDocument`, `OutboundSubmissionRequest`).
- Core **never references adapter assemblies** at compile time. DI keys by `TypeCode` at runtime.
- One plugin = one NuGet project = one test assembly. Isolated CI.

### 7.4 Immutable audit log

Same `audit.events` table from 7.2 is the compliance audit log. The chain is:

1. Command arrives (`"assign review"`).
2. Application service validates, produces a `DomainEvent`.
3. Event is persisted in the same transaction as the state change (outbox pattern — read-your-writes).
4. Post-commit projector updates read models.

Querying "who accessed case X at time Y" is a single scan of `audit.events` with a composite index on `(entity_id, occurred_at)`.

### 7.5 ML telemetry schema — captured from day 1

On every `AnalystReview`, capture:

| Field | Why |
|---|---|
| `time_to_decision_ms` | Training signal for triage priority |
| `roi_interactions jsonb` (array of `{x,y,w,h,zoom,dwell_ms}`) | Which regions analysts care about — future auto-ROI suggestion |
| `confidence_score` (0.0–1.0, **required**) | Calibrates analyst reliability over time |
| `verdict_changes jsonb` (array of prior verdict attempts) | Analyst uncertainty signal |
| `peer_disagreement_count` (when dual-review enabled) | Ground-truth-ish signal |
| `post_hoc_outcome jsonb` (when customs seizure/clearance data comes back) | True label for supervised learning |

Store as-you-go. Don't worry about ML consumers yet — the schema is cheap, the data is irreplaceable.

### 7.6 Migration readiness

v1 → v2 cutover plumbing is a first-class concern, not a last-mile scramble:

- v2 accepts **dual-reporting scanners** from Phase 7.1 (scanner sends to both systems during parallel run). `idempotency_key` per scan guarantees no double-processing.
- Outbound submissions carry a `cutover_register` flag: "this case was already submitted from v1 — v2 must NOT resubmit."
- `MIGRATION-FROM-V1.md` grows one section per parallel-run concern as it's discovered.

### 7.7 Image pipeline — pre-rendering from line 1

**No base64 image marshalling, ever.** v1's worst performance failure was reading 2–30 MB scan files at request time and base64-encoding them through Blazor SignalR. v2 must not repeat this. The image pipeline is a **first-class** cross-cutting concern, baked in from Phase 7.1 — not deferred to a later sub-phase.

The vendor-neutral pre-rendering pipeline lives inside `NickERP.Inspection.Application.Imaging` (or a sibling project) and serves every scanner adapter:

- **On scan ingest** (any `IScannerAdapter` produces a `RawScanArtifact`): enqueue a pre-render job carrying the artifact's `SourceHash` as idempotency key.
- **Pre-render workers** (background services) consume the queue, call `IImageRenderer.RenderThumbnailAsync` (256 px) and `RenderPreviewAsync` (1024 px) on each artifact, store outputs.
- **Storage routing**: thumbnails (< 512 KB) → Redis via `ICacheService`; previews → disk blob cache at `<storage_root>/prerender/{yyyy}/{MM}/{dd}/{scanId}/{kind}_{size}.{ext}` with LRU eviction.
- **Serving**: `GET /api/images/{scanId}/{kind}` streams the cached bytes with `ETag: "<sourceHash[0..15]>"` + `Cache-Control: public, max-age=86400, s-maxage=604800, immutable`. Range requests supported. **No `[Cached]` attribute on these routes** — it serializes streams to JSON in cache and corrupts images.
- **Library**: ImageSharp (managed; TIFF first-class; matches v1 deploy story). Rejected: SkiaSharp (native libs), Magick.NET (slow + license).
- **Queue**: SQL-backed durable queue + in-memory `Channel<long>` for fast wake-up. Atomic dequeue via `UPDATE…OUTPUT…WITH (READPAST, UPDLOCK, ROWLOCK)`. Lease-based for multi-instance safety.
- **Predictive prefetch**: when an analyst opens a Case, hint the queue to prioritise that case's previews. SignalR `AssetReady` push updates the UI live as renders land.

**Rationale for "from line 1, not B.1.2":** at production scale (analysts cite 2000 images/day per Location as the working assumption), base64-per-request is unworkable. Building Phase 7.1 with synchronous image fetch and "we'll add pre-rendering in 7.2" creates two acceptance moments, breaks Phase 7.1's perf metrics, and risks Phase 7.2 silently sliding. Cleaner: ship 7.1 with the pre-rendering pipeline already serving every image.

**Reference design**: the v1 pre-rendering plan at `C:\Users\Administrator\.claude\plans\glimmering-sparking-valiant.md` is the source spec for queue mechanics, retry, lease, idempotency, observability, and rollout patterns. v2 ports the *design*, not the v1 controllers — the implementation lives in `NickERP.Inspection.*` against the new domain.

**Acceptance bar (rolls into Phase 7.1)**: thumbs ≤ 50 ms p95 from Redis, previews ≤ 80 ms p95 from disk; cache hit rate ≥ 85% after 1 h warm operation; ingestion throughput unaffected (±5% per scanner).

#### 7.7.1 Cross-tenant blob collision posture (FU-3)

**The setup.** `DiskImageStore` is content-addressed by SHA-256: every source blob lands at `{StorageRoot}/source/{hash[0..2]}/{hash}.{ext}`, and `SaveSourceAsync` is idempotent — if the file already exists, the existing path is returned. The path is **not** tenant-scoped. By contrast, the `ScanArtifact` row that points at the blob *is* tenant-scoped (`ITenantOwned`, RLS-enforced via `TenantConnectionInterceptor`).

**The theoretical hazard.** If two tenants ever produced byte-identical source bytes, their two `ScanArtifact` rows would carry the same `ContentHash` and resolve to the same on-disk file. `SourceJanitorWorker` runs per-tenant under RLS — for each active tenant it asks "are any *of my own* rows still referencing this hash from a non-Closed/Cancelled case?" — so one tenant moving their case to `Closed` past retention could let the worker delete a blob that another tenant's open case still references. The deletion is silent: the surviving tenant's `ScanArtifact` row stays, but its `StorageUri` no longer points at bytes.

**Why this is astronomically unlikely.**

- **Collision-resistance.** SHA-256 has a 256-bit output. The birthday-bound for an *accidental* collision is ~2^128 distinct inputs — orders of magnitude beyond the bytes the platform will ever process. A *deliberate* collision against SHA-256 has no published preimage attack.
- **Scan-byte uniqueness in practice.** Real scanner output is densely keyed by per-scan metadata baked into the file: timestamp at sub-second resolution, scanner serial number, frame counters, and (for FS6000) firmware-stamped session IDs. Two tenants producing the *exact same bytes* would need to share a scanner instance at the same instant — which the federation-by-location model already forbids (scanners bind to one location, locations bind to one tenant).
- **Adversarial path is dead.** Even if a tenant could craft a colliding input, they would need to know another tenant's hash to target it. The `ContentHash` column lives in the tenant-scoped `inspection.scan_artifacts` table; RLS hides foreign rows. The attacker has no read path into the foreign hash space, so cannot pick a target to collide against.

**Defence-in-depth posture (today).**

- `ScanArtifact` rows are tenant-scoped (`ITenantOwned`) and RLS-hidden across tenants. Confidentiality is preserved at the *row* layer regardless of blob sharing.
- The shared resource is **storage only** — bytes on disk. An attacker cannot enumerate blob paths because `{hash[0..2]}/{hash}.{ext}` requires reading rows they cannot see; the filesystem itself sits behind the service account.
- The worst-case impact of an accidental collision is therefore an **availability** issue (eviction races a delete out from under a sibling tenant), not a confidentiality breach. The blob can be reconstructed from the original source path on the scanner side; analyst review still has rendered derivatives in `{StorageRoot}/render/`, which are keyed by `ScanArtifactId` and are *not* shared across tenants.

**Race-eviction note.** `SourceJanitorWorker.SweepTenantAsync` reduces candidates by content hash and only evicts when no row in the *current tenant's* RLS-narrowed view still references the hash from a non-Closed/Cancelled case (see `SourceJanitorWorker.cs` lines 178–219). The check is correct *within* a tenant; it is not cross-tenant. A genuine cross-tenant guard would require either:

1. A `BYPASSRLS`-equivalent existence query (Sprint-5's `ITenantContext.SetSystemContext()` mechanism), **plus**
2. An opt-in clause on the `inspection.scan_artifacts` RLS policy that admits the system context — without weakening the per-tenant default.

Both are out of scope for FU-3. The hook is declared as `ImagingOptions.EnforceCrossTenantBlobGuard` (default `false`); when enforcement lands in a future sprint, flipping the flag to `true` will make the worker refuse to evict any blob whose hash appears in another tenant's rows. Until then the per-tenant check is the documented, deliberate behaviour, with the analysis above as justification.

### 7.8 Observability

- **Logs:** Serilog → Seq, same sink as main roadmap Track A.1.
- **Metrics:** `System.Diagnostics.Metrics` counters/histograms per domain event type, scraped to Prometheus (or whatever Track A.1 settles on). Pre-rendering metrics: queue depth, render latency histogram, cache-hit rate by tier.
- **Traces:** OpenTelemetry from day 1. Trace every command from API → domain → adapter call. Ties into main roadmap Track A.1.

### 7.9 Team topology

Plugin isolation enables this split:

- **Core team** owns `Core`, `Application`, `Infrastructure`, `Api`, `Web`, and the `Abstractions` projects.
- **Scanner work** (new vendor, new mode) happens inside `Scanners.<Vendor>` projects. One engineer can own a scanner end-to-end.
- **Country/authority work** happens inside `Authorities.<Country>` — parallel tracks without contention.

Repo is one monorepo; builds are per-project; release cadence can differ.

---

## 8. Build phases

Each phase has a crisp acceptance bar. Do not declare done until it's met.

### Phase 7.0 — Skeleton (target: 2–3 weeks)

Goal: a runnable empty app that proves the shape.

- [ ] Scaffold all projects (see section 9 for layout). Target .NET 10.
- [ ] Wire `NickERP.Platform.Identity` (or a temporary stub if Phase 1 of main roadmap hasn't landed) and `NickERP.Platform.Tenancy`.
- [ ] Postgres schema `inspection` + RLS bootstrap.
- [ ] Event log plumbing (write path + projector host) against `audit.events`.
- [ ] Plugin loader with manifest discovery.
- [ ] Admin UI (Blazor Server) with empty CRUD for Tenant, Region, Location, Station, User, ScannerDeviceInstance, ExternalSystemInstance, ExternalSystemBinding.
- [ ] CI: build + test + schema-drift check.
- [ ] Seq + OpenTelemetry wiring.

**Acceptance:** an admin can create a tenant, a location, a station, and register a mock scanner plugin. Cross-tenant access returns zero rows (RLS verified by test).

### Phase 7.1 — Single-location single-scanner happy path (target: 5–7 weeks)

Goal: Tema, one FS6000, full case lifecycle end-to-end **with the production-grade image pipeline live from day 1.**

- [ ] `NickERP.Inspection.Scanners.FS6000` adapter — port decoder from v1 `Services.FS6000`, dress behind `IScannerAdapter`.
- [ ] `NickERP.Inspection.ExternalSystems.IcumsGh` adapter — port ICUMS ingestion + outbox from v1 `Services.Icums`, dress behind `IExternalSystemAdapter`.
- [ ] `NickERP.Inspection.Authorities.CustomsGh` — port port-match, Fyco, regime, CMR→IM rules behind `IAuthorityRulesProvider`.
- [ ] Core domain: Case → Scan → Artifact → Workflow → Review → Finding → Verdict → Submission.
- [ ] Analyst UI (one case at a time — viewer ported from v1: W/L, pixel-probe, ROI inspector).
- [ ] **Image pre-rendering pipeline** (per §7.7) — `IImageRenderer` (ImageSharp), pre-render workers consuming a SQL+Channel queue, Redis-or-disk routing, ETag/Cache-Control streaming endpoints, predictive prefetch on case-open, SignalR `AssetReady` push. **No base64 image marshalling anywhere in the path.**
- [ ] ML telemetry capture.
- [ ] Outbound submission with idempotency.

**Acceptance:** a scan at Tema lands, case materializes, authority docs pull, analyst reviews, verdict submits, event log has the full chain. RLS holds. Telemetry rows exist. **Image-pipeline acceptance**: thumbs ≤ 50 ms p95 from Redis, previews ≤ 80 ms p95 from disk; cache hit rate ≥ 85% after 1 h warm; ingestion throughput unaffected (±5%).

### Phase 7.2 — Multi-scanner same location (target: 2–3 weeks)

- [ ] `NickERP.Inspection.Scanners.ASE` adapter (ported tri-panel/composite rendering).
- [ ] Multiple Stations at Tema (Lane 1 FS6000, Lane 2 ASE).
- [ ] Lane routing — a scan knows its Station by scanner config, not by IP guessing.
- [ ] ASE adapter exercises the pre-rendering pipeline shipped in 7.1 — no new pipeline work, but verify the second scanner's artifact formats render cleanly.

**Acceptance:** two scanners running at Tema feed the same analyst queue; image list loads < 50 ms warm; ASE-specific formats (composite, IR) render correctly through the existing pipeline.

### Phase 7.3 — Multi-location (target: 3–4 weeks)

- [ ] Onboarding flow for second Location (Kotoka Cargo).
- [ ] Per-Location user assignments.
- [ ] Per-Location ExternalSystemInstance OR shared-national binding (both paths exercised — Tema gets its own ICUMS endpoint, Kotoka binds to a shared one).
- [ ] Tenant-admin rollup dashboard.

**Acceptance:** two locations live simultaneously, analysts at each see only their location's work, tenant admin sees both, zero cross-location data leakage verified by penetration test.

### Phase 7.4 — External system breadth (target: 2–3 weeks)

- [ ] Second `ExternalSystemType` (e.g., a second authority or a cross-border customs — whoever is next on the commercial list).
- [ ] Polymorphic `AuthorityDocument` payloads.
- [ ] Submission retry + circuit-breaker patterns hardened.

**Acceptance:** a Case can carry documents from two different external systems; the correct Rules Provider validates each.

### Phase 7.5 — Migration tooling + parallel run (target: 3–4 weeks, then however long parallel-run lasts)

- [ ] v1 → v2 data migration scripts (reference data first, then in-flight cases as they close in v1).
- [ ] Dual-report mode for scanners — both v1 and v2 receive, dedupe on idempotency key.
- [ ] Cutover register — which cases already went to ICUMS from v1.
- [ ] Deep-link redirect layer — v1 URLs forward to v2 where possible.

**Acceptance:** one Location runs on v2 full-time, other Locations still on v1, all external submissions correctly attributed, zero duplicates.

### Phase 7.6 — Edge node (post-cutover, target: 4–6 weeks)

- [ ] Lightweight local node (`NickERP.Inspection.Edge`) per Location — SQLite-backed event log, scanner adapter co-located.
- [ ] Sync protocol — event replay on reconnect, conflict resolution via idempotency.
- [ ] Offline analyst UI (PWA or thin native).
- [ ] Degraded-mode UX when central is unreachable.

**Acceptance:** unplug Tema's WAN for 2 hours, scans continue, reviews continue (on cached authority docs), reconnect → full sync within 5 minutes.

---

## 9. Repo layout (created in Phase 7.0)

```
inspection-v2/
├── README.md
├── docs/
│   ├── ARCHITECTURE.md
│   ├── MIGRATION-FROM-V1.md
│   └── PLUGIN-AUTHORING.md                           (Phase 7.0)
├── src/
│   ├── NickERP.Inspection.Core/                      domain entities + events
│   ├── NickERP.Inspection.Application/               use cases / command handlers
│   ├── NickERP.Inspection.Infrastructure/            EF Core, Postgres, RLS
│   ├── NickERP.Inspection.Api/                       REST + SignalR
│   ├── NickERP.Inspection.Web/                       Blazor Server (analyst + admin UI)
│   ├── NickERP.Inspection.Scanners.Abstractions/     IScannerAdapter + DTOs
│   ├── NickERP.Inspection.Scanners.FS6000/           (Phase 7.1)
│   ├── NickERP.Inspection.Scanners.ASE/              (Phase 7.2)
│   ├── NickERP.Inspection.ExternalSystems.Abstractions/
│   ├── NickERP.Inspection.ExternalSystems.IcumsGh/   (Phase 7.1)
│   ├── NickERP.Inspection.Authorities.Abstractions/
│   ├── NickERP.Inspection.Authorities.CustomsGh/     (Phase 7.1)
│   └── NickERP.Inspection.Edge/                      (Phase 7.6, later)
├── tests/
│   ├── NickERP.Inspection.Core.Tests/
│   ├── NickERP.Inspection.Application.Tests/
│   ├── NickERP.Inspection.Scanners.FS6000.Tests/
│   ├── NickERP.Inspection.ExternalSystems.IcumsGh.Tests/
│   ├── NickERP.Inspection.Authorities.CustomsGh.Tests/
│   └── NickERP.Inspection.Integration.Tests/
├── plugins/                                          runtime plugin drop folder
│   └── README.md
└── NickERP.Inspection.sln
```

---

## 10. What we borrow from v1 (port list)

Point-in-time copies into v2 new files with rename. **No shared references.**

| From v1 | To v2 | Rename / reshape |
|---|---|---|
| `Services.FS6000/ImageProcessingService.cs` decoder | `Scanners.FS6000/Fs6000Decoder.cs` | Strip controller deps; return `ParsedArtifact` DTO |
| `Services.FS6000/IngestionService.cs` file-watch logic | `Scanners.FS6000/Fs6000FileWatcher.cs` | Rewrap as `IAsyncEnumerable<RawScanArtifact>` |
| `Services/ASE/AseCompositeRenderer` | `Scanners.ASE/AseCompositeRenderer` | Vendor-neutral DTO in/out |
| `Services.ImageProcessing/ImageSharpRenderer` + W/L math | `Web/Components/Viewer/` + server render helpers | Keep the math; drop v1 controller coupling |
| `Services.ImageProcessing/AdvancedImageProcessingService` | Reuse parts in `Scanners.Abstractions/Rendering` | Extract vendor-free pieces only |
| `Services.Icums.IcumJsonIngestionService` (esp. CMR→IM upgrade, line 600+) | Split: **core ingestion** → `ExternalSystems.IcumsGh.Ingestor`; **the CMR→IM rule** → `Authorities.CustomsGh.InferAsync` | Separating the two is the whole point |
| Port-match, Fyco, regime validation rules | `Authorities.CustomsGh.ValidateAsync` | Keep logic, drop v1 scaffolding |
| `ContainerCompletenessService` | `CaseCompletenessService` | Renamed; rules move to `Authorities.CustomsGh` |
| Client-side 16-bit viewer JS | `Web/wwwroot/viewer/` | As-is; self-contained |
| ROI inspector Blazor component | `Web/Components/Viewer/RoiInspector.razor` | As-is |
| EF migrations (structure only) | **Read as reference, do not replay** | Write fresh migrations for new schema names |

## 11. What we explicitly leave behind

- `MasterOrchestrator` scaffold (dead in v1, ~30% ready, unused).
- `[AllowAnonymous]` endpoint shortcuts.
- 14-background-services sprawl — v2 starts with the 3-orchestrator pattern (Phase 2.1–2.3 of main roadmap) from line 1.
- Base64 image marshalling.
- `ImageCacheService` storing blobs in SQL.
- `/api/auth/login` password-based path — v2 trusts `CF-Access-Jwt-Assertion` exclusively.
- ICUMS/vendor vocabulary in controllers, entities, configs.
- Flat single-location assumptions.
- The `ImageCaches` table.
- In-process scanner DLL coupling without an adapter interface.

---

## 12. Open questions — deferred, not forgotten

| # | Question | When to settle |
|---|---|---|
| Q1 | **Conflict resolution on edge-node sync** — when the same case is touched offline at the edge and online at central, who wins? Last-writer? Field-level merge? Designed before Phase 7.6 starts. | Phase 7.5 |
| Q2 | **Station assignment dynamics** — is Device-to-Station a permanent binding or can it rotate mid-day? Affects historical queries ("what scanned this case"). | Phase 7.2 |
| Q3 | **Dual-review enforcement** — do high-value cases require two analysts? Orthogonal feature; schema supports it, UI work deferred. | Phase 7.3+ |
| Q4 | **Post-hoc outcome capture** — how does customs seizure/clearance feedback flow back into v2 for ML labels? Probably a new adapter or a manual entry tool. | Phase 7.4 |
| Q5 | **Rate limiting / throttling per ExternalSystemInstance** — some authorities will have strict QPS. Where does the token bucket live? | Phase 7.1 (before first real external call) |
| Q6 | **Data residency** — if a future tenant is in another country, does their data need to live in a separate cluster? | Before 2nd tenant |
| Q7 | **Operator identity at the scanner** — does the FS6000 know who is operating it, or does v2 have to infer from Station assignment + time? | Phase 7.1 |

---

## 13. Related documents

- **This repo (`C:\Shared\ERP V2\`):**
  - [`../README.md`](../README.md) — entry point.
  - [`MIGRATION-FROM-V1.md`](MIGRATION-FROM-V1.md) — cutover plan.
  - [`runbooks/README.md`](runbooks/README.md) — named-incident operations runbooks (Sprint 7 / P1).
- **v1 repo (`C:\Shared\NSCIM_PRODUCTION\`) — sibling, not parent:**
  - `C:\Shared\NSCIM_PRODUCTION\ROADMAP.md` — Phase 7 entry points back here.
  - `C:\Shared\NSCIM_PRODUCTION\PLATFORM.md` — shared platform contracts (what we consume).
  - `C:\Shared\NSCIM_PRODUCTION\CHANGELOG.md` — v1 history; provenance for ported logic.
- **External plan files:**
  - `C:\Users\Administrator\.claude\plans\glimmering-sparking-valiant.md` — currently the relocate-and-init plan for this repo. Earlier in conversation history it was the pre-rendering service design (Phase 2.5 in v1 ROADMAP); that content remains the design of record for the v1 pre-rendering work.
