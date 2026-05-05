using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 44 / Phase A — DI helpers for <see cref="RetentionService"/>
/// + the Phase B <see cref="RetentionEnforcerWorker"/>.
///
/// <para>
/// <c>AddNickErpInspectionRetention</c> registers the scoped
/// <see cref="RetentionService"/> (matches CaseWorkflowService /
/// RulesAdminService / ReportsService posture — captures per-request
/// <c>InspectionDbContext</c> + <c>ITenantContext</c>). The
/// <c>RetentionEnforcerWorker</c> is registered directly in
/// Program.cs alongside the other singleton hosted services
/// (Sprint 36 SlaStateRefresherWorker pattern); this extension stays
/// scoped-only so it can be invoked safely from
/// <c>Inspection.Web/Program.cs</c> + from any future host that wants
/// just the admin-side service without the worker.
/// </para>
///
/// <para>
/// Idempotent — uses <see cref="ServiceCollectionDescriptorExtensions.TryAdd"/>
/// shapes so a second call is a no-op.
/// </para>
/// </summary>
public static class RetentionServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="RetentionService"/> as scoped. Idempotent.
    /// Hosts wishing to expose the <c>/admin/retention</c> + purge-
    /// candidate pages call this in <c>Program.cs</c> alongside
    /// <c>AddNickErpInspectionReviews</c>.
    /// </summary>
    public static IServiceCollection AddNickErpInspectionRetention(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<RetentionService>();
        return services;
    }
}
