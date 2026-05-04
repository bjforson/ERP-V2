using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NickERP.Inspection.Application.Submissions;

/// <summary>
/// DI helpers for the Sprint 22 / B2.1 submission-queue admin surface.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="IcumsSubmissionQueueAdminService"/> as scoped.
    /// Idempotent (TryAddScoped). Used by the
    /// <c>/admin/icums/submission-queue</c> Razor page and any direct
    /// caller (test fixture, future API endpoint) that needs the same
    /// list / requeue / payload-fetch surface.
    /// </summary>
    public static IServiceCollection AddIcumsSubmissionQueueAdmin(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IcumsSubmissionQueueAdminService>();
        return services;
    }
}
