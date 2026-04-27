# NickERP v2 — testing the build

This is the single source of truth for **how to run what's been built so far** and **what to look at when giving feedback.**

The build is intentionally a skeleton — the platform layers + first two consumers (Portal v2, Inspection v2 admin). It validates that every architectural decision works end-to-end. Scanner adapters, the analyst review UI, the audit search UI, NickFinance, and the rest of Track B are next.

---

## What you can test today

| App | URL | Status |
|---|---|---|
| **Seq** (centralized logs + traces) | http://localhost:5341 | live, admin-only |
| **NickERP.Portal v2** | http://localhost:5400 | live, click-through |
| **NickERP.Inspection v2 admin** | http://localhost:5410 | live, click-through |

Plus the underlying infrastructure: Postgres `nickerp_platform` (3 schemas — `identity`, `tenancy`, `audit`) and `nickerp_inspection` (1 schema — `inspection`).

---

## One-time setup (already done on TEST-SERVER)

You don't need to run any of this — it's already provisioned. Listed here so you know what exists.

```powershell
# Seq (logs + OTLP traces backend)
choco install seq -y                                    # installed 2026-04-25
# Admin password is set in C:\ProgramData\Seq\Seq.json

# Postgres databases (cluster already running on :5432)
psql -U postgres -c "CREATE DATABASE nickerp_platform;"   # done
psql -U postgres -c "CREATE DATABASE nickerp_inspection;" # done
```

Migrations applied:
- `nickerp_platform`: `identity` schema (5 tables), `tenancy` schema (1 table), `audit` schema (1 table)
- `nickerp_inspection`: `inspection` schema (16 tables — case lifecycle + scan-render derivatives)

Bootstrap row:
- `identity.identity_users` has `dev@nickscan.com` (display name "Local dev") with the `Identity.Admin` scope. Used for the dev-bypass authentication.
- `tenancy.tenants` has tenant id 1 (`nick-tc-scan`, "Nick TC-Scan Operations").

---

## Running the apps

Phase F5 — the app hosts now connect as the non-superuser `nscim_app` role
(`LOGIN NOSUPERUSER NOBYPASSRLS`). This is what makes the F1 RLS policies
actually enforce; running as `postgres` bypasses RLS silently. Set the
role password once after applying the F5 migrations:

```bash
export NICKSCAN_DB_PASSWORD="<the rotated postgres superuser password>"
export NICKERP_APP_DB_PASSWORD="$NICKSCAN_DB_PASSWORD"   # dev — same value
./tools/migrations/phase-f5/set-nscim-app-password.sh
```

In prod, `NICKERP_APP_DB_PASSWORD` is a separate secret rotated independently
of the superuser password.

Both apps need two env vars set in your shell:

```bash
export NICKSCAN_DB_PASSWORD="<the rotated postgres password>"
export ConnectionStrings__Platform="Host=localhost;Port=5432;Database=nickerp_platform;Username=nscim_app;Password=$NICKERP_APP_DB_PASSWORD"
```

Plus, for Inspection only:

```bash
export ConnectionStrings__Inspection="Host=localhost;Port=5432;Database=nickerp_inspection;Username=nscim_app;Password=$NICKERP_APP_DB_PASSWORD"
```

Migrations themselves still run as `postgres` (they need superuser to
create roles + grants). Use `dotnet ef database update` with
`NICKERP_INSPECTION_DB_CONNECTION` / `NICKERP_PLATFORM_DB_CONNECTION`
pointed at `Username=postgres`. The host's `RunMigrationsOnStartup` flag
defaults to `true` in dev so you don't need to do this manually after
the first time.

### Portal v2

```bash
cd "/c/Shared/ERP V2/apps/portal"
dotnet run
# → http://localhost:5400
```

### Inspection v2 admin

The Inspection admin loads plugins from `modules/inspection/src/NickERP.Inspection.Web/plugins/`. Stage the mock plugins there before first run:

```bash
cd "/c/Shared/ERP V2"
dotnet build modules/inspection/plugins/NickERP.Inspection.Scanners.Mock
dotnet build modules/inspection/plugins/NickERP.Inspection.ExternalSystems.Mock
dotnet build modules/inspection/plugins/NickERP.Inspection.Scanners.FS6000
dotnet build modules/inspection/plugins/NickERP.Inspection.ExternalSystems.IcumsGh
dotnet build modules/inspection/plugins/NickERP.Inspection.Authorities.CustomsGh

DEST="modules/inspection/src/NickERP.Inspection.Web/plugins"
mkdir -p "$DEST"
cp modules/inspection/plugins/NickERP.Inspection.Scanners.Mock/bin/Debug/net10.0/NickERP.Inspection.Scanners.Mock.dll "$DEST/"
cp modules/inspection/plugins/NickERP.Inspection.Scanners.Mock/bin/Debug/net10.0/plugin.json "$DEST/NickERP.Inspection.Scanners.Mock.plugin.json"
cp modules/inspection/plugins/NickERP.Inspection.ExternalSystems.Mock/bin/Debug/net10.0/NickERP.Inspection.ExternalSystems.Mock.dll "$DEST/"
cp modules/inspection/plugins/NickERP.Inspection.ExternalSystems.Mock/bin/Debug/net10.0/plugin.json "$DEST/NickERP.Inspection.ExternalSystems.Mock.plugin.json"
cp modules/inspection/plugins/NickERP.Inspection.Scanners.FS6000/bin/Debug/net10.0/NickERP.Inspection.Scanners.FS6000.dll "$DEST/"
cp modules/inspection/plugins/NickERP.Inspection.Scanners.FS6000/bin/Debug/net10.0/plugin.json "$DEST/NickERP.Inspection.Scanners.FS6000.plugin.json"
cp modules/inspection/plugins/NickERP.Inspection.Scanners.FS6000/bin/Debug/net10.0/SixLabors.ImageSharp.dll "$DEST/"
cp modules/inspection/plugins/NickERP.Inspection.ExternalSystems.IcumsGh/bin/Debug/net10.0/NickERP.Inspection.ExternalSystems.IcumsGh.dll "$DEST/"
cp modules/inspection/plugins/NickERP.Inspection.ExternalSystems.IcumsGh/bin/Debug/net10.0/plugin.json "$DEST/NickERP.Inspection.ExternalSystems.IcumsGh.plugin.json"
cp modules/inspection/plugins/NickERP.Inspection.Authorities.CustomsGh/bin/Debug/net10.0/NickERP.Inspection.Authorities.CustomsGh.dll "$DEST/"
cp modules/inspection/plugins/NickERP.Inspection.Authorities.CustomsGh/bin/Debug/net10.0/plugin.json "$DEST/NickERP.Inspection.Authorities.CustomsGh.plugin.json"
cp modules/inspection/plugins/NickERP.Inspection.Authorities.CustomsGh/bin/Debug/net10.0/NickERP.Inspection.Authorities.Abstractions.dll "$DEST/"
cp modules/inspection/plugins/NickERP.Inspection.Scanners.Mock/bin/Debug/net10.0/NickERP.Inspection.Scanners.Abstractions.dll "$DEST/"
cp modules/inspection/plugins/NickERP.Inspection.ExternalSystems.Mock/bin/Debug/net10.0/NickERP.Inspection.ExternalSystems.Abstractions.dll "$DEST/"
cp modules/inspection/plugins/NickERP.Inspection.Scanners.Mock/bin/Debug/net10.0/NickERP.Platform.Plugins.dll "$DEST/"

cd modules/inspection/src/NickERP.Inspection.Web
dotnet run
# → http://localhost:5410
```

---

## What to click through

### Portal v2 (http://localhost:5400)

1. **Land on home.** You should see "Welcome back, Local dev". Four stat tiles: Tenants / Users / App scopes / Audit events. Two grids: **v2 modules** (Inspection v2, plus disabled placeholders for Petty Cash / HR v2 / Comms v2) and **Platform tools** (Tenants, Audit log, Health, Seq).
2. **Click stat tiles + module cards** — they navigate inside v2 (or to Seq). The v2 chrome **never links to v1 apps** (`hr.nickscan.net`, `scan.nickscan.net`); v1 stays untouched per the v1/v2 separation rule.
3. **App switcher** in the TopNav (top-right grid icon) — should show only Portal + Inspection v2 today; more land as Track B modules ship.
3. **/tenants** — list of tenants. Try adding one (Code: `customer-2`, Name: `Test customer`, defaults for the rest). The new row shows up. Behind the scenes, a `nickerp.tenancy.tenant_created` `DomainEvent` is emitted.
4. **/audit** — verify the event you just emitted shows up at the top of the list. Filter by event-type prefix (`nickerp.tenancy`) to narrow.
5. **/health** — should show 4 OK pills (identity, tenancy, audit DB schemas + Seq).

### Inspection v2 admin (http://localhost:5410)

1. **/** (overview) — five stat tiles. Plugins should already show **5 loaded** (mock-scanner + mock-external + fs6000 + icums-gh + gh-customs).
2. **/plugins** — confirms all five are registered with their contracts (`IScannerAdapter`, `IExternalSystemAdapter`, `IAuthorityRulesProvider`) detected via reflection. `fs6000` is the real scanner adapter ported from v1's `FS6000FormatDecoder`; `icums-gh` is the real Ghana ICUMS adapter (file-drop intake + file-based outbox); `gh-customs` is the Ghana Customs rule pack (port-match, Fyco, regime, CMR→IM upgrade) ported from v1's ContainerValidationService.
3. **/locations** — add `tema` / `Tema Port` / `Greater Accra` / `Africa/Accra`. Row appears.
4. **/stations** — add `lane-1` / `Lane 1` against the Tema location. Row appears.
5. **/scanners** — pick the `fs6000` plugin from the dropdown, point it at Tema, name it `Test FS6000`. Set `WatchPath` in the config JSON to a directory you have read access to (e.g. `C:\\fs6000\\incoming`). Row appears. (`mock-scanner` is also still available if you want to test the synthetic-stream path.)
6. **/external-systems** — pick `icums-gh`, name it `Test ICUMS`, scope = `Per-location`. Set `BatchDropPath` and `OutboxPath` in the config JSON (e.g. `C:\\icums\\drop` / `C:\\icums\\outbox`). Row appears. (`mock-external` is also still available.)

After clicking through both apps, **open Seq at http://localhost:5341** and filter by `ServiceName = 'NickERP.Portal'` then `ServiceName = 'NickERP.Inspection.Web'` — you should see structured log entries for every page load + DB write, plus OpenTelemetry traces correlating the request → DB span.

### Image pre-rendering pipeline (ARCHITECTURE §7.7)

When you simulate a scan, the FS6000 / mock scanner adapter's bytes are
stashed under `<StorageRoot>/source/{hash[0..2]}/{hash}.png`. A
`PreRenderWorker` background service (every 3s in dev) picks up unrendered
`ScanArtifact` rows and produces:

- 256 px thumbnail at `/api/images/{scanArtifactId}/thumbnail`
- 1024 px preview at `/api/images/{scanArtifactId}/preview`

Both stream with `Cache-Control: public, max-age=86400, s-maxage=604800, immutable`
and an ETag = source hash (first 16 chars). Conditional `If-None-Match` returns 304.

`StorageRoot` defaults to `C:\Shared\ERP V2\.imaging-store` in dev (gitignored).

---

## Demo walkthrough

The end-to-end demo lives in two flavours:

- **Manual.** [`docs/runbooks/demo-walkthrough.md`](docs/runbooks/demo-walkthrough.md) — a step-by-step that an analyst (or a sales engineer) can follow in under five minutes. Drops a real FS6000 triplet into a watch folder; the `ScannerIngestionWorker` picks it up, the `PreRenderWorker` thumbnails it, the analyst fetches an ICUMS BOE, the gh-customs rules fire, the analyst clears the case, the verdict lands in the ICUMS outbox as a JSON file, and ≥8 `nickerp.inspection.*` events show up on `/audit`.
- **Automated.** `tests/NickERP.Inspection.E2E.Tests/FullCaseLifecycleTests.cs` — the same lifecycle as a single xUnit `[Fact]`. Stands up an isolated Postgres pair (per-run unique-suffixed DBs on the dev `localhost:5432`), boots the inspection host via `WebApplicationFactory<Program>`, runs the workflow, and asserts every checkpoint plus an RLS sanity probe with a non-superuser role. Marked `[Trait("Category","Integration")]` so unit-test runs skip it.

Run it:

```bash
# All tests including the e2e (~35s; needs NICKSCAN_DB_PASSWORD).
dotnet test NickERP.Tests.slnx

# Unit-only — fast (<10s on a warm cache), no Postgres dependency.
dotnet test NickERP.Tests.slnx --filter "Category!=Integration"

# Integration-only — runs the e2e in isolation.
dotnet test NickERP.Tests.slnx --filter "Category=Integration"
```

The e2e teardown drops its scratch databases on success. On a hard
crash (Ctrl-C, host kill), leftover DBs and roles are prefixed
`nickerp_e2e_*` so a manual `psql -c "DROP DATABASE ..."` sweep cleans
them up.

---

## What's deliberately not finished yet

- **Inspection case lifecycle** — ✅ shipped (ROADMAP §4.1). 12 entities, 7 workflow transitions, DomainEvents on every state change.
- **Real scanner / external-system / authority adapters** — ✅ shipped (ROADMAP §4.2). FS6000, IcumsGh, CustomsGh ported point-in-time from v1. Authority rules surface in CaseDetail.
- **Image pre-rendering pipeline** — ✅ skeleton shipped (ROADMAP §4.3.a). `IImageRenderer` (ImageSharp), `IImageStore` (disk), `PreRenderWorker`, `/api/images/{id}/{kind}` endpoint with ETag/Cache-Control. Redis tier + SQL durable queue + SignalR `AssetReady` push come in later slices.
- **Analyst review UI** — viewer ports from v1 (W/L sliders, ROI inspector, pixel probe). Next.
- **Audit search deep-dive** — Portal's `/audit` shows the rows; clicking into one to see the full payload jsonb is a backlog item.
- **Notification bell** — placeholder until the audit-events projection lands.
- **Federated cross-app search** — `TopNav` exposes the slot; the search API depends on Portal v2 maturity.
- **Tenants edit / delete** — only list + create today.
- **NickFinance** (Track B.3) — separate track; petty cash module is the pathfinder.

---

## Repo geography

```
C:\Shared\ERP V2\                                     ← github.com/bjforson/ERP-V2 (private)
├── docs/
│   ├── ARCHITECTURE.md                               ← v2 design of record
│   └── MIGRATION-FROM-V1.md                          ← cutover plan (stub, grows over time)
├── platform/                                         ← Track A
│   ├── NickERP.Platform.Logging/                     ← Serilog + Seq conventions
│   ├── NickERP.Platform.Telemetry/                   ← OpenTelemetry conventions
│   ├── NickERP.Platform.Identity/                    ← canonical user, JWT scheme, dev bypass
│   ├── NickERP.Platform.Identity.Database/           ← IdentityDbContext + DbIdentityResolver
│   ├── NickERP.Platform.Identity.Api/                ← admin REST API + OpenAPI
│   ├── NickERP.Platform.Tenancy/                     ← ITenantOwned + interceptors + middleware
│   ├── NickERP.Platform.Tenancy.Database/            ← TenancyDbContext (tenants table)
│   ├── NickERP.Platform.Plugins/                     ← [Plugin] + manifest + loader + registry
│   ├── NickERP.Platform.Audit/                       ← DomainEvent + IEventPublisher + IdempotencyKey
│   ├── NickERP.Platform.Audit.Database/              ← AuditDbContext + DbEventPublisher + InProcessEventBus
│   ├── NickERP.Platform.Web.Shared/                  ← TopNav + UserMenu + NotificationBell + AppSwitcher + tokens.css
│   └── demos/
│       ├── observability/                            ← end-to-end log+trace+metric demo
│       ├── identity/                                 ← Blazor admin demo for Identity (live since A.2.9)
│       └── plugins/MockEcho/                         ← reference plugin proving the loader round-trips
├── apps/
│   └── portal/                                       ← Portal v2 (this build)
└── modules/
    └── inspection/                                   ← Inspection v2 module
        ├── src/
        │   ├── NickERP.Inspection.Core/              ← domain entities (Location/Station/etc.)
        │   ├── NickERP.Inspection.Scanners.Abstractions/
        │   ├── NickERP.Inspection.ExternalSystems.Abstractions/
        │   ├── NickERP.Inspection.Authorities.Abstractions/
        │   ├── NickERP.Inspection.Database/          ← InspectionDbContext + migrations
        │   └── NickERP.Inspection.Web/               ← admin Blazor app (port 5410)
        └── plugins/
            ├── NickERP.Inspection.Scanners.Mock/     ← mock-scanner reference impl
            ├── NickERP.Inspection.Scanners.FS6000/   ← real FS6000 adapter (decoder ported from v1)
            ├── NickERP.Inspection.ExternalSystems.Mock/ ← mock-external reference impl
            ├── NickERP.Inspection.ExternalSystems.IcumsGh/ ← real ICUMS Ghana adapter (file-drop + outbox)
            └── NickERP.Inspection.Authorities.CustomsGh/ ← Ghana Customs rule pack (port-match, Fyco, regime, CMR→IM)
```

---

## Giving feedback

The fastest path is one of:

- **"Open Seq, click around the apps, then look at the trace for X"** — concrete request-level feedback.
- **"The Tenants page should have an edit button"** — UX feedback.
- **"Inspection should have a Cases page next"** — direction feedback for B.1.1.
- **"This feels heavy / wrong / weird"** — architectural smell, even better.

There's no "right" feedback shape. Whatever's easiest to type after clicking around for ten minutes.
