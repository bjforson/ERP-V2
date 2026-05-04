using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NickERP.Inspection.Application.ExternalSystems;

/// <summary>
/// Sprint 16 — DI helpers for <see cref="ExternalSystemAdminService"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="ExternalSystemAdminService"/> as scoped. Used
    /// by the admin Razor page (<c>/external-systems</c>) and any
    /// programmatic caller that needs to register or look up external
    /// system instances. Idempotent (TryAddScoped).
    /// </summary>
    public static IServiceCollection AddExternalSystemAdmin(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<ExternalSystemAdminService>();
        return services;
    }
}
