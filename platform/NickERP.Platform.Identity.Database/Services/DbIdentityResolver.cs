using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Identity.Auth;
using NickERP.Platform.Identity.Entities;
using NickERP.Platform.Identity.Services;

namespace NickERP.Platform.Identity.Database.Services;

/// <summary>
/// Postgres-backed default <see cref="IIdentityResolver"/>. Lives next to the
/// DbContext so the EF model is reused. Implements the IDENTITY.md contract:
/// reads the inbound headers, validates JWT (when present), looks up the
/// canonical record, returns a <see cref="ResolvedIdentity"/> or <see langword="null"/>.
/// </summary>
internal sealed class DbIdentityResolver : IIdentityResolver
{
    private readonly IdentityDbContext _db;
    private readonly ICfJwtValidator _jwt;
    private readonly CfAccessAuthenticationOptions _options;
    private readonly IHostEnvironment _env;
    private readonly ILogger<DbIdentityResolver> _logger;
    private readonly TimeProvider _clock;

    public DbIdentityResolver(
        IdentityDbContext db,
        ICfJwtValidator jwt,
        CfAccessAuthenticationOptions options,
        IHostEnvironment env,
        ILogger<DbIdentityResolver> logger,
        TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _jwt = jwt ?? throw new ArgumentNullException(nameof(jwt));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ResolvedIdentity?> ResolveAsync(HttpContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // 1. Dev bypass (Development only, gated at startup).
        if (_options.DevBypass.Enabled && _env.IsDevelopment())
        {
            var devEmail = ctx.Request.Headers[_options.DevBypass.TriggerHeader].ToString();
            if (string.IsNullOrWhiteSpace(devEmail)) devEmail = _options.DevBypass.FakeUserEmail;
            if (!string.IsNullOrWhiteSpace(devEmail))
            {
                var devUser = await FindUserByEmailAsync(devEmail, ct);
                if (devUser is not null)
                {
                    return await BuildUserIdentityAsync(devUser, externalSubject: null, ct);
                }
                _logger.LogWarning(
                    "Dev bypass requested email {Email} but no active IdentityUser exists. Provision the user first.",
                    devEmail);
                return null;
            }
        }

        // 2. CF Access JWT (or Authorization: Bearer fallback).
        var token = ctx.Request.Headers["Cf-Access-Jwt-Assertion"].ToString();
        if (string.IsNullOrEmpty(token) && _options.AcceptAuthorizationHeaderFallback)
        {
            var authHeader = ctx.Request.Headers.Authorization.ToString();
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = authHeader.Substring("Bearer ".Length).Trim();
            }
        }

        if (string.IsNullOrEmpty(token)) return null;

        var principal = await _jwt.ValidateAsync(token, ct);
        if (principal is null) return null;

        var sub = principal.FindFirst("sub")?.Value;
        var email = principal.FindFirst("email")?.Value;

        // 3. Service token first — the JWT 'sub' equals TokenClientId for service-token JWTs.
        if (!string.IsNullOrEmpty(sub))
        {
            var serviceToken = await FindServiceTokenAsync(sub, ct);
            if (serviceToken is not null)
            {
                return await BuildServiceTokenIdentityAsync(serviceToken, sub, ct);
            }
        }

        // 4. Human user by email.
        if (!string.IsNullOrEmpty(email))
        {
            var user = await FindUserByEmailAsync(email, ct);
            if (user is not null)
            {
                return await BuildUserIdentityAsync(user, externalSubject: sub, ct);
            }
            _logger.LogInformation(
                "Validated CF Access JWT for {Email} (sub={Sub}) but no active IdentityUser. Provision via /api/identity/users.",
                email, sub);
        }

        return null;
    }

    private Task<IdentityUser?> FindUserByEmailAsync(string email, CancellationToken ct)
    {
        var normalized = email.Trim().ToUpperInvariant();
        return _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized && u.IsActive, ct);
    }

    private Task<ServiceTokenIdentity?> FindServiceTokenAsync(string clientId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return _db.ServiceTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenClientId == clientId
                && t.IsActive
                && (t.ExpiresAt == null || t.ExpiresAt > now), ct);
    }

    private async Task<ResolvedIdentity> BuildUserIdentityAsync(IdentityUser user, string? externalSubject, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        // Active, non-revoked, non-expired user scopes joined against active app_scopes (within the user's tenant).
        var scopeCodes = await (
            from us in _db.UserScopes.AsNoTracking()
            join s in _db.AppScopes.AsNoTracking()
                on new { Tenant = us.TenantId, Code = us.AppScopeCode }
                equals new { Tenant = s.TenantId, Code = s.Code }
            where us.IdentityUserId == user.Id
                && us.TenantId == user.TenantId
                && us.RevokedAt == null
                && (us.ExpiresAt == null || us.ExpiresAt > now)
                && s.IsActive
            select s.Code
        ).Distinct().ToListAsync(ct);

        // Liveness — fire-and-forget; LastSeenAt is analytics not security.
        _ = TouchLastSeenUserAsync(user.Id);

        return new ResolvedIdentity(
            Id: user.Id,
            Email: user.Email,
            DisplayName: !string.IsNullOrWhiteSpace(user.DisplayName) ? user.DisplayName! : user.Email,
            IsServiceToken: false,
            TenantId: user.TenantId,
            ScopeCodes: scopeCodes,
            ExternalSubject: externalSubject);
    }

    private async Task<ResolvedIdentity> BuildServiceTokenIdentityAsync(ServiceTokenIdentity token, string externalSubject, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var scopeCodes = await (
            from ts in _db.ServiceTokenScopes.AsNoTracking()
            join s in _db.AppScopes.AsNoTracking()
                on new { Tenant = ts.TenantId, Code = ts.AppScopeCode }
                equals new { Tenant = s.TenantId, Code = s.Code }
            where ts.ServiceTokenIdentityId == token.Id
                && ts.TenantId == token.TenantId
                && ts.RevokedAt == null
                && (ts.ExpiresAt == null || ts.ExpiresAt > now)
                && s.IsActive
            select s.Code
        ).Distinct().ToListAsync(ct);

        _ = TouchLastSeenServiceTokenAsync(token.Id);

        return new ResolvedIdentity(
            Id: token.Id,
            Email: null,
            DisplayName: token.DisplayName,
            IsServiceToken: true,
            TenantId: token.TenantId,
            ScopeCodes: scopeCodes,
            ExternalSubject: externalSubject);
    }

    private async Task TouchLastSeenUserAsync(Guid userId)
    {
        try
        {
            var now = _clock.GetUtcNow();
            await _db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(u => u.LastSeenAt, now)
                    .SetProperty(u => u.UpdatedAt, now));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update LastSeenAt for IdentityUser {UserId}", userId);
        }
    }

    private async Task TouchLastSeenServiceTokenAsync(Guid tokenId)
    {
        try
        {
            var now = _clock.GetUtcNow();
            await _db.ServiceTokens
                .Where(t => t.Id == tokenId)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.LastSeenAt, now));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update LastSeenAt for ServiceTokenIdentity {TokenId}", tokenId);
        }
    }
}
