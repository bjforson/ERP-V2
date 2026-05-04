namespace NickERP.Portal.Services.Modules;

/// <summary>
/// Sprint 29 — read-side abstraction over the portal's module catalogue.
/// The launcher page consumes this; the per-tenant settings card on
/// <c>TenantDetail.razor</c> consumes the write-side
/// (<see cref="ITenantModuleSettingsService"/>).
/// </summary>
public interface IModuleRegistry
{
    /// <summary>
    /// All known modules with config-driven URLs. Static across requests;
    /// reflects only the configuration shape.
    /// </summary>
    IReadOnlyList<ModuleRegistryEntry> GetAllModules();

    /// <summary>
    /// Modules visible to the supplied tenant — applies the per-tenant
    /// <c>tenant_module_settings</c> overrides on top of
    /// <see cref="GetAllModules"/>. Modules with no override row inherit
    /// the registry default (Enabled = true).
    /// </summary>
    /// <param name="tenantId">Tenant id from the caller's
    /// <c>nickerp:tenant_id</c> claim.</param>
    /// <param name="includeDisabled">When true, returns disabled rows
    /// too (admin tooling). Default false — the launcher only wants the
    /// enabled set.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ModuleRegistryEntry>> GetModulesForTenantAsync(
        long tenantId,
        bool includeDisabled = false,
        CancellationToken ct = default);
}
