# NickERP.Platform.Demos.Identity

End-to-end smoke check for **Track A.2**: validates that the `NickERP.Platform.Identity` + `.Identity.Database` + `.Identity.Api` projects compose correctly into a real host. Acceptance gate for the layer per `ROADMAP.md` §A.2.

This is a **throwaway demo** — it exists to keep the platform contracts honest before Track B starts. Do not import or consume from production services.

## What it proves

The single Blazor Server app:

1. **Mounts the auth scheme** — `services.AddNickErpIdentity(...)` is wired the same way every production host will wire it.
2. **Mounts the DB-backed resolver** — `services.AddNickErpIdentityCore(...)` against the real `nickerp_platform` Postgres database.
3. **Mounts the admin REST API** — `app.MapNickErpIdentityAdmin()` exposes user / scope / service-token CRUD under `/api/identity/`, gated by the `Identity.Admin` scope.
4. **Resolves on every request** — the home page dumps the `ResolvedIdentity` and the raw claim collection so you can see exactly what `[Authorize]` sees.
5. **Round-trips end-to-end** — the `/round-trip` page does scope → user → grant → re-resolve in one button-click, fails loudly if any step or invariant breaks.

## Prerequisites

- .NET 10 SDK.
- Postgres on localhost with database `nickerp_platform` reachable; migrations from `NickERP.Platform.Identity.Database` applied (`dotnet ef database update --project ..\..\NickERP.Platform.Identity.Database --startup-project .`).
- Connection string: set `ConnectionStrings:Platform` (in user secrets, env var `ConnectionStrings__Platform`, or directly in `appsettings.Development.json`).

## Run

```bash
cd /c/Shared/ERP\ V2/platform/demos/identity
dotnet run
```

The demo listens on **http://localhost:5260**.

## How to authenticate locally

In Development mode, the **dev-bypass** is enabled (`appsettings.Development.json`). Two options:

### Option A — REST clients (curl, .http file)

Attach `X-Dev-User: dev@nickscan.com` to every request. The `.http` file in this folder demonstrates this for every endpoint.

```bash
curl -H "X-Dev-User: dev@nickscan.com" http://localhost:5260/api/identity/users
```

### Option B — Browser

Browsers don't send custom headers on plain navigations. Use one of:

- **REST Client extension** (VS Code's built-in `.http` runner) — point it at the `.http` file in this folder.
- **A browser extension** (e.g. *ModHeader*) configured to add `X-Dev-User: dev@nickscan.com` for `localhost:5260`.
- **`curl --cookie-jar`** to grab a session, then open the browser separately. Not pretty; the extension is easier.

## First-admin bootstrap

The round-trip page and `/lists` page require the `Identity.Admin` scope. To grant yourself that scope on first run, drop into psql:

```sql
\c nickerp_platform

-- 1. Mint the admin scope row if it doesn't exist
INSERT INTO public.app_scopes (id, code, app_name, description, is_active, created_at, tenant_id)
VALUES (gen_random_uuid(), 'Identity.Admin', 'platform.identity', 'Admin gate for Identity REST API.', TRUE, NOW(), 1)
ON CONFLICT (tenant_id, code) DO NOTHING;

-- 2. Mint the dev user row if it doesn't exist
INSERT INTO public.identity_users (id, email, normalized_email, display_name, is_active, created_at, updated_at, tenant_id)
VALUES (gen_random_uuid(), 'dev@nickscan.com', 'DEV@NICKSCAN.COM', 'Local dev', TRUE, NOW(), NOW(), 1)
ON CONFLICT (tenant_id, normalized_email) DO NOTHING;

-- 3. Grant Identity.Admin to dev@nickscan.com
INSERT INTO public.user_scopes (id, identity_user_id, app_scope_code, granted_at, granted_by_user_id, tenant_id, notes)
SELECT gen_random_uuid(), u.id, 'Identity.Admin', NOW(), u.id, 1, 'Bootstrap grant'
FROM public.identity_users u
WHERE u.normalized_email = 'DEV@NICKSCAN.COM'
ON CONFLICT DO NOTHING;
```

A scripted bootstrap CLI is on the backlog — see `IDENTITY.md` §Open Questions.

## Pages

| Route          | What it shows                                                     |
|----------------|-------------------------------------------------------------------|
| `/`            | Resolved identity + raw claim dump + scope check                  |
| `/round-trip`  | Scope → user → grant → resolve, with green/red per step           |
| `/lists`       | Read-only dump of `app_scopes`, `identity_users`, service tokens  |

## Endpoints (mounted under `/api/identity/`)

| Method | Route | Purpose |
|---|---|---|
| GET    | `/users`                | Paged + searchable user list |
| GET    | `/users/{id}`           | Single user with full scope history |
| POST   | `/users`                | Create user, optionally with initial scopes |
| PATCH  | `/users/{id}/scopes`    | Atomic revoke + grant (revokes happen first) |
| DELETE | `/users/{id}`           | Soft-deactivate (sets `is_active = false`) |
| GET    | `/scopes`               | List app scopes |
| POST   | `/scopes`               | Create app scope |
| DELETE | `/scopes/{id}`          | Soft-retire scope |
| GET    | `/service-tokens`       | List service tokens |
| POST   | `/service-tokens`       | Create token (no secret minted; CF Access stores that out-of-band) |
| PATCH  | `/service-tokens/{id}/scopes` | Atomic revoke + grant for token scopes |
| DELETE | `/service-tokens/{id}`  | Soft-deactivate token |

Every endpoint requires the `Identity.Admin` scope. The full request/response shapes live in `NickERP.Platform.Identity.Api/Models/Dtos.cs`.

## Roadmap reference

This demo is **Track A.2.9** of `C:\Shared\NSCIM_PRODUCTION\ROADMAP.md`. Once Track B starts and a real module (e.g. NickFinance.PettyCash) consumes the Identity layer, this folder can be deleted — real modules become the validation.
