using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NickERP.Inspection.Core.Completeness;

namespace NickERP.Inspection.Application.Completeness;

/// <summary>
/// Sprint 31 / B5.1 — DI helpers for the vendor-neutral completeness
/// engine.
///
/// <para>
/// Hosts call <see cref="AddNickErpInspectionCompleteness"/> to register
/// the engine as scoped + the built-in requirements. Adapter projects
/// (CustomsGh, NigeriaCustoms, etc.) add their own requirements through
/// their own <c>ServiceCollectionExtensions</c> — same plugin pattern
/// the Sprint 28 ValidationEngine uses.
/// </para>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the vendor-neutral completeness engine + its built-in
    /// requirements. Idempotent (TryAdd*) — calling twice is a no-op.
    /// </summary>
    /// <remarks>
    /// Hosts must additionally register an
    /// <see cref="ICompletenessRequirementProvider"/>; the production
    /// wiring uses <see cref="DbCompletenessRequirementProvider"/>
    /// backed by <c>TenancyDbContext</c>. Tests typically register
    /// <see cref="InMemoryCompletenessRequirementProvider"/>.
    /// </remarks>
    public static IServiceCollection AddNickErpInspectionCompleteness(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<CompletenessChecker>();
        services.TryAddScoped<ICompletenessChecker>(sp => sp.GetRequiredService<CompletenessChecker>());

        // Built-in vendor-neutral requirements — every host gets these
        // by default. Adapter / plugin requirements add themselves
        // separately.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICompletenessRequirement, RequiredScanArtifactRequirement>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICompletenessRequirement, RequiredCustomsDeclarationRequirement>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICompletenessRequirement, RequiredAnalystDecisionRequirement>());
        // Sprint 36 / FU-completeness-percent-requirements — first
        // percent-based built-in. Reads tenant_completeness_settings.MinThreshold;
        // defaults to 0.85 (= 85%) when no tenant override exists.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICompletenessRequirement, RequiredImageCoverageRequirement>());

        return services;
    }

    /// <summary>
    /// Register the production
    /// <see cref="DbCompletenessRequirementProvider"/>. Hosts that
    /// already wired it (e.g. via tests) can skip this; it's
    /// idempotent.
    /// </summary>
    public static IServiceCollection AddNickErpInspectionCompletenessDbProvider(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<ICompletenessRequirementProvider, DbCompletenessRequirementProvider>();
        return services;
    }
}
