namespace NickERP.Platform.Tenancy;

/// <summary>
/// Per-request handle to the current tenant. Resolved by
/// <see cref="TenantResolutionMiddleware"/> from the authenticated principal's
/// <c>nickerp:tenant_id</c> claim and consumed by EF Core interceptors,
/// query filters, and module business code.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// Tenant id. <c>0</c> when <see cref="IsResolved"/> is false (no active tenant).
    /// When <see cref="IsSystem"/> is <c>true</c>, this is the sentinel <c>-1</c>.
    /// </summary>
    long TenantId { get; }

    /// <summary><c>true</c> after <see cref="SetTenant"/> or <see cref="SetSystemContext"/> has run; <c>false</c> before resolution or for unauthenticated requests.</summary>
    bool IsResolved { get; }

    /// <summary>
    /// <c>true</c> when <see cref="SetSystemContext"/> has been called and the
    /// context is operating in cross-tenant system mode; <c>false</c> in
    /// regular tenant-scoped requests. Calling <see cref="SetTenant"/> after
    /// <see cref="SetSystemContext"/> reverts this back to <c>false</c>.
    /// </summary>
    bool IsSystem { get; }

    /// <summary>
    /// Replace the current tenant. Called by the resolution middleware (or by
    /// background jobs that explicitly impersonate). Clears <see cref="IsSystem"/>
    /// — a context that was previously in system mode returns to regular
    /// tenant-scoped operation.
    /// </summary>
    void SetTenant(long tenantId);

    /// <summary>
    /// Switch this context into system mode. The <see cref="TenantConnectionInterceptor"/>
    /// will push the sentinel <c>app.tenant_id = '-1'</c> on connection open;
    /// RLS policies that opt in to system access (via
    /// <c>OR current_setting('app.tenant_id') = '-1'</c>) will allow
    /// cross-tenant reads / NULL-tenant writes. After <see cref="SetSystemContext"/>,
    /// <see cref="IsResolved"/> is <c>true</c> and <see cref="TenantId"/> is <c>-1</c>.
    /// </summary>
    /// <remarks>
    /// Every call site MUST be registered in
    /// <c>docs/system-context-audit-register.md</c>. Reviewed at every sprint
    /// boundary by the rolling master and at every security review by the user.
    /// </remarks>
    void SetSystemContext();
}

/// <summary>
/// Default <see cref="ITenantContext"/> — a per-request scoped service.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    /// <summary>The sentinel session value pushed to Postgres in system mode. Not a row-data value.</summary>
    public const long SystemSentinel = -1L;

    private bool _isSystem;

    public long TenantId { get; private set; }
    public bool IsResolved { get; private set; }
    public bool IsSystem => _isSystem;

    public void SetTenant(long tenantId)
    {
        if (tenantId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tenantId), "TenantId must be positive.");
        }
        TenantId = tenantId;
        IsResolved = true;
        // Switching to a concrete tenant clears system mode — a single
        // scoped context that calls SetSystemContext() then later
        // SetTenant(2) ends up in tenant 2's regular scope.
        _isSystem = false;
    }

    /// <inheritdoc />
    public void SetSystemContext()
    {
        _isSystem = true;
        TenantId = SystemSentinel;
        IsResolved = true;
    }
}
