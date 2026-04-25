namespace NickERP.Platform.Identity.Entities;

/// <summary>
/// A named, app-specific permission. One row per (app, scope) pair seeded
/// at module install time (e.g. "HR" registers <c>HR.Admin</c>,
/// <c>HR.Manager</c>, <c>HR.Employee</c>). Apps consult the user's
/// resolved scope set via <c>IIdentityResolver</c> (shipped in A.2.5);
/// they do not invent or modify scopes at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Code"/> is the stable identifier referenced in code paths
/// and policy YAML. Conventionally <c>{App}.{Scope}</c> with PascalCase
/// segments — <c>NSCIM.Analyst</c>, <c>Finance.PettyCash.Approver</c>.
/// </para>
/// <para>
/// Scopes are global to a tenant — there is no per-site or per-project
/// granularity here. Apps that need finer authorisation handle it
/// internally (e.g. NickHR scopes a manager to their own dept;
/// NickFinance scopes an approver to a specific float).
/// </para>
/// </remarks>
public class AppScope
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable code, e.g. "Finance.PettyCash.Approver". Unique within tenant.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>The owning app, e.g. "Finance" or "NSCIM". Indexed for app-filtered queries.</summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>Human-friendly explanation surfaced in admin UIs.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// <c>false</c> when an app retires a scope. Existing
    /// <see cref="UserScope"/> rows remain for audit but the resolver
    /// will not include them in the active set.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public long TenantId { get; set; } = 1;
}
