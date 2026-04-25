# Migrating from NSCIM v1 to NickERP.Inspection v2

> **Status:** stub. Grows one section per parallel-run concern as Phase 5 approaches.
> **Partner doc:** [`ARCHITECTURE.md`](ARCHITECTURE.md)

---

## Purpose

This file exists from day 1 so migration is never "the thing we start thinking about at the end." Every architectural decision in v2 that touches a migration concern leaves a note here.

## Principles

1. **Zero invasive changes to v1.** v1 keeps shipping features and fixes on its own cadence. v2 reads v1's outputs (database + ICUMS outbox files + scan files on disk) but never writes to v1.
2. **v2 is the source of truth from the day a Location cuts over** — not earlier, not later. A Location is either fully on v1 or fully on v2. No hybrid per-case.
3. **Parallel run is per-Location**, not per-scanner or per-case. Flip Tema to v2 while Kotoka stays on v1; finish in-flight v1 Tema cases on v1.
4. **Every outbound submission carries an idempotency key** that survives a v1 → v2 replay. External systems never see the same verdict twice.

## Known parallel-run concerns (grown over time)

### Scanner dual-reporting

(To be detailed during Phase 5.)

Scanners during cutover may report scans to both v1 and v2 so we can compare pipelines. Each raw artifact carries a globally-unique source hash; v2's `Scan.idempotency_key` uses it; v1 continues its own path. Neither pipeline is aware of the other.

### External submission de-duplication

(To be detailed during Phase 5.)

Before v2 submits any verdict, it checks the **cutover register** — a table listing `(case_external_reference, external_system_instance, submitted_from_version, submitted_at)`. If v1 already submitted, v2 records locally but does not re-submit.

### In-flight case handoff

(To be detailed during Phase 5.)

At Location cutover moment T:

- Cases with `status ∈ {AwaitingReview, UnderReview}` in v1 at time T → finish in v1. v1 continues to accept analyst actions for these until closed.
- New cases from T+1 → created in v2.
- Cases closed in v1 after T → back-loaded into v2's read-only history table for continuity of tenant-admin reporting.

### UI deep-link forwarding

(To be detailed during Phase 5.)

v1 URLs like `/container/{n}/details` map onto v2 URLs like `/cases/by-subject/{n}`. A small HTTP forwarder at the edge preserves user bookmarks, WhatsApp links, and emailed PDFs for a 90-day window.

### Reference data migration order

(To be detailed during Phase 5.)

Migrate in this order to respect foreign-key constraints:

1. Tenants
2. Users + LocationAssignments (requires Phase 1 of main roadmap — NickERP.Platform.Identity)
3. Locations + Regions
4. ScannerDeviceInstances (after Scanner plugins register their TypeCodes)
5. ExternalSystemInstances + Bindings
6. Historical closed cases (read-only, last 12 months)

---

## What does NOT come over

| v1 artifact | Why not | What replaces it |
|---|---|---|
| `ImageCaches` table blobs | Bad storage model | Disk cache + Redis (per pre-rendering plan) |
| `[AllowAnonymous]` endpoints | Security debt | All v2 routes require auth |
| Scanner-DLL in-process coupling | Blocks the plugin model | `IScannerAdapter` contract |
| `MasterOrchestrator` | Dead scaffold | 3-orchestrator pattern from day 1 |
| v1 user tables (NSCIM.Users, NickHR.Users) | Two-stores problem | `NickERP.Platform.Identity` canonical user |

---

## Cutover checklist (grown during Phase 5)

A living checklist per Location. Filled in as we approach Phase 5.
