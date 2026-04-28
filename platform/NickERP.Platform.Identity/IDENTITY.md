# NickERP.Platform.Identity

> Status (2026-04-23): A.2.1 – A.2.10 shipped — layer is feature-complete and
> the acceptance demo at `platform/demos/identity/` runs end-to-end. Remaining
> open items (first-admin bootstrap CLI, OpenAPI spec generation) are
> nice-to-haves and don't block consumers. ROADMAP §A.2.

The single contract every NickERP service uses to answer
**"who is this caller, and what may they do?"**. Owns canonical user
records, app-scope assignments, Cloudflare Access JWT validation,
service-token authentication, and the admin REST API for managing
all of the above.

---

## Layer split — three projects

| Project | What it ships | Reference |
|---|---|---|
| `NickERP.Platform.Identity` | Auth scheme + JWT validator + claim contract — no DB knowledge | `AddNickErpIdentity()` |
| `NickERP.Platform.Identity.Database` | EF Core `IdentityDbContext`, migrations, `DbIdentityResolver` (the real `IIdentityResolver` impl) | `AddNickErpIdentityCore()` |
| `NickERP.Platform.Identity.Api` | Minimal-API admin endpoints under `/api/identity/...` | `MapNickErpIdentityAdmin()` |

Hosts compose them based on need:

```csharp
// A NickERP service that just needs to authenticate callers and read scopes:
builder.Services
    .AddNickErpIdentity(builder.Configuration, builder.Environment)  // auth scheme
    .Services
    .AddNickErpIdentityCore(builder.Configuration);                  // DbContext + DbIdentityResolver

// The dedicated identity-admin service additionally maps the admin API:
app.MapNickErpIdentityAdmin();   // /api/identity/users, /scopes, /service-tokens
```

A consumer can swap the resolver impl (e.g. an HTTP-client resolver
that calls a remote identity service) without re-registering the auth
scheme — that's why `Identity` and `Identity.Database` are separate.

---

## Header contract

Every NickERP service trusts these on inbound requests; the auth
handler resolves them in this order on every call:

| Header | Source | Resolution path |
|---|---|---|
| `X-Dev-User: someone@nickscan.com` | Local dev only | Looked up by email in `identity.users`. **Throws at startup** if `DevBypass.Enabled=true` outside the `Development` environment — see `IdentityServiceCollectionExtensions`. |
| `Cf-Access-Jwt-Assertion: <jwt>` | Cloudflare Access edge | Validated against CF's JWKS (`https://{TeamDomain}.cloudflareaccess.com/cdn-cgi/access/certs`). On success, `sub` is checked against `service_tokens.token_client_id` first; if no service-token match, `email` claim is looked up in `identity.users`. |
| `Authorization: Bearer <jwt>` | Local testing fallback | Same JWT validation as the CF header. Disabled by setting `AcceptAuthorizationHeaderFallback: false`. |

If none resolve cleanly, the auth handler returns
`AuthenticateResult.NoResult()` — apps see an unauthenticated
principal and (with `[Authorize]`) return `401`.

---

## Domain model

| Entity | Role |
|---|---|
| `IdentityUser` | One row per real human. Email is the natural key. Soft-delete via `IsActive=false`. Auto-provisioned on first valid CF JWT for an unknown email. |
| `AppScope` | A named permission registered at app install time, e.g. `Finance.PettyCash.Approver`. Code is dot-separated PascalCase — enforced by the admin API. |
| `UserScope` | Link-table grant. Time-boundable (`ExpiresAt`), revocable (`RevokedAt` + `RevokedByUserId`), never deleted. The resolver excludes revoked / expired rows from the active set. |
| `ServiceTokenIdentity` (+ `ServiceTokenScope`) | Non-human caller (CF Access service token). `token_client_id` is the natural key. Has its own scope set; never collapses into a user. |

Schema: `identity` inside DB `nickerp_platform`. Migration history
table is `identity.__ef_migrations_history`. First migration is
`20260425205153_Initial_AddIdentitySchema`.

---

## Claims emitted by the auth handler

`NickErpAuthenticationHandler` translates the resolved identity into
ASP.NET Core claims. Use the constants in `Auth/NickErpClaims.cs`
when reading them:

| Claim | Value |
|---|---|
| `NickErpClaims.Id` (+ `ClaimTypes.NameIdentifier`) | Canonical id — `IdentityUser.Id` for humans, `ServiceTokenIdentity.Id` for service tokens. |
| `NickErpClaims.DisplayName` (+ `ClaimTypes.Name`) | Friendly label for UIs and audit. Falls back to email for users with no `DisplayName`. |
| `NickErpClaims.TenantId` | The tenant scope. |
| `NickErpClaims.IsServiceToken` | `"true"` / `"false"`. |
| `NickErpClaims.Email` (+ `ClaimTypes.Email`) | Lowercased email. Absent for service tokens. |
| `NickErpClaims.ExternalSubject` | The CF JWT `sub` claim — preserved for audit. Absent for the dev-bypass path. |
| `NickErpClaims.Scope` (one per scope) | The set of `AppScope.Code` values currently granted. |
| `ClaimTypes.Role` (one per scope) | Each scope is mirrored as a Role so `[Authorize(Roles="Finance.PettyCash.Approver")]` works without custom policy plumbing. |

---

## Authorization patterns

```csharp
// Single scope:
[Authorize(Roles = "Finance.PettyCash.Approver")]

// Any-of:
[Authorize(Roles = "HR.Admin,HR.Manager")]

// Inside a handler (when you need branching, not just gate):
public async Task<IResult> SomeEndpoint(HttpContext ctx, IIdentityResolver resolver)
{
    var who = await resolver.ResolveAsync(ctx);
    if (who is null) return Results.Unauthorized();
    if (!who.HasScope("Finance.PettyCash.Approver")) return Results.Forbid();
    if (who.IsServiceToken) return Results.Forbid(); // humans only here
    return Results.Ok($"Hello {who.DisplayName}");
}
```

App code never parses the JWT itself, never calls a database for user
lookup, never decides what email maps to what scope. The auth scheme +
resolver own all of it.

---

## Admin REST API (`MapNickErpIdentityAdmin`)

Mounted under `/api/identity/`. Every endpoint requires the
`Identity.Admin` scope. All requests are paged where the response set
could be large; defaults are page 1, page size 25, max page size 200.

### Users

| Verb | Path | Body | Returns |
|---|---|---|---|
| `GET` | `/users` | — | `PagedResult<UserDto>` (filters: `q`, `tenantId`, `page`, `pageSize`) |
| `GET` | `/users/{id}` | — | `UserDto` or 404 |
| `POST` | `/users` | `CreateUserRequest` (email, displayName, tenantId, initialScopes) | `201 UserDto`, or `409` if email already taken in the tenant |
| `PATCH` | `/users/{id}/scopes` | `UpdateUserScopesRequest` (grant[], revoke[], expiresAt, notes) | `200 UserDto` (revokes apply before grants so revoke+regrant in one call works) |
| `DELETE` | `/users/{id}` | — | `204` (sets `IsActive=false`; idempotent) |

### App scopes

| Verb | Path | Body | Returns |
|---|---|---|---|
| `GET` | `/scopes` | — | `AppScopeDto[]` (filters: `tenantId`, `app`) |
| `POST` | `/scopes` | `CreateAppScopeRequest` (code regex-validated, see below) | `201 AppScopeDto`, or `409` |
| `DELETE` | `/scopes/{id}` | — | `204` (sets `IsActive=false`; existing UserScope grants survive but resolver excludes them) |

#### Scope code naming (G1 #6 — hard rule)

Every `AppScope.Code` MUST match the regex `^[A-Z][A-Za-z]+(\.[A-Z][A-Za-z]+)+$`:

- At least two dot-separated segments.
- Each segment starts with an uppercase letter, then 1+ letters (no digits, no underscores, no dashes).
- The first segment is the **app prefix** — `Identity`, `Inspection`, `Finance`, etc. — and acts as the namespace. Two apps cannot reuse a leaf without colliding (e.g. `Identity.Admin` and `Finance.Admin` are two different scopes).

Valid: `Identity.Admin`, `Inspection.CaseReviewer`, `Finance.PettyCash.Approver`, `Finance.Reports.Read`.
Rejected at `POST /scopes`: `admin` (single segment), `admin.foo` (lowercase), `Finance.123Approver` (digits), `Finance.Petty_Cash` (underscore), `Finance.A.B` (single-letter segment).

The validator runs at the API boundary; existing rows from before G1 are not retroactively re-validated, but the dev seeder + tests use compliant names.

### Service tokens

| Verb | Path | Body | Returns |
|---|---|---|---|
| `GET` | `/service-tokens` | — | `ServiceTokenDto[]` (filters: `tenantId`) |
| `POST` | `/service-tokens` | `CreateServiceTokenRequest` (tokenClientId, displayName, purpose, expiresAt, initialScopes) | `201 ServiceTokenDto`, or `409` |
| `PATCH` | `/service-tokens/{id}/scopes` | `UpdateServiceTokenScopesRequest` (grant[], revoke[], expiresAt) | `200 ServiceTokenDto` |
| `DELETE` | `/service-tokens/{id}` | — | `204` (sets `IsActive=false`; idempotent) |

### Bootstrapping the first admin

Chicken-and-egg: the API requires `Identity.Admin` scope to grant
scopes, including `Identity.Admin`. Two supported bootstrap paths:

1. **Dev environment** — set `NickErp:Identity:CfAccess:DevBypass:Enabled=true`
   in `appsettings.Development.json`, hit any admin endpoint with the
   `X-Dev-User` header, manually `INSERT` a `UserScope` row granting
   `Identity.Admin` to the resolved user. Subsequent grants happen
   through the API.
2. **Production** — once via DBA: `INSERT` `Identity.Admin` for the
   first human in `identity.user_scopes`. Document in your runbook;
   never automate.

The DI extension explicitly **throws at startup** if `DevBypass.Enabled`
is true outside the `Development` environment, so path 1 cannot leak
into prod.

---

## Migration patterns from legacy user stores (Track C.2)

When wiring an existing v1 app (NickHR, NSCIM) to consume this layer,
the path is:

1. **Lookup** — replace the v1 user-by-email query with
   `IIdentityResolver.ResolveAsync(HttpContext)`. Carry on using the
   v1 user record (Employees, NSCIM Users, etc.) keyed by the
   resolved email for *profile* data only.
2. **Authorisation** — replace v1 role checks with scope checks
   against `ResolvedIdentity.HasScope`. Translate v1 roles to v2
   scopes via a one-time SQL backfill on `user_scopes`.
3. **Provisioning** — let the auto-provision path create the
   canonical user on first sign-in. Backfill historical users
   ahead of cutover via a one-off script that reads the v1 store
   and INSERTs into `identity.users`.
4. **Soft-delete** — when a v1 app marks an employee inactive,
   PATCH the canonical user via the admin API. Don't update
   `identity.users.IsActive` directly from app code.

Detailed runbook lives in Track C.2 entries of `ROADMAP.md` (one per
v1 app cut over).

---

## Open contract questions

- [x] **JWKS caching** — answered in `Auth/CfJwtValidator.cs`. Uses
      `Microsoft.IdentityModel.Protocols.ConfigurationManager`
      which auto-refreshes on TTL or unknown-kid. No bespoke logic.
- [x] **First-login auto-provision** — answered in
      `Database/Services/DbIdentityResolver.cs`. Unknown email +
      valid JWT creates a new `IdentityUser` with **zero scopes** and
      `IsActive=true`. Admin grants scopes after.
- [x] **Service-token vs user precedence** — answered: JWT `sub` is
      checked against `service_tokens.token_client_id` first; if no
      match, `email` claim is looked up against `users`.
      Cf-Access never issues a JWT where a single `sub` matches both,
      so the precedence is unambiguous.
- [ ] **Multi-email aliasing** — does
      `angela@nickscan.com` and `angela.ayanful@nickscan.com` ever
      resolve to the same user? Deferred until a real case forces it.
- [ ] **Tenant resolution from JWT** — Cloudflare Access doesn't
      carry tenant. Single-tenant for v1; revisit when Phase A.3
      tenant bootstrap lands.
- [ ] **Audit trail of admin API calls** — currently nothing. Phase
      A.5 (Audit & Events) will plug in via outbox; until then,
      admin actions are visible only via DB write history.

---

## Module layout

```
platform/NickERP.Platform.Identity/
├── Auth/
│   ├── CfAccessAuthenticationOptions.cs   — config + Validate()
│   ├── CfJwtValidator.cs                  — JWT vs CF JWKS
│   ├── NickErpAuthenticationHandler.cs    — ASP.NET Core auth scheme
│   └── NickErpClaims.cs                   — claim-name constants
├── Entities/
│   ├── IdentityUser.cs
│   ├── AppScope.cs
│   ├── UserScope.cs
│   └── ServiceTokenIdentity.cs            (+ ServiceTokenScope)
├── Services/
│   ├── IIdentityResolver.cs               — the contract apps consume
│   └── ResolvedIdentity.cs                — record returned by resolver
├── IdentityServiceCollectionExtensions.cs — AddNickErpIdentity()
├── IDENTITY.md                            — this file
└── NickERP.Platform.Identity.csproj

platform/NickERP.Platform.Identity.Database/
├── Migrations/
│   ├── 20260425205153_Initial_AddIdentitySchema.cs
│   ├── 20260425205153_Initial_AddIdentitySchema.Designer.cs
│   └── IdentityDbContextModelSnapshot.cs
├── Services/
│   └── DbIdentityResolver.cs              — IIdentityResolver impl
├── IdentityDbContext.cs
├── IdentityDatabaseServiceCollectionExtensions.cs — AddNickErpIdentityCore()
└── NickERP.Platform.Identity.Database.csproj

platform/NickERP.Platform.Identity.Api/
├── Models/
│   └── Dtos.cs                            — request + response DTOs
├── IdentityAdminEndpoints.cs              — MapNickErpIdentityAdmin()
└── NickERP.Platform.Identity.Api.csproj

platform/demos/identity/                   — A.2.9 acceptance demo
├── Components/
│   ├── Layout/MainLayout.razor + NavMenu.razor
│   ├── Pages/Home.razor                   — claims dump + scope check
│   ├── Pages/RoundTrip.razor              — scope→user→grant→resolve
│   ├── Pages/Lists.razor                  — read-only table dump
│   ├── App.razor + Routes.razor + _Imports.razor
├── Properties/launchSettings.json         — http://localhost:5260
├── wwwroot/app.css
├── appsettings.json + appsettings.Development.json
├── NickERP.Platform.Demos.Identity.csproj
├── NickERP.Platform.Demos.Identity.http   — curl-equivalent test surface
├── Program.cs                             — host wiring (auth + admin API + Blazor)
└── README.md                              — including first-admin bootstrap SQL
```

---

## What's still open in A.2

- [x] **A.2.9 Demo app** at `platform/demos/identity/` — Blazor Server
      app that mounts the auth scheme, the DB-backed resolver, and the
      admin REST API on a single host. Has a `/round-trip` page that
      creates scope → creates user → grants → re-resolves and reports
      green/red per step. Dev-bypass enabled in `Development` so engineers
      run it without configuring CF Access locally. **Acceptance gate met.**
- **First-admin bootstrap script** — a one-line `dotnet run` tool
  that inserts the `Identity.Admin` grant for a given email so prod
  setup doesn't require DBA-level SQL. Demo's README §"First-admin
  bootstrap" has the SQL template; turning that into a CLI is on the
  backlog.
- **OpenAPI spec generation** — `MapNickErpIdentityAdmin` exposes
  endpoints with route metadata but no Swagger doc shipped yet.
  Add an opinionated `MapNickErpIdentityAdminSwagger()` extension
  next to it.

Neither of the open items blocks consumers from wiring up auth + scope
checks today.

---

## Out of scope

- **Password storage.** We don't have passwords; CF Access does
  email OTP / SAML / OIDC. The platform never sees a password.
- **Group / role hierarchy beyond a flat scope list.** If a real
  case demands it, build the resolver that flattens hierarchies on
  read. Don't put hierarchy in the schema.
- **Per-record authorisation.** App-level concern. This layer says
  "Angela has scope X". Whether Angela can read row Y is the app's
  call.
- **Identity provider federation (Google / M365 / SAML).** Cloudflare
  Access already handles this — `Identity:CfAccess:TeamDomain`
  points at a CF team that can be configured for any IdP.

---

## Related docs

- `ROADMAP.md` §A.2 — task list and acceptance criteria
- `ROADMAP.md` §C.2 — v1 retrofit playbook (when each legacy app
  cuts over to consume `IIdentityResolver`)
- v1 repo `platform/NickERP.Portal/SSO.md` — the original
  Option-A-now / Option-D-later decision this layer enables.
