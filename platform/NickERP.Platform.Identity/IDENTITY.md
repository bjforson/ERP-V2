# NickERP.Platform.Identity

> Status: scaffolded (A.2.1, A.2.2). Auth middleware (A.2.4),
> resolver (A.2.5), dev bypass (A.2.6), service tokens (A.2.7),
> admin REST API (A.2.8), and demo app (A.2.9) are all open.
>
> See `ROADMAP.md §A.2` for the task list.

---

## What this layer does

Owns the canonical identity of every actor in the suite — humans and
service callers alike — and exposes one resolver every NickERP service
consumes:

```csharp
public interface IIdentityResolver
{
    Task<ResolvedIdentity?> ResolveAsync(HttpContext ctx, CancellationToken ct = default);
}
```

`ResolveAsync` reads either:
- `CF-Access-Jwt-Assertion` header → human user (resolves to `IdentityUser`),
- `CF-Access-Client-Id` + `CF-Access-Client-Secret` headers → service caller
  (resolves to `ServiceTokenIdentity`),
- in `Development` only, an `X-Dev-User: someone@nickscan.com` header → fake
  human user for local work.

It returns a `ResolvedIdentity` with the canonical id, email/display name,
and the active scope set. App code never parses the JWT itself.

## Domain model

Four entities:

| Entity | Role |
|---|---|
| `IdentityUser` | One row per real human. Email is the natural key matched against the JWT claim. Soft-delete via `IsActive=false`. |
| `AppScope` | A named permission registered by an app at install time, e.g. `Finance.PettyCash.Approver`. |
| `UserScope` | Link table grant. Time-boundable, revocable, never deleted (audit). |
| `ServiceTokenIdentity` (+ `ServiceTokenScope`) | Non-human caller. Cloudflare Access service token client id is the natural key. Has its own scope set; never collapses into a user. |

## Open contract questions

These get answered as A.2.4 onwards lands:

- [ ] JWKS caching policy — refresh interval and behaviour during a CF outage.
- [ ] First-login auto-provisioning — does an unknown email auto-create an
      `IdentityUser` with no scopes, or require an admin to invite first?
      (Leaning: auto-create + zero scopes; admin grants scopes after.)
- [ ] Multi-email aliasing — does `angela@nickscan.com` and
      `angela.ayanful@nickscan.com` ever resolve to the same user? Defer
      until a real case forces it.
- [ ] Tenant resolution from JWT — Cloudflare Access doesn't carry tenant.
      Need a strategy for multi-tenant deployments. (Phase 5 problem;
      single-tenant for v1.)

## Consumers

Apps wire this in via `services.AddNickErpIdentity()` (extension method
shipped in A.2.5). Once wired, they call:

```csharp
var resolved = await resolver.ResolveAsync(HttpContext);
if (resolved is null) return Results.Unauthorized();
if (!resolved.HasScope("Finance.PettyCash.Approver")) return Results.Forbid();
```

No JWT parsing. No PBKDF2. No `[Authorize(Roles=...)]`. The resolver
owns it all.

## Out of scope

- Password storage. We don't have passwords; CF Access does email OTP
  (today) or SSO/SAML (later). The platform never sees a password.
- Group/role hierarchy beyond a flat scope list. If we need
  hierarchies, build the resolver that flattens them on read; don't put
  hierarchy in the schema.
- Per-record authorisation. That's app-level; this layer says "Angela
  has scope X", not "Angela can read row Y".

## Related docs

- `ROADMAP.md §A.2` — task list and acceptance criteria
- `platform/NickERP.Portal/SSO.md` — the original Option-A-now,
  Option-D-later decision that this layer enables
