# `v1-clone/nickhr/` — point-in-time clone of v1's NickHR into v2

**Cloned**: 2026-05-04 (Sprint 15 / VP6 + parallel NickHR clone)
**Source**: `C:\Shared\NSCIM_PRODUCTION\NickHR\` (v1 repo,
`github.com/bjforson/NSCIM-PRODUCTION`)
**This repo**: `C:\Shared\ERP V2\` (v2 repo,
`github.com/bjforson/ERP-V2`, private)

## What this is

A verbatim source-only copy of the v1 NickHR module (8 projects + 1
test project), plus its strict transitive `NickERP.Platform.Tenancy`
dep that was not already in the v1-clone (the other Tenancy-adjacent
deps — Core / Identity / Observability / Web.Shared — were cloned for
NickFinance on 2026-04-30 and are reused here).

Why it exists: the **directional v1/v2 separation rule** plus the
pilot strategy locked on 2026-05-02 (plan-mode walk):

> Three modules co-deployed under one v2 portal at pilot —
> inspection v2-native + NickFinance v1-clone + NickHR v1-clone.
> Post-pilot, the v1-clones get folded into v2-native architecture
> (~6-10 sprints per module).

Pre-pilot, NickHR continues to ship from v1's deploy script. v2 owns
the canonical source going forward (this clone), and v1's copy
becomes the deploy-staging artefact.

## What was cloned

| v2 path | v1 origin | Project count |
| --- | --- | --- |
| `v1-clone/nickhr/src/NickHR.API/` | `NickHR/src/NickHR.API/` | 1 |
| `v1-clone/nickhr/src/NickHR.Core/` | `NickHR/src/NickHR.Core/` | 1 |
| `v1-clone/nickhr/src/NickHR.Infrastructure/` | `NickHR/src/NickHR.Infrastructure/` | 1 |
| `v1-clone/nickhr/src/NickHR.Services/` | `NickHR/src/NickHR.Services/` | 1 |
| `v1-clone/nickhr/src/NickHR.Services.Payroll/` | `NickHR/src/NickHR.Services.Payroll/` | 1 |
| `v1-clone/nickhr/src/NickHR.Services.Recruitment/` | `NickHR/src/NickHR.Services.Recruitment/` | 1 |
| `v1-clone/nickhr/src/NickHR.Shared/` | `NickHR/src/NickHR.Shared/` | 1 |
| `v1-clone/nickhr/src/NickHR.WebApp/` | `NickHR/src/NickHR.WebApp/` | 1 |
| `v1-clone/nickhr/test/NickHR.WebApp.Tests/` | `NickHR/test/NickHR.WebApp.Tests/` | 1 |
| `v1-clone/nickhr/NickHR.sln` | `NickHR/NickHR.sln` | (solution) |
| `v1-clone/nickhr/{certs,data,deploy,scripts,uploads,NuGet.config}` | identical paths in v1 | (config + assets) |

`bin/`, `obj/`, `*.user`, `*.suo`, `build.log` were excluded.

## What was NOT cloned

- The `NickHR/src/NickHR.WebApp/Identity/RoleNames.cs` mirror,
  `NickFinanceAccessSection.razor`, `ModuleAccessPanel.razor`,
  `IdentityProvisioningService.cs`, and
  `NickFinanceAccessSectionTests.cs` are part of the role-overhaul
  arc (plan: `~/.claude/plans/lovely-sleeping-metcalfe.md`). They
  ARE included in this clone (they live inside `src/NickHR.WebApp/`).
  Earlier plan said they wouldn't be cloned for NickFinance; now that
  we're cloning NickHR itself, those files come along.
- v1's running databases (`nickhr` Postgres, the seeded grades /
  permissions / grants from 2026-04-30) — not cloned. Edit the schema
  via `tools/migration-runner` against the v1 DB (`nscim_app` cannot
  run DDL; use `Username=postgres`) once a v2 migration framework lands.

## ProjectReference resolution

NickHR's csproj files reference platform deps with relative paths:

```xml
<ProjectReference Include="..\..\..\platform\NickERP.Platform.Core\NickERP.Platform.Core.csproj" />
<ProjectReference Include="..\..\..\platform\NickERP.Platform.Identity\NickERP.Platform.Identity.csproj" />
<ProjectReference Include="..\..\..\platform\NickERP.Platform.Tenancy\NickERP.Platform.Tenancy.csproj" />
```

Inside v1: `NSCIM_PRODUCTION/NickHR/src/<project>/` → `NSCIM_PRODUCTION/platform/<dep>/`.

Inside v2 v1-clone: `v1-clone/nickhr/src/<project>/` → `v1-clone/platform/<dep>/`.

The relative-path arithmetic resolves correctly in both trees as long
as the `v1-clone/platform/` and `v1-clone/nickhr/` siblings stay where
they are.

## Editing flow going forward

1. Edit NickHR (and its v1-clone platform deps) in
   `C:\Shared\ERP V2\v1-clone\nickhr\…` and
   `C:\Shared\ERP V2\v1-clone\platform\…`.
2. Commit to the v2 origin (`github.com/bjforson/ERP-V2`).
3. Mirror the changed files back to
   `C:\Shared\NSCIM_PRODUCTION\NickHR\…` and
   `C:\Shared\NSCIM_PRODUCTION\platform\…` — deploy-staging only,
   not a separate edit point.
4. Run NickHR's deploy script (in NSCIM_PRODUCTION) to ship.

Eventually the deploy direction flips (v2 publishes; v1 mirror step
disappears), but that's a separate planned task.

## Two coexisting NickERP.Platform.Tenancy flavours in v2

After this clone, the v2 repo has TWO copies of `NickERP.Platform.Tenancy`:

- **`ERP V2\v1-clone\platform\NickERP.Platform.Tenancy\`** — the v1
  mature design, identical to v1's; only here so the cloned NickHR +
  NickFinance compile against the same shape they always did. Edit
  these when changing the running NickHR.

- **`ERP V2\platform\NickERP.Platform.Tenancy\`** — the v2 GREENFIELD
  design (different shape: `TenantConnectionInterceptor`,
  `ITenantOwned`, `SetSystemContext` + sentinel `'-1'`). Used by
  inspection v2-native + apps/portal + NickFinance pathfinder.

The two copies have different namespace conventions (the v2 greenfield
adds Database split-out, audit register, system-context register,
interceptor variants). Don't conflate them.

## Reciprocal V1 banner

`C:\Shared\NSCIM_PRODUCTION\NickHR\V1_BANNER.md` — read-this-before-editing
on the v1 deploy-staging copy. Same exception NickFinance got for the
`finance/V1_BANNER.md` file — adding **only** that banner file is
allowed in v1 even though v1 is otherwise read-only during v2 dev.

## Refactor plan (post-pilot)

NickHR's v2-native refactor (folding into the greenfield platform stack:
RLS via `TenantConnectionInterceptor` + `ITenantOwned` + `app.tenant_id`
session var + audit register) is post-pilot work, ~6-10 sprints. Until
then, NickHR continues running on v1's platform-Tenancy variant.
