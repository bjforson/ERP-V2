# `v1-clone/` — point-in-time clone of v1's NickFinance into v2

**Cloned**: 2026-04-30 17:30 UTC
**Source**: `C:\Shared\NSCIM_PRODUCTION\` (the v1 repo —
`github.com/bjforson/NSCIM-PRODUCTION`)
**This repo**: `C:\Shared\ERP V2\` (the v2 repo —
`github.com/bjforson/ERP-V2`, private)

## What this is

A verbatim file-tree copy of the working NickFinance code (and the v1
platform projects it depends on) from inside the v1 tree, dropped into
the v2 tree so this repo carries the canonical NickFinance source going
forward.

The clone exists because of the **directional v1/v2 separation rule**
(see `~/.claude/projects/.../memory/reference_v1_v2_separation.md`):

> NickFinance lives in v2 (`C:\Shared\ERP V2\`) and pushes to
> `github.com/bjforson/ERP-V2`. Never let those commits land on the v1
> origin (`github.com/bjforson/NSCIM-PRODUCTION`).

But the rich working NickFinance grew up inside the v1 tree under
deploy-driven development, never moved across, and deploys still run from
there. This `v1-clone/` is the first step in normalising that — git
captures it here; future NickFinance dev happens here; the v1-tree copy
becomes the deploy-staging artefact only.

## What was cloned

| Path under `v1-clone/` | Origin in v1 | Project count |
| --- | --- | --- |
| `finance/` | `NSCIM_PRODUCTION/finance/` | 30 |
| `platform/NickERP.Platform.Core/` | `NSCIM_PRODUCTION/platform/NickERP.Platform.Core/` | 1 |
| `platform/NickERP.Platform.Identity/` | `NSCIM_PRODUCTION/platform/NickERP.Platform.Identity/` | 1 |
| `platform/NickERP.Platform.Observability/` | `NSCIM_PRODUCTION/platform/NickERP.Platform.Observability/` | 1 |
| `platform/NickERP.Platform.Web.Shared/` | `NSCIM_PRODUCTION/platform/NickERP.Platform.Web.Shared/` | 1 |

`bin/`, `obj/`, `.vs/` were excluded. Source-only: ~352 files, ~2 MB.

These four `platform/*` projects are the strict transitive deps of v1
NickFinance — copied as-is so the relative `..\..\platform\...`
ProjectReferences inside the cloned `finance/*.csproj` files still resolve
within this `v1-clone/` subtree.

## What was NOT cloned

- **NickHR** — v1's HR module. Stays in v1; the platform/Identity bits in
  this clone are also used by NickHR but NickHR itself is not v2 work.
- **The v2 greenfield NickFinance skeleton** at
  `C:\Shared\ERP V2\modules\nickfinance\src\NickERP.NickFinance.{Core,Database,Web}\`
  — that's a separate, much-smaller scaffolded design. Different shape,
  different namespace; not yet built. It stays as-is. We will eventually
  decide whether to fold this `v1-clone/` into that skeleton or replace
  the skeleton outright.
- **The v2 greenfield platform layers** at
  `C:\Shared\ERP V2\platform\NickERP.Platform.{Identity,Audit,Logging,Telemetry,Plugins,Tenancy,Web.Shared}\`
  — these are different designs from the v1 ones. Both copies coexist;
  the `v1-clone/platform/` ones are scoped to the cloned NickFinance, the
  top-level v2 ones are the greenfield rebuild.

## Today's role-overhaul (2026-04-30)

The role-overhaul work that landed today
(plan: `~/.claude/plans/lovely-sleeping-metcalfe.md`) is included in this
clone — both the NickFinance-side files (`finance/NickFinance.WebApp/Identity/*`,
the bootstrap CLI seed steps, the rewritten Razor pages) and the
platform-side files (`platform/NickERP.Platform.Identity/Entities.cs` +
`IdentityDbContext.cs` + the new `Migrations/20260430120000_Add_Permissions_RolePermissions.{cs,Designer.cs}`).

The corresponding NickHR-side wrapper changes
(`NickHR/src/NickHR.WebApp/Identity/RoleNames.cs` mirror,
`NickFinanceAccessSection.razor`, `ModuleAccessPanel.razor`,
`IdentityProvisioningService.cs` audit-vs-ops check, the rewritten
`NickFinanceAccessSectionTests.cs`) are NOT in this clone — they live in
the v1 tree at `NSCIM_PRODUCTION/NickHR/` and stay there.

## Going forward

- **Develop NickFinance changes in this `v1-clone/` tree** (git origin
  `github.com/bjforson/ERP-V2`).
- **Sync to the v1 deploy tree** when ready to deploy: copy the changed
  files back to `NSCIM_PRODUCTION/finance/...` and `NSCIM_PRODUCTION/platform/...`,
  build + deploy from there. (The deploy script + service binPaths still
  live in v1.)
- **Eventually, swap the deploy direction** — move services to publish
  from v2 — but that's a separate planned task.

For the v1-side reciprocal banner, see
`C:\Shared\NSCIM_PRODUCTION\finance\V1_BANNER.md`.
