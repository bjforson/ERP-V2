using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Identity;

namespace NickFinance.WebApp.Identity;

/// <summary>
/// EF-backed <see cref="IPermissionService"/> with per-request caching
/// in <see cref="HttpContext.Items"/>. Mirror of NSCIM's
/// <c>PermissionService</c> shape, simplified — NickFinance does NOT
/// support user-specific permission overrides, only role grants.
/// </summary>
public sealed class PermissionService : IPermissionService
{
    private const string CacheItemKeyPrefix = "NickFinance.Identity.Permissions:";

    private readonly IdentityDbContext _db;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<PermissionService> _logger;
    private readonly TimeProvider _clock;

    public PermissionService(
        IdentityDbContext db,
        ILogger<PermissionService> logger,
        IHttpContextAccessor? httpContextAccessor = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<bool> HasPermissionAsync(Guid userId, string permissionName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(permissionName))
        {
            return false;
        }

        var permissions = await GetPermissionsForUserAsync(userId, ct);
        // Case-sensitive — the seeded permission names are stable lower-case
        // strings (e.g. "petty.voucher.approve"); call sites reference them
        // via the Permissions.X constants.
        return permissions.Contains(permissionName);
    }

    public async Task<IReadOnlyList<string>> GetPermissionsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        // Per-request cache: a Razor page that hits five [Authorize] gates
        // (e.g. a master-detail page with multiple permission-gated
        // partials) does ONE round-trip per request, not five.
        var cacheKey = CacheItemKeyPrefix + userId.ToString("N");
        var ctx = _httpContextAccessor?.HttpContext;
        if (ctx is not null && ctx.Items.TryGetValue(cacheKey, out var cached) && cached is IReadOnlyList<string> cachedList)
        {
            return cachedList;
        }

        IReadOnlyList<string> result;
        try
        {
            var now = _clock.GetUtcNow();
            // user_roles → roles → role_permissions → permissions
            // Filter expired grants; distinct permission names.
            var rows = await _db.UserRoles
                .Where(ur => ur.UserId == userId && (ur.ExpiresAt == null || ur.ExpiresAt > now))
                .Join(_db.Set<RolePermission>(), ur => ur.RoleId, rp => rp.RoleId, (ur, rp) => rp.PermissionId)
                .Join(_db.Set<Permission>(), pid => pid, p => p.PermissionId, (pid, p) => p.Name)
                .Distinct()
                .ToListAsync(ct);

            result = rows;
            _logger.LogDebug(
                "[PERMISSION-SERVICE] User {UserId} has {Count} permissions via role grants.",
                userId, result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[PERMISSION-SERVICE] Error loading permissions for user {UserId}: {Message}. Failing closed (zero permissions).",
                userId, ex.Message);
            // Fail-closed: a DB error returns zero permissions, never the
            // full bundle. Better to surface 403 than to grant accidentally.
            result = Array.Empty<string>();
        }

        if (ctx is not null)
        {
            ctx.Items[cacheKey] = result;
        }
        return result;
    }
}
