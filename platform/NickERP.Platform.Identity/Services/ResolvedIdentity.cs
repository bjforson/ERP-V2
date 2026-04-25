namespace NickERP.Platform.Identity.Services;

/// <summary>
/// The shape app code receives back from
/// <see cref="IIdentityResolver.ResolveAsync(Microsoft.AspNetCore.Http.HttpContext, System.Threading.CancellationToken)"/>.
/// Carries the canonical id, the basic display data, the tenant scope, and
/// the active set of <see cref="Entities.AppScope.Code"/> values.
/// </summary>
/// <param name="Id">Canonical identifier — <see cref="Entities.IdentityUser.Id"/> for human callers, <see cref="Entities.ServiceTokenIdentity.Id"/> for service tokens.</param>
/// <param name="Email">Lowercased email for human users; <see langword="null"/> for service tokens.</param>
/// <param name="DisplayName">Display name for UIs and audit trails. Falls back to email for users with no <see cref="Entities.IdentityUser.DisplayName"/>.</param>
/// <param name="IsServiceToken"><see langword="true"/> when the caller is a non-human service token; <see langword="false"/> for a human user.</param>
/// <param name="TenantId">The tenant the caller is scoped to.</param>
/// <param name="ScopeCodes">Active <see cref="Entities.AppScope.Code"/> values granted to the caller. Already filtered for <c>RevokedAt</c> / <c>ExpiresAt</c> / <c>IsActive</c> by the resolver.</param>
/// <param name="ExternalSubject">The original CF Access JWT <c>sub</c> claim, preserved for audit/debugging. <see langword="null"/> when the resolution came from the dev-mode bypass header.</param>
public sealed record ResolvedIdentity(
    Guid Id,
    string? Email,
    string DisplayName,
    bool IsServiceToken,
    long TenantId,
    IReadOnlyList<string> ScopeCodes,
    string? ExternalSubject)
{
    /// <summary>
    /// Case-insensitive scope check. Use this rather than reading
    /// <see cref="ScopeCodes"/> directly so the comparison stays consistent
    /// across modules.
    /// </summary>
    public bool HasScope(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        return ScopeCodes.Contains(code, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True if the caller has any of the given scopes.</summary>
    public bool HasAnyScope(params string[] codes)
    {
        if (codes is null || codes.Length == 0) return false;
        foreach (var code in codes)
        {
            if (HasScope(code)) return true;
        }
        return false;
    }
}
