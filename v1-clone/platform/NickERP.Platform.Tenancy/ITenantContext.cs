namespace NickERP.Platform.Tenancy;

/// <summary>
/// Per-request tenant context. Resolved from the JWT <c>tenant_id</c> claim
/// by <see cref="TenantResolutionMiddleware"/>, or set explicitly by background
/// jobs that need to impersonate a tenant.
///
/// Modules NEVER read this from configuration — only from DI. The contract
/// is: anything that has business data has a tenant; anything without a
/// resolved tenant context throws on access.
/// </summary>
public interface ITenantContext
{
    /// <summary>The current tenant id, or <see cref="Tenant.DefaultTenantId"/> if not set.</summary>
    long TenantId { get; }

    /// <summary>True if a tenant has been explicitly resolved on this request/scope.</summary>
    bool IsResolved { get; }

    /// <summary>
    /// True if the current principal is a platform-level superuser (can impersonate
    /// any tenant for support purposes). Set by the JWT claim <c>platform_admin</c>.
    /// </summary>
    bool IsPlatformAdmin { get; }

    /// <summary>
    /// Sets the active tenant id. Used by background jobs and the
    /// <c>TenantSwitcher</c> impersonation flow. Throws if not allowed.
    /// </summary>
    void SetTenant(long tenantId);
}

/// <summary>
/// Default in-process implementation. Scoped per request in DI.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private long _tenantId = Tenant.DefaultTenantId;

    public long TenantId => _tenantId;
    public bool IsResolved { get; private set; }
    public bool IsPlatformAdmin { get; set; }

    public void SetTenant(long tenantId)
    {
        if (tenantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tenantId), "TenantId must be positive.");
        _tenantId = tenantId;
        IsResolved = true;
    }
}
