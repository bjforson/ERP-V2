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
- `nickerp_inspection`: `inspection` schema (5 tables)

Bootstrap row:
- `identity.identity_users` has `dev@nickscan.com` (display name "Local dev") with the `Identity.Admin` scope. Used for the dev-bypass authentication.
- `tenancy.tenants` has tenant id 1 (`nick-tc-scan`, "Nick TC-Scan Operations").

---

## Running the apps

Both apps need two env vars set in your shell:

```bash
export NICKSCAN_DB_PASSWORD="<the rotated postgres password>"
export ConnectionStrings__Platform="Host=localhost;Port=5432;Database=nickerp_platform;Username=postgres;Password=$NICKSCAN_DB_PASSWORD"
```

Plus, for Inspection only:

```bash
export ConnectionStrings__Inspection="Host=localhost;Port=5432;Database=nickerp_inspection;Username=postgres;Password=$NICKSCAN_DB_PASSWORD"
```

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

DEST="modules/inspection/src/NickERP.Inspection.Web/plugins"
mkdir -p "$DEST"
cp modules/inspection/plugins/NickERP.Inspection.Scanners.Mock/bin/Debug/net10.0/NickERP.Inspection.Scanners.Mock.dll "$DEST/"
cp modules/inspection/plugins/NickERP.Inspection.Scanners.Mock/bin/Debug/net10.0/plugin.json "$DEST/NickERP.Inspection.Scanners.Mock.plugin.json"
cp modules/inspection/plugins/NickERP.Inspection.ExternalSystems.Mock/bin/Debug/net10.0/NickERP.Inspection.ExternalSystems.Mock.dll "$DEST/"
cp modules/inspection/plugins/NickERP.Inspection.ExternalSystems.Mock/bin/Debug/net10.0/plugin.json "$DEST/NickERP.Inspection.ExternalSystems.Mock.plugin.json"
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

1. **/** (overview) — five stat tiles. Plugins should already show **2 loaded** (mock-scanner + mock-external).
2. **/plugins** — confirms both mocks are registered with their contracts (`IScannerAdapter`, `IExternalSystemAdapter`) detected via reflection.
3. **/locations** — add `tema` / `Tema Port` / `Greater Accra` / `Africa/Accra`. Row appears.
4. **/stations** — add `lane-1` / `Lane 1` against the Tema location. Row appears.
5. **/scanners** — pick the `mock-scanner` plugin from the dropdown, point it at Tema, name it `Test FS6000`. Row appears.
6. **/external-systems** — pick `mock-external`, name it `Test ICUMS`, scope = `Per-location`. Row appears.

After clicking through both apps, **open Seq at http://localhost:5341** and filter by `ServiceName = 'NickERP.Portal'` then `ServiceName = 'NickERP.Inspection.Web'` — you should see structured log entries for every page load + DB write, plus OpenTelemetry traces correlating the request → DB span.

---

## What's deliberately not finished yet

- **Inspection case lifecycle** — `InspectionCase`, `Scan`, `ScanArtifact`, `Review`, `Verdict`, `OutboundSubmission`. That's the next chunk (B.1.1).
- **Real scanner adapters** — FS6000, ASE etc. The plugin contracts exist; concrete implementations are next.
- **Image pre-rendering pipeline** — designed (per `docs/ARCHITECTURE.md` §7.7); built in B.1.1.
- **Analyst review UI** — viewer ports from v1 (W/L sliders, ROI inspector, pixel probe).
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
            └── NickERP.Inspection.ExternalSystems.Mock/ ← mock-external reference impl
```

---

## Giving feedback

The fastest path is one of:

- **"Open Seq, click around the apps, then look at the trace for X"** — concrete request-level feedback.
- **"The Tenants page should have an edit button"** — UX feedback.
- **"Inspection should have a Cases page next"** — direction feedback for B.1.1.
- **"This feels heavy / wrong / weird"** — architectural smell, even better.

There's no "right" feedback shape. Whatever's easiest to type after clicking around for ten minutes.
