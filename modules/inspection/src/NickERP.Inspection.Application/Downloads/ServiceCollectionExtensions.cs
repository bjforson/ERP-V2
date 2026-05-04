using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NickERP.Inspection.Application.Downloads;

/// <summary>DI helpers for the Sprint 22 / B2.2 download-queue admin surface.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="IcumsDownloadQueueAdminService"/> as scoped.
    /// Idempotent (TryAddScoped). Used by the
    /// <c>/admin/icums/download-queue</c> Razor page and any direct
    /// caller (BOE lookup detail, manual-pull flow, dashboard cards).
    /// </summary>
    public static IServiceCollection AddIcumsDownloadQueueAdmin(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IcumsDownloadQueueAdminService>();
        return services;
    }
}
