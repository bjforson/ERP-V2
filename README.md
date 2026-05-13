# NickERP v2

> **Status:** active .NET 10 modular ERP/inspection platform, not a design-only repository.
> **Repo:** standalone git repo at `C:\Shared\ERP V2\`, independent of v1.
> **Current architecture review:** [`docs/architectural-design-analysis-2026-05-13.md`](docs/architectural-design-analysis-2026-05-13.md)
> **Living design record:** [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
> **Roadmap:** [`ROADMAP.md`](ROADMAP.md)

NickERP v2 is a greenfield ERP platform that currently includes:

- Blazor Server Portal shell for platform administration, tenant/module navigation, and Portal-hosted modules.
- Blazor Server Inspection Web app for inspection-domain workflows, scanner/external-system plugins, imaging, audit, queueing, and edge replay.
- Shared platform libraries for identity, tenancy, audit/events, logging, telemetry, plugins, queueing, email, and web chrome.
- Inspection module packages and plugins under `modules/inspection/`.
- NickFinance v2 petty-cash pathfinder under `modules/nickfinance/`, hosted by Portal when configured.
- Edge Node service under `apps/edge-node/` with a local SQLite outbox and central replay worker.
- A broad test solution, `NickERP.Tests.slnx`, covering platform, inspection, plugins, edge node, and NickFinance.

The older statement that "nothing is built yet" is stale. This repo is in a pathfinder-to-pilot state: a large amount of functionality exists, while some queue/workflow hardening, operator deployment steps, and post-pilot fold-in work remain open.

## Active Services

| Service | Path | Default local URL | Notes |
| --- | --- | --- | --- |
| Portal | `apps/portal/` | `http://localhost:5400` | ERP launcher, tenant/admin pages, health, and Portal-hosted NickFinance when `ConnectionStrings:NickFinance` is configured. |
| Inspection Web | `modules/inspection/src/NickERP.Inspection.Web/` | `http://localhost:5410` | Inspection UI/API host, plugin composition, imaging, queues, health, and `/api/edge/replay`. |
| Edge Node | `apps/edge-node/NickERP.EdgeNode/` | host-selected unless `ASPNETCORE_URLS` is set | Local service exposing `/edge/healthz`; replays to the central server configured in `Server:Url`. |
| NickFinance | `modules/nickfinance/` | Portal-hosted today | The standalone `5420` service slot is reserved, but current v2 NickFinance runs inside Portal. |
| NickHR | `v1-clone/nickhr/` | not a v2-native service | Co-located clone for pilot compatibility; v2-native refactor is post-pilot. |

Deployment scripts reserve `5420` for future standalone NickFinance and `5430` for future standalone NickHR, but the currently deployed first-class services are Portal and Inspection Web.

Health endpoints:

- Portal: `/healthz/live`, `/healthz/ready`
- Inspection Web: `/healthz/live`, `/healthz/ready`, authenticated `/healthz/workers`
- Edge Node: `/edge/healthz`

## Repository Layout

```text
C:\Shared\ERP V2\
|-- apps\
|   |-- portal\                 # Portal Blazor Server host
|   `-- edge-node\              # Edge Node host + README
|-- modules\
|   |-- inspection\             # Inspection domain, web, database, plugins
|   `-- nickfinance\            # v2-native Petty Cash pathfinder
|-- platform\                   # Shared NickERP platform packages
|-- tests\                      # Active test projects in NickERP.Tests.slnx
|-- docs\                       # Architecture, migration, runbooks, audits
|-- tools\                      # Operational and evaluation tooling
|-- v1-clone\                   # Historical/compatibility clones; see below
|-- README.md
|-- ROADMAP.md
`-- NickERP.Tests.slnx
```

## How To Run Locally

Use .NET 10 from this repo root.

```powershell
dotnet build NickERP.Tests.slnx
dotnet run --project apps/portal/NickERP.Portal.csproj
dotnet run --project modules/inspection/src/NickERP.Inspection.Web/NickERP.Inspection.Web.csproj
dotnet run --project apps/edge-node/NickERP.EdgeNode/NickERP.EdgeNode.csproj
```

The apps need the expected Postgres databases and environment/user-secret overrides for placeholder passwords and Cloudflare Access settings. Development bypass is configured through `NickErp:Identity:CfAccess:DevBypass`.

## v1 and v1-clone

`C:\Shared\NSCIM_PRODUCTION\` remains the separate v1 production repo. Do not make v1 production changes from this repository.

`v1-clone/` inside this repo is a compatibility/reference island, not the active v2 architecture. It contains point-in-time NickFinance/NickHR clones used for pilot co-deploy and migration planning. Treat it as read-mostly historical material unless a task explicitly targets the clone workflow. New v2-native work belongs under `modules/` and shared platform work belongs under `platform/`.

## Documentation Map

- [`docs/architectural-design-analysis-2026-05-13.md`](docs/architectural-design-analysis-2026-05-13.md) is the latest source-backed architecture analysis and target design proposal.
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) is the living design record. Some early sections preserve original design rationale; prefer newer "current state" notes where sections differ.
- [`ROADMAP.md`](ROADMAP.md) tracks shipped work, operator blockers, and post-pilot scope.
- [`TESTING.md`](TESTING.md) covers test and click-through workflows.
- [`apps/edge-node/README.md`](apps/edge-node/README.md) covers edge-node configuration and operational behavior.
- [`docs/runbooks/`](docs/runbooks/) contains named operational runbooks. Some sprint/team docs are explicitly historical and keep their original problem statements for audit trail.

## Rules Of Engagement

- Keep v2 standalone: no shared project references into `C:\Shared\NSCIM_PRODUCTION\`.
- Keep vendor and authority names in adapters, plugins, or authority modules; core domain language remains generic.
- Keep v1-clone changes isolated and clearly marked as clone compatibility work.
- When documentation disagrees, prefer the source-backed architecture analysis, active project files, and current roadmap over older design-only text.

## Path Note

The folder name `ERP V2` contains a space. Quote shell references:

```powershell
cd "C:\Shared\ERP V2"
```
