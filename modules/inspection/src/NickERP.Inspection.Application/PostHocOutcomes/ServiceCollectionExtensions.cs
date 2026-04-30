using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NickERP.Inspection.Application.PostHocOutcomes;

/// <summary>
/// DI helpers for the post-hoc outcome ingestion pipeline (§6.11).
/// Hosts wire this from <c>Program.cs</c> to register
/// <see cref="OutcomeIngestionOptions"/> + supporting application
/// services. The <c>OutcomePullWorker</c> itself lives in
/// <c>NickERP.Inspection.Web</c> (BackgroundService is host-shaped) and
/// is registered there.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Bind <see cref="OutcomeIngestionOptions"/> from the
    /// <c>PostHocOutcomes:</c> config section and register the scoped
    /// <see cref="IPostHocOutcomeWriter"/>. Idempotent — calling twice
    /// re-binds (last wins) but doesn't double-register the writer
    /// because <c>TryAddScoped</c> is used.
    /// </summary>
    public static IServiceCollection AddPostHocOutcomeIngestion(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<OutcomeIngestionOptions>(
            configuration.GetSection("PostHocOutcomes"));

        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
            .TryAddScoped<IPostHocOutcomeWriter, PostHocOutcomeWriter>(services);

        return services;
    }
}
