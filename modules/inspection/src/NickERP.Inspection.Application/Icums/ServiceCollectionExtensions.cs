using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NickERP.Inspection.Application.Icums;

/// <summary>DI helpers for the Sprint 22 / B2.3 ICUMS dashboard sub-pages.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the dashboard + sub-page admin services
    /// (<see cref="IcumsDashboardService"/>,
    /// <see cref="IcumsLooseCargoService"/>,
    /// <see cref="IcumsBoeLookupService"/>) as scoped. Idempotent.
    /// </summary>
    public static IServiceCollection AddIcumsDashboardSuite(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IcumsDashboardService>();
        services.TryAddScoped<IcumsLooseCargoService>();
        services.TryAddScoped<IcumsBoeLookupService>();
        return services;
    }
}
