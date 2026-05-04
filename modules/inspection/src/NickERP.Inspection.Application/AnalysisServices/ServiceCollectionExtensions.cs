using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NickERP.Inspection.Application.AnalysisServices;

/// <summary>
/// DI helpers for VP6 Phase A.5 — auto-bootstrap of the "All
/// Locations" <c>AnalysisService</c> per tenant + auto-join of every
/// new <c>Location</c> to it.
///
/// <para>
/// Hosts that need to <b>create</b> the All Locations service for a
/// tenant (the portal's <c>Tenants.razor</c>) call
/// <see cref="AddAnalysisServiceBootstrap"/>, which registers
/// <see cref="IAnalysisServiceBootstrap"/> as scoped.
/// </para>
///
/// <para>
/// Hosts that <b>insert locations</b> through
/// <c>InspectionDbContext</c> (Inspection.Web's Locations page,
/// scanner-onboarding wizards, test fixtures) call
/// <see cref="AddAnalysisServiceLocationAutoJoinInterceptor"/> which
/// registers the SaveChanges interceptor as singleton — EF picks it up
/// via the registered DbContext options.
/// </para>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="IAnalysisServiceBootstrap"/> as scoped.
    /// Idempotent (TryAddScoped).
    /// </summary>
    public static IServiceCollection AddAnalysisServiceBootstrap(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IAnalysisServiceBootstrap, AnalysisServiceBootstrap>();
        return services;
    }

    /// <summary>
    /// Register the
    /// <see cref="AnalysisServiceLocationAutoJoinInterceptor"/> as
    /// singleton + scoped. The DbContext registration must wire it
    /// into <c>UseNpgsql(...).AddInterceptors(provider =&gt; provider
    /// .GetRequiredService&lt;AnalysisServiceLocationAutoJoinInterceptor&gt;())</c>
    /// to actually take effect on SaveChanges.
    /// </summary>
    public static IServiceCollection AddAnalysisServiceLocationAutoJoinInterceptor(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<AnalysisServiceLocationAutoJoinInterceptor>();
        return services;
    }

    /// <summary>
    /// Phase B — register <see cref="AnalysisServiceAdminService"/> as
    /// scoped. Used by the admin Razor pages
    /// (<c>/admin/analysis-services</c> + <c>/admin/analysis-services/{id}</c>)
    /// and any direct caller that needs CRUD on the VP6 entities.
    /// Idempotent (TryAddScoped).
    /// </summary>
    public static IServiceCollection AddAnalysisServiceAdmin(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<AnalysisServiceAdminService>();
        return services;
    }
}
