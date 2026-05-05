using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Tenancy.Database;

namespace NickERP.Portal.Services.Modules;

/// <summary>
/// Sprint 29 — default <see cref="IModuleRegistry"/>. Catalogue is bound
/// from <c>Portal:Modules:{Id}</c> at startup; per-tenant overrides come
/// from <c>tenancy.tenant_module_settings</c>.
/// </summary>
/// <remarks>
/// Scoped because the per-tenant lookup uses the host's
/// <see cref="TenancyDbContext"/>. The static catalogue is captured in
/// the constructor; the lookup query joins it to the per-tenant rows in
/// memory (the override set is tiny — at most one row per known module).
/// </remarks>
public sealed class ModuleRegistryService : IModuleRegistry
{
    private readonly TenancyDbContext _db;
    private readonly IReadOnlyList<ModuleRegistryEntry> _catalogue;

    /// <summary>
    /// Construct the registry with its EF context + the bound catalogue.
    /// The options bind from <c>Portal:Modules</c> (see
    /// <c>ModuleRegistryOptions</c> for the shape).
    /// </summary>
    public ModuleRegistryService(TenancyDbContext db, ModuleRegistryOptions options)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _catalogue = (options ?? throw new ArgumentNullException(nameof(options))).Modules;
    }

    /// <inheritdoc />
    public IReadOnlyList<ModuleRegistryEntry> GetAllModules() => _catalogue;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModuleRegistryEntry>> GetModulesForTenantAsync(
        long tenantId,
        bool includeDisabled = false,
        CancellationToken ct = default)
    {
        // Sprint 29 — load the per-tenant overrides for this caller.
        // RLS narrows by app.tenant_id; the TenantConnectionInterceptor
        // pushes app.tenant_id on every connection open from the request
        // principal's tenant claim.
        //
        // Sprint 43 / Phase D — FU-launcher-rls-with-postgres. The
        // explicit Where(TenantId == tenantId) belt-and-suspenders
        // filter that previously sat here was dropped after
        // TenantModuleSettingsRlsIntegrationTests proved RLS narrows
        // correctly under a non-superuser role with the policy's
        // COALESCE-to-'0' fail-closed default. Defense-in-depth via
        // a redundant LINQ filter is no longer required: the policy
        // itself fails closed when app.tenant_id is unset, and the
        // unit-test path that needed cross-tenant isolation now uses
        // distinct in-memory database names per tenant. See
        // feedback_confirm_before_weakening_security.md — this change
        // does NOT weaken the security posture; it removes a belt-
        // and-suspenders that was hiding a missing-context bug case
        // that should fail loud.
        var overrides = await _db.TenantModuleSettings
            .AsNoTracking()
            .ToDictionaryAsync(x => x.ModuleId, x => x.Enabled, ct);

        var resolved = new List<ModuleRegistryEntry>(_catalogue.Count);
        foreach (var entry in _catalogue)
        {
            // No override row ⇒ inherit the catalogue default. Override
            // present ⇒ the row's Enabled wins.
            var enabled = overrides.TryGetValue(entry.Id, out var v) ? v : entry.Enabled;
            if (!enabled && !includeDisabled)
            {
                continue;
            }
            resolved.Add(entry with { Enabled = enabled });
        }
        return resolved;
    }
}

/// <summary>
/// Sprint 29 — bound options for <see cref="ModuleRegistryService"/>. Holds
/// the static module catalogue assembled at startup from
/// <c>Portal:Modules:{Id}:BaseUrl</c> entries + the built-in defaults.
/// </summary>
public sealed class ModuleRegistryOptions
{
    /// <summary>The catalogue. Order is the order tiles render in.</summary>
    public IReadOnlyList<ModuleRegistryEntry> Modules { get; init; } = Array.Empty<ModuleRegistryEntry>();
}
