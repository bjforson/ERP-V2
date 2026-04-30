using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;

namespace NickFinance.WebApp.Identity;

/// <summary>
/// Read-side projection of role grants for an internal user. Authorization
/// handlers consult this at every gated request; the service caches its
/// result per HTTP request inside <see cref="HttpContext.Items"/> so a
/// page that hits five guarded endpoints does one DB roundtrip, not five.
/// </summary>
public interface IRoleService
{
    /// <summary>Roles directly granted to the user (any site).</summary>
    Task<IReadOnlyList<string>> GetRolesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// True if the user has the role. When <paramref name="siteId"/> is
    /// supplied, matches either a tenant-wide grant (NULL site) OR a grant
    /// specifically for that site. When null, matches any grant.
    /// </summary>
    Task<bool> HasRoleAsync(Guid userId, string roleName, Guid? siteId = null, CancellationToken ct = default);

    /// <summary>
    /// Grant a role to a user. <paramref name="grantedByUserId"/> is the
    /// admin doing the grant; the audit log captures the pair. Idempotent —
    /// a duplicate grant returns the existing row.
    /// </summary>
    Task GrantRoleAsync(Guid userId, string roleName, Guid grantedByUserId, Guid? siteId = null, DateTimeOffset? expiresAt = null, CancellationToken ct = default);

    /// <summary>
    /// Revoke an existing grant. No-op if the grant doesn't exist.
    /// </summary>
    Task RevokeRoleAsync(Guid userId, string roleName, Guid? siteId = null, CancellationToken ct = default);
}

/// <summary>
/// EF-backed implementation. Returns role names (the string), not role
/// ids, because policies are written against names — the smallint id is
/// strictly an FK / index optimisation.
/// </summary>
public sealed class RoleService : IRoleService
{
    private readonly IdentityDbContext _db;
    private readonly ISecurityAuditService? _audit;
    private readonly TimeProvider _clock;

    public RoleService(IdentityDbContext db, ISecurityAuditService? audit = null, TimeProvider? clock = null)
    {
        _db = db;
        _audit = audit;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<string>> GetRolesAsync(Guid userId, CancellationToken ct = default)
    {
        // Filter by user; expired grants don't count (NULL = never expires).
        var now = _clock.GetUtcNow();
        var rows = await _db.UserRoles
            .Where(ur => ur.UserId == userId && (ur.ExpiresAt == null || ur.ExpiresAt > now))
            .Join(_db.Roles, ur => ur.RoleId, r => r.RoleId, (ur, r) => r.Name)
            .Distinct()
            .ToListAsync(ct);
        return rows;
    }

    public async Task<bool> HasRoleAsync(Guid userId, string roleName, Guid? siteId = null, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var query = _db.UserRoles
            .Where(ur => ur.UserId == userId && (ur.ExpiresAt == null || ur.ExpiresAt > now))
            .Join(_db.Roles, ur => ur.RoleId, r => r.RoleId, (ur, r) => new { ur.SiteId, r.Name })
            .Where(x => x.Name == roleName);

        if (siteId is not null)
        {
            // Site-scoped check: tenant-wide grant (SiteId == null) OR grant for this exact site.
            query = query.Where(x => x.SiteId == null || x.SiteId == siteId);
        }
        return await query.AnyAsync(ct);
    }

    public async Task GrantRoleAsync(Guid userId, string roleName, Guid grantedByUserId, Guid? siteId = null, DateTimeOffset? expiresAt = null, CancellationToken ct = default)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Name == roleName, ct)
            ?? throw new InvalidOperationException($"Role '{roleName}' is not seeded; run the bootstrap CLI.");

        // Idempotency: an active grant with the same (user, role, site) shape is a no-op.
        var existing = await _db.UserRoles.FirstOrDefaultAsync(
            ur => ur.UserId == userId && ur.RoleId == role.RoleId && ur.SiteId == siteId, ct);
        if (existing is not null)
        {
            // Refresh expiry if the new grant is longer.
            if (expiresAt is not null && (existing.ExpiresAt is null || existing.ExpiresAt < expiresAt))
            {
                existing.ExpiresAt = expiresAt;
                await _db.SaveChangesAsync(ct);
            }
            return;
        }

        _db.UserRoles.Add(new UserRole
        {
            UserId = userId,
            RoleId = role.RoleId,
            SiteId = siteId,
            GrantedByUserId = grantedByUserId,
            GrantedAt = _clock.GetUtcNow(),
            ExpiresAt = expiresAt
        });
        await _db.SaveChangesAsync(ct);

        if (_audit is not null)
        {
            await _audit.RecordAsync(
                action: SecurityAuditAction.RoleGranted,
                targetType: "User",
                targetId: userId.ToString(),
                result: SecurityAuditResult.Allowed,
                details: new { role = roleName, siteId, expiresAt },
                ct: ct);
        }
    }

    public async Task RevokeRoleAsync(Guid userId, string roleName, Guid? siteId = null, CancellationToken ct = default)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Name == roleName, ct);
        if (role is null) return;

        var grant = await _db.UserRoles.FirstOrDefaultAsync(
            ur => ur.UserId == userId && ur.RoleId == role.RoleId && ur.SiteId == siteId, ct);
        if (grant is null) return;

        _db.UserRoles.Remove(grant);
        await _db.SaveChangesAsync(ct);

        if (_audit is not null)
        {
            await _audit.RecordAsync(
                action: SecurityAuditAction.RoleRevoked,
                targetType: "User",
                targetId: userId.ToString(),
                result: SecurityAuditResult.Allowed,
                details: new { role = roleName, siteId },
                ct: ct);
        }
    }
}
