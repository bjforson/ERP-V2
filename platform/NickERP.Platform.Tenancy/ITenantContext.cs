namespace NickERP.Platform.Tenancy;

/// <summary>
/// Per-request handle to the current tenant. Resolved by
/// <see cref="TenantResolutionMiddleware"/> from the authenticated principal's
/// <c>nickerp:tenant_id</c> claim and consumed by EF Core interceptors,
/// query filters, and module business code.
/// </summary>
public interface ITenantContext
{
    /// <summary>Tenant id. <c>0</c> when <see cref="IsResolved"/> is false (no active tenant).</summary>
    long TenantId { get; }

    /// <summary><c>true</c> after <see cref="SetTenant"/> has run; <c>false</c> before resolution or for unauthenticated requests.</summary>
    bool IsResolved { get; }

    /// <summary>Replace the current tenant. Called by the resolution middleware (or by background jobs that explicitly impersonate).</summary>
    void SetTenant(long tenantId);
}

/// <summary>
/// Default <see cref="ITenantContext"/> — a per-request scoped service.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public long TenantId { get; private set; }
    public bool IsResolved { get; private set; }

    public void SetTenant(long tenantId)
    {
        if (tenantId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tenantId), "TenantId must be positive.");
        }
        TenantId = tenantId;
        IsResolved = true;
    }
}
