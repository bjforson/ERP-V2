using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NickERP.Inspection.Core.Validation;

namespace NickERP.Inspection.Application.Validation;

/// <summary>
/// Sprint 28 — DI helpers for the vendor-neutral validation engine.
///
/// <para>
/// Hosts call <see cref="AddNickErpInspectionValidation"/> to register
/// the engine as scoped + the built-in rules. CustomsGh-side or other
/// adapter rules add themselves through their own
/// <c>ServiceCollectionExtensions</c> (each plugin owns its DI for the
/// rules it ships).
/// </para>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the vendor-neutral validation engine + its built-in
    /// rules. Idempotent (TryAdd*) — calling twice is a no-op.
    /// </summary>
    /// <remarks>
    /// Hosts must additionally register an
    /// <see cref="IRuleEnablementProvider"/>; the production wiring uses
    /// <see cref="DbRuleEnablementProvider"/> backed by
    /// <c>TenancyDbContext</c>. Tests typically register
    /// <see cref="InMemoryRuleEnablementProvider"/>.
    /// </remarks>
    public static IServiceCollection AddNickErpInspectionValidation(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<ValidationEngine>();

        // Sprint 48 / Phase B — snapshot reader so /cases/{id} can
        // hydrate the validation pane on cold reload without re-running
        // the engine. Backed by InspectionDbContext; every host that
        // already wires the DbContext gets this for free.
        services.TryAddScoped<IValidationSnapshotReader, DbValidationSnapshotReader>();

        // Built-in vendor-neutral rules — every host gets these by
        // default. Adapter / plugin rules add themselves separately
        // (e.g. AddCustomsGhValidation in the Authorities.CustomsGh
        // assembly).
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IValidationRule, RequiredScanArtifactRule>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IValidationRule, RequiredCustomsDeclarationRule>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IValidationRule, ScanWithinWindowRule>());

        // Default options bind so callers can override via
        // builder.Services.Configure<ScanWithinWindowOptions>(...) without
        // having to register the options type themselves.
        services.AddOptions<ScanWithinWindowOptions>();

        return services;
    }

    /// <summary>
    /// Register the production <see cref="DbRuleEnablementProvider"/>.
    /// Hosts that already wired it (e.g. via tests) can skip this; it's
    /// idempotent.
    /// </summary>
    public static IServiceCollection AddNickErpInspectionValidationDbProvider(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IRuleEnablementProvider, DbRuleEnablementProvider>();
        return services;
    }
}
