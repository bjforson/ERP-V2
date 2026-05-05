using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NickERP.Inspection.Application.Sla;

/// <summary>
/// Sprint 31 / B5.1 — DI helpers for the SLA tracker. Hosts call
/// <see cref="AddNickErpInspectionSla"/> at startup; the engine binds
/// <see cref="SlaTrackerOptions"/> from the
/// <c>Inspection:Sla</c> configuration section.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the vendor-neutral SLA tracker + the in-memory
    /// settings provider. Hosts must additionally call
    /// <see cref="AddNickErpInspectionSlaDbProvider"/> to wire the
    /// production Postgres-backed settings provider; tests can rely on
    /// the in-memory variant they wire themselves.
    /// </summary>
    public static IServiceCollection AddNickErpInspectionSla(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<SlaTracker>();
        services.TryAddScoped<ISlaTracker>(sp => sp.GetRequiredService<SlaTracker>());

        if (configuration is not null)
            services.Configure<SlaTrackerOptions>(configuration.GetSection(SlaTrackerOptions.SectionName));
        else
            services.AddOptions<SlaTrackerOptions>();

        return services;
    }

    /// <summary>
    /// Register the Postgres-backed
    /// <see cref="DbSlaSettingsProvider"/>. Idempotent — calling more
    /// than once is a no-op.
    /// </summary>
    public static IServiceCollection AddNickErpInspectionSlaDbProvider(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<ISlaSettingsProvider, DbSlaSettingsProvider>();
        return services;
    }
}
