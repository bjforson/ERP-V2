namespace NickERP.Platform.Identity.Auth;

/// <summary>
/// Stable claim type names used by the NickERP identity layer. Modules read
/// <c>HttpContext.User.FindFirst(NickErpClaims.X)?.Value</c>; these strings
/// MUST stay stable across versions.
/// </summary>
public static class NickErpClaims
{
    /// <summary>Canonical id (<see cref="System.Guid"/> as string). Sources: <see cref="Entities.IdentityUser.Id"/> for users, <see cref="Entities.ServiceTokenIdentity.Id"/> for service tokens.</summary>
    public const string Id = "nickerp:id";

    /// <summary>Lowercased email. Empty for service tokens.</summary>
    public const string Email = "nickerp:email";

    /// <summary>Display name.</summary>
    public const string DisplayName = "nickerp:display_name";

    /// <summary>Tenant id (long as string) the caller is scoped to.</summary>
    public const string TenantId = "nickerp:tenant_id";

    /// <summary><c>true</c> when the principal represents a service token.</summary>
    public const string IsServiceToken = "nickerp:is_service_token";

    /// <summary>One claim per active <see cref="Entities.AppScope.Code"/> the caller has been granted.</summary>
    public const string Scope = "nickerp:scope";

    /// <summary>The original CF Access JWT <c>sub</c> claim, preserved for debugging / audit.</summary>
    public const string ExternalSubject = "nickerp:external_sub";
}
