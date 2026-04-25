using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity.Api.Models;
using NickERP.Platform.Identity.Auth;
using NickERP.Platform.Identity.Database;
using NickERP.Platform.Identity.Entities;

namespace NickERP.Platform.Identity.Api;

/// <summary>
/// Wires the Identity admin REST surface — user / scope / service-token CRUD.
/// Hosts call <see cref="MapNickErpIdentityAdmin(IEndpointRouteBuilder)"/>
/// after <c>UseAuthentication()</c> + <c>UseAuthorization()</c>. Every
/// endpoint is gated by the <c>Identity.Admin</c> scope (mirrored as a role
/// by <see cref="NickErpAuthenticationHandler"/>).
/// </summary>
public static class IdentityAdminEndpoints
{
    /// <summary>The single scope code that gates the entire admin surface.</summary>
    public const string AdminScope = "Identity.Admin";

    /// <summary>
    /// Map all Identity admin endpoints under <c>/api/identity</c>. Returns
    /// the underlying <see cref="RouteGroupBuilder"/> so callers can attach
    /// extra behaviour (rate limits, OpenAPI tags, additional metadata).
    /// </summary>
    public static RouteGroupBuilder MapNickErpIdentityAdmin(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/identity")
            .RequireAuthorization(p => p.AddAuthenticationSchemes(CfAccessAuthenticationOptions.SchemeName)
                                        .RequireRole(AdminScope))
            .WithTags("Identity Admin");

        MapUsers(group.MapGroup("/users").WithTags("Identity Admin · Users"));
        MapScopes(group.MapGroup("/scopes").WithTags("Identity Admin · Scopes"));
        MapServiceTokens(group.MapGroup("/service-tokens").WithTags("Identity Admin · Service Tokens"));

        return group;
    }

    // -----------------------------------------------------------------------
    // Helpers shared across the three sub-groups
    // -----------------------------------------------------------------------

    /// <summary>Pull the canonical user id of the caller out of the principal claims.</summary>
    private static Guid CallerId(ClaimsPrincipal user)
    {
        var raw = user.FindFirst(NickErpClaims.Id)?.Value;
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    private static int ClampPage(int page) => page < 1 ? 1 : page;
    private static int ClampPageSize(int size) => size switch { < 1 => 25, > 200 => 200, _ => size };

    // -----------------------------------------------------------------------
    // Users
    // -----------------------------------------------------------------------

    private static void MapUsers(RouteGroupBuilder users)
    {
        users.MapGet("/", async (
            IdentityDbContext db,
            int? page,
            int? pageSize,
            string? q,
            long? tenantId,
            CancellationToken ct) =>
        {
            var p = ClampPage(page ?? 1);
            var s = ClampPageSize(pageSize ?? 25);

            IQueryable<IdentityUser> query = db.Users.AsNoTracking().Include(u => u.Scopes);
            if (tenantId is not null)
                query = query.Where(u => u.TenantId == tenantId.Value);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var needle = q.Trim().ToUpperInvariant();
                query = query.Where(u =>
                    u.NormalizedEmail.Contains(needle) ||
                    (u.DisplayName != null && u.DisplayName.ToUpper().Contains(needle)));
            }

            var total = await query.LongCountAsync(ct);
            var items = await query
                .OrderBy(u => u.NormalizedEmail)
                .Skip((p - 1) * s)
                .Take(s)
                .Select(u => MapUser(u))
                .ToListAsync(ct);

            return Results.Ok(new PagedResult<UserDto>(items, p, s, total));
        });

        users.MapGet("/{id:guid}", async (Guid id, IdentityDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking()
                .Include(u => u.Scopes)
                .FirstOrDefaultAsync(u => u.Id == id, ct);
            return user is null ? Results.NotFound() : Results.Ok(MapUser(user));
        });

        users.MapPost("/", async (
            CreateUserRequest req,
            IdentityDbContext db,
            ClaimsPrincipal caller,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            if (!MiniValidate(req, out var problem))
                return Results.ValidationProblem(problem);

            var normalised = req.Email.Trim().ToUpperInvariant();
            var existing = await db.Users
                .Where(u => u.NormalizedEmail == normalised && u.TenantId == req.TenantId)
                .Select(u => u.Id)
                .FirstOrDefaultAsync(ct);
            if (existing != Guid.Empty)
                return Results.Conflict(new { message = "User with this email already exists for the tenant.", id = existing });

            var now = clock.GetUtcNow();
            var user = new IdentityUser
            {
                Email = req.Email.Trim().ToLowerInvariant(),
                NormalizedEmail = normalised,
                DisplayName = req.DisplayName,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
                TenantId = req.TenantId
            };
            db.Users.Add(user);

            if (req.InitialScopes is { Count: > 0 })
            {
                var grantor = CallerId(caller);
                foreach (var code in req.InitialScopes.Select(c => c?.Trim()).Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    db.UserScopes.Add(new UserScope
                    {
                        IdentityUserId = user.Id,
                        AppScopeCode = code!,
                        GrantedAt = now,
                        GrantedByUserId = grantor,
                        TenantId = req.TenantId
                    });
                }
            }

            await db.SaveChangesAsync(ct);
            await db.Entry(user).Collection(u => u.Scopes).LoadAsync(ct);
            return Results.Created($"/api/identity/users/{user.Id}", MapUser(user));
        });

        users.MapPatch("/{id:guid}/scopes", async (
            Guid id,
            UpdateUserScopesRequest req,
            IdentityDbContext db,
            ClaimsPrincipal caller,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var user = await db.Users.Include(u => u.Scopes).FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();

            var now = clock.GetUtcNow();
            var grantor = CallerId(caller);

            // Revoke first (so a revoke + re-grant in the same request lands cleanly).
            foreach (var code in (req.Revoke ?? Array.Empty<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var open = user.Scopes.FirstOrDefault(s =>
                    s.RevokedAt is null &&
                    s.AppScopeCode.Equals(code, StringComparison.OrdinalIgnoreCase));
                if (open is null) continue;
                open.RevokedAt = now;
                open.RevokedByUserId = grantor;
            }

            foreach (var code in (req.Grant ?? Array.Empty<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var alreadyOpen = user.Scopes.Any(s =>
                    s.RevokedAt is null &&
                    s.AppScopeCode.Equals(code, StringComparison.OrdinalIgnoreCase));
                if (alreadyOpen) continue;

                db.UserScopes.Add(new UserScope
                {
                    IdentityUserId = user.Id,
                    AppScopeCode = code,
                    GrantedAt = now,
                    GrantedByUserId = grantor,
                    ExpiresAt = req.ExpiresAt,
                    Notes = req.Notes,
                    TenantId = user.TenantId
                });
            }

            user.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await db.Entry(user).Collection(u => u.Scopes).LoadAsync(ct);
            return Results.Ok(MapUser(user));
        });

        users.MapDelete("/{id:guid}", async (
            Guid id,
            IdentityDbContext db,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();
            if (!user.IsActive) return Results.NoContent(); // already deprovisioned, idempotent
            user.IsActive = false;
            user.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // -----------------------------------------------------------------------
    // App scopes
    // -----------------------------------------------------------------------

    private static void MapScopes(RouteGroupBuilder scopes)
    {
        scopes.MapGet("/", async (IdentityDbContext db, long? tenantId, string? app, CancellationToken ct) =>
        {
            IQueryable<AppScope> q = db.AppScopes.AsNoTracking();
            if (tenantId is not null) q = q.Where(s => s.TenantId == tenantId.Value);
            if (!string.IsNullOrWhiteSpace(app)) q = q.Where(s => s.AppName == app);

            var items = await q.OrderBy(s => s.AppName).ThenBy(s => s.Code)
                .Select(s => new AppScopeDto(s.Id, s.Code, s.AppName, s.Description, s.IsActive, s.CreatedAt, s.TenantId))
                .ToListAsync(ct);
            return Results.Ok(items);
        });

        scopes.MapPost("/", async (
            CreateAppScopeRequest req,
            IdentityDbContext db,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            if (!MiniValidate(req, out var problem))
                return Results.ValidationProblem(problem);

            var existing = await db.AppScopes
                .Where(s => s.TenantId == req.TenantId && s.Code == req.Code)
                .Select(s => s.Id)
                .FirstOrDefaultAsync(ct);
            if (existing != Guid.Empty)
                return Results.Conflict(new { message = "Scope with this code already exists for the tenant.", id = existing });

            var scope = new AppScope
            {
                Code = req.Code,
                AppName = req.AppName,
                Description = req.Description,
                IsActive = true,
                CreatedAt = clock.GetUtcNow(),
                TenantId = req.TenantId
            };
            db.AppScopes.Add(scope);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/identity/scopes/{scope.Id}",
                new AppScopeDto(scope.Id, scope.Code, scope.AppName, scope.Description, scope.IsActive, scope.CreatedAt, scope.TenantId));
        });

        // Soft-retire a scope. Existing user_scope rows keep the historical
        // grant; the resolver will exclude them from the active set because
        // the AppScope.IsActive=false.
        scopes.MapDelete("/{id:guid}", async (Guid id, IdentityDbContext db, CancellationToken ct) =>
        {
            var scope = await db.AppScopes.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (scope is null) return Results.NotFound();
            if (!scope.IsActive) return Results.NoContent();
            scope.IsActive = false;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // -----------------------------------------------------------------------
    // Service tokens
    // -----------------------------------------------------------------------

    private static void MapServiceTokens(RouteGroupBuilder tokens)
    {
        tokens.MapGet("/", async (IdentityDbContext db, long? tenantId, CancellationToken ct) =>
        {
            IQueryable<ServiceTokenIdentity> q = db.ServiceTokens.AsNoTracking().Include(t => t.Scopes);
            if (tenantId is not null) q = q.Where(t => t.TenantId == tenantId.Value);

            var items = await q.OrderBy(t => t.DisplayName)
                .Select(t => MapServiceToken(t))
                .ToListAsync(ct);
            return Results.Ok(items);
        });

        tokens.MapPost("/", async (
            CreateServiceTokenRequest req,
            IdentityDbContext db,
            ClaimsPrincipal caller,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            if (!MiniValidate(req, out var problem))
                return Results.ValidationProblem(problem);

            var existing = await db.ServiceTokens
                .Where(t => t.TenantId == req.TenantId && t.TokenClientId == req.TokenClientId)
                .Select(t => t.Id)
                .FirstOrDefaultAsync(ct);
            if (existing != Guid.Empty)
                return Results.Conflict(new { message = "Service token with this client id already exists.", id = existing });

            var now = clock.GetUtcNow();
            var token = new ServiceTokenIdentity
            {
                TokenClientId = req.TokenClientId.Trim(),
                DisplayName = req.DisplayName.Trim(),
                Purpose = req.Purpose,
                IsActive = true,
                CreatedAt = now,
                ExpiresAt = req.ExpiresAt,
                TenantId = req.TenantId
            };
            db.ServiceTokens.Add(token);

            if (req.InitialScopes is { Count: > 0 })
            {
                var grantor = CallerId(caller);
                foreach (var code in req.InitialScopes
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    db.ServiceTokenScopes.Add(new ServiceTokenScope
                    {
                        ServiceTokenIdentityId = token.Id,
                        AppScopeCode = code,
                        GrantedAt = now,
                        GrantedByUserId = grantor,
                        TenantId = req.TenantId
                    });
                }
            }

            await db.SaveChangesAsync(ct);
            await db.Entry(token).Collection(t => t.Scopes).LoadAsync(ct);
            return Results.Created($"/api/identity/service-tokens/{token.Id}", MapServiceToken(token));
        });

        tokens.MapPatch("/{id:guid}/scopes", async (
            Guid id,
            UpdateServiceTokenScopesRequest req,
            IdentityDbContext db,
            ClaimsPrincipal caller,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var token = await db.ServiceTokens.Include(t => t.Scopes).FirstOrDefaultAsync(t => t.Id == id, ct);
            if (token is null) return Results.NotFound();

            var now = clock.GetUtcNow();
            var grantor = CallerId(caller);

            foreach (var code in (req.Revoke ?? Array.Empty<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var open = token.Scopes.FirstOrDefault(s =>
                    s.RevokedAt is null &&
                    s.AppScopeCode.Equals(code, StringComparison.OrdinalIgnoreCase));
                if (open is null) continue;
                open.RevokedAt = now;
                open.RevokedByUserId = grantor;
            }

            foreach (var code in (req.Grant ?? Array.Empty<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var alreadyOpen = token.Scopes.Any(s =>
                    s.RevokedAt is null &&
                    s.AppScopeCode.Equals(code, StringComparison.OrdinalIgnoreCase));
                if (alreadyOpen) continue;

                db.ServiceTokenScopes.Add(new ServiceTokenScope
                {
                    ServiceTokenIdentityId = token.Id,
                    AppScopeCode = code,
                    GrantedAt = now,
                    GrantedByUserId = grantor,
                    ExpiresAt = req.ExpiresAt,
                    TenantId = token.TenantId
                });
            }

            await db.SaveChangesAsync(ct);
            await db.Entry(token).Collection(t => t.Scopes).LoadAsync(ct);
            return Results.Ok(MapServiceToken(token));
        });

        tokens.MapDelete("/{id:guid}", async (Guid id, IdentityDbContext db, CancellationToken ct) =>
        {
            var token = await db.ServiceTokens.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (token is null) return Results.NotFound();
            if (!token.IsActive) return Results.NoContent();
            token.IsActive = false;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // -----------------------------------------------------------------------
    // Mappers
    // -----------------------------------------------------------------------

    private static UserDto MapUser(IdentityUser u) => new(
        Id: u.Id,
        Email: u.Email,
        DisplayName: u.DisplayName,
        IsActive: u.IsActive,
        CreatedAt: u.CreatedAt,
        UpdatedAt: u.UpdatedAt,
        LastSeenAt: u.LastSeenAt,
        TenantId: u.TenantId,
        Scopes: u.Scopes
            .OrderBy(s => s.AppScopeCode)
            .Select(s => new UserScopeDto(
                s.Id, s.AppScopeCode, s.GrantedAt, s.GrantedByUserId,
                s.ExpiresAt, s.RevokedAt, s.RevokedByUserId, s.Notes))
            .ToList());

    private static ServiceTokenDto MapServiceToken(ServiceTokenIdentity t) => new(
        Id: t.Id,
        TokenClientId: t.TokenClientId,
        DisplayName: t.DisplayName,
        Purpose: t.Purpose,
        IsActive: t.IsActive,
        CreatedAt: t.CreatedAt,
        LastSeenAt: t.LastSeenAt,
        ExpiresAt: t.ExpiresAt,
        TenantId: t.TenantId,
        Scopes: t.Scopes
            .OrderBy(s => s.AppScopeCode)
            .Select(s => new ServiceTokenScopeDto(
                s.Id, s.AppScopeCode, s.GrantedAt, s.GrantedByUserId,
                s.ExpiresAt, s.RevokedAt, s.RevokedByUserId))
            .ToList());

    /// <summary>
    /// Tiny DataAnnotations runner. The full ASP.NET Core minimal-API
    /// validation pipeline isn't enabled by default (it requires a
    /// separate middleware in 10.0); this keeps the dependency surface
    /// small while still catching the obvious mistakes.
    /// </summary>
    private static bool MiniValidate(object instance, out IDictionary<string, string[]> problem)
    {
        var ctx = new System.ComponentModel.DataAnnotations.ValidationContext(instance, null, null);
        var errors = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        if (System.ComponentModel.DataAnnotations.Validator.TryValidateObject(instance, ctx, errors, validateAllProperties: true))
        {
            problem = new Dictionary<string, string[]>();
            return true;
        }
        problem = errors
            .SelectMany(e => (e.MemberNames.Any() ? e.MemberNames : new[] { string.Empty })
                .Select(m => new { Member = m, Message = e.ErrorMessage ?? "Invalid." }))
            .GroupBy(x => x.Member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Message).ToArray());
        return false;
    }
}
