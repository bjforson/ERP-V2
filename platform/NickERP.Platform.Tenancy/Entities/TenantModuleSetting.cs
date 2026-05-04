namespace NickERP.Platform.Tenancy.Entities;

/// <summary>
/// Sprint 29 — per-tenant on/off flag for one of the v2 portal's modules
/// (inspection, nickfinance, nickhr). Drives the launcher tile grid: only
/// modules whose row is <see cref="Enabled"/> for the caller's tenant get
/// rendered.
/// </summary>
/// <remarks>
/// <para>
/// Tenant-scoped (<see cref="ITenantOwned"/>) and lives under the same
/// <c>tenancy</c> schema as the canonical tenants table so RLS can isolate
/// rows by <c>app.tenant_id</c>. The <c>(TenantId, ModuleId)</c> pair is
/// unique — at most one row per module per tenant.
/// </para>
/// <para>
/// Module activation is a platform-admin action; ordinary users only read
/// rows for their own tenant via the registry service.
/// </para>
/// </remarks>
public sealed class TenantModuleSetting : ITenantOwned
{
    /// <summary>Surrogate primary key. Long because every row is small + cheap.</summary>
    public long Id { get; set; }

    /// <summary>
    /// Owning tenant id. Stamped by <c>TenantOwnedEntityInterceptor</c> on
    /// insert; never overwritten on update. RLS narrows reads/writes to
    /// the caller's tenant via <c>app.tenant_id</c>.
    /// </summary>
    public long TenantId { get; set; }

    /// <summary>
    /// Stable module id — matches <c>ModuleRegistryEntry.Id</c> in the
    /// portal's <c>IModuleRegistry</c>. Lowercase ascii (e.g.
    /// <c>"inspection"</c>, <c>"nickfinance"</c>, <c>"nickhr"</c>).
    /// </summary>
    public string ModuleId { get; set; } = string.Empty;

    /// <summary>
    /// True ⇒ module tile renders for this tenant; false ⇒ tile suppressed.
    /// Default true so newly-created modules light up unless an admin
    /// explicitly opts out.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Wallclock of the last toggle. Set by the registry service whenever
    /// <see cref="Enabled"/> flips. Useful for audit reports + the
    /// per-tenant module-toggle card on <c>TenantDetail.razor</c>.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Identity user id of the operator who last toggled this row. Null
    /// until the first manual override (the seeded default rows have no
    /// originating actor).
    /// </summary>
    public Guid? UpdatedByUserId { get; set; }
}
