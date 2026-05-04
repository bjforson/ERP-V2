namespace NickERP.Portal.Services.Modules;

/// <summary>
/// Sprint 29 — write-side service for the per-tenant module enable/disable
/// flag. Backs the module-toggle card on <c>TenantDetail.razor</c>; the
/// platform admin flips a module on or off for one tenant via this
/// surface. Read access goes through <see cref="IModuleRegistry"/>.
/// </summary>
public interface ITenantModuleSettingsService
{
    /// <summary>
    /// Set the enabled flag for one (tenant, module) pair. Upserts into
    /// <c>tenancy.tenant_module_settings</c>: creates the row if it
    /// doesn't exist, otherwise updates <c>Enabled</c> +
    /// <c>UpdatedAt</c> + <c>UpdatedByUserId</c>.
    /// </summary>
    /// <returns>The post-write entity (after re-fetch).</returns>
    Task<TenantModuleSettingDto> SetEnabledAsync(
        long tenantId,
        string moduleId,
        bool enabled,
        Guid? actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// All override rows for one tenant. Used by
    /// <see cref="IModuleRegistry.GetModulesForTenantAsync"/>; admin UIs
    /// can also call it directly. Empty list ⇒ tenant inherits the
    /// registry defaults for every module.
    /// </summary>
    Task<IReadOnlyList<TenantModuleSettingDto>> GetSettingsForTenantAsync(
        long tenantId,
        CancellationToken ct = default);
}

/// <summary>
/// Sprint 29 — DTO for the module-settings service. Mirrors the
/// <c>TenantModuleSetting</c> entity but doesn't leak the EF-tracked
/// instance to the Razor page.
/// </summary>
public sealed record TenantModuleSettingDto(
    long Id,
    long TenantId,
    string ModuleId,
    bool Enabled,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedByUserId);
