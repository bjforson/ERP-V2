using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;

namespace NickFinance.WebApp.Identity;

/// <summary>
/// Resolves a user's full permission bundle from the
/// <c>identity.role_permissions</c> join table. Per-request cached via
/// <see cref="HttpContext.Items"/> so a Razor page hitting five
/// <c>[Authorize(Policy = ...)]</c> gates does ONE database round-trip,
/// not five.
/// </summary>
/// <remarks>
/// <para>
/// Backed by the NSCIM-style permission-claim model: each user has one
/// (active) <see cref="UserRole"/> grant; that grant maps to a row in
/// <c>identity.roles</c>; the role's bundle lives in
/// <c>identity.role_permissions</c>; each row maps to a row in
/// <c>identity.permissions</c>; the <c>name</c> column carries the
/// permission string (e.g. <c>"petty.voucher.approve"</c>).
/// </para>
/// <para>
/// Expired grants (<c>ExpiresAt &lt;= now</c>) DO NOT contribute
/// permissions — the join's <c>WHERE</c> clause filters them out so an
/// ExternalAuditor whose access expired silently flips to zero
/// permissions on their next request without waiting for a cache evict.
/// </para>
/// </remarks>
public interface IPermissionService
{
    /// <summary>
    /// True iff the user has the named permission via any active role
    /// grant. Case-sensitive against the seeded permission names.
    /// </summary>
    Task<bool> HasPermissionAsync(Guid userId, string permissionName, CancellationToken ct = default);

    /// <summary>
    /// All distinct permission names the user holds across every active
    /// role grant. Returned in no particular order; case-sensitive.
    /// </summary>
    Task<IReadOnlyList<string>> GetPermissionsForUserAsync(Guid userId, CancellationToken ct = default);
}
