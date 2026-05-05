using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NickERP.Inspection.Application.Reviews;

/// <summary>
/// Sprint 34 / B6 — DI extension for the specialised-review workflow.
/// Registers <see cref="IReviewWorkflow"/> as scoped (matches the
/// CaseWorkflowService posture) so it captures the per-request
/// InspectionDbContext + ITenantContext.
///
/// <para>
/// Hosts that drive the BL / AI-triage / Audit review pages call
/// <see cref="AddNickErpInspectionReviews"/>; idempotent
/// (TryAddScoped under the hood). The
/// <see cref="NickERP.Inspection.Application.Sla.ISlaTracker"/>
/// dependency is OPTIONAL — if the host has not registered the
/// tracker, the workflow logs a warning and proceeds without
/// opening / closing windows.
/// </para>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the specialised-review workflow service. Idempotent.
    /// Inspection.Web's Program.cs calls this alongside
    /// <c>AddNickErpInspectionSla</c> + <c>AddNickErpInspectionValidation</c>.
    /// </summary>
    public static IServiceCollection AddNickErpInspectionReviews(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IReviewWorkflow, ReviewWorkflow>();
        return services;
    }
}
