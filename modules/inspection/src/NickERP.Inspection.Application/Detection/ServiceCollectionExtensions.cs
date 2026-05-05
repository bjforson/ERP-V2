using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NickERP.Inspection.Application.Detection;

/// <summary>
/// Sprint 31 / B5.2 — DI helpers for the cross-record-scan detector.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the built-in detector + the contract. Idempotent.
    /// Adapter projects can ship additional <see cref="ICrossRecordScanDetector"/>
    /// implementations (e.g. CustomsGh-aware ones); the
    /// <c>CrossRecordScanService</c> resolves <c>IEnumerable&lt;ICrossRecordScanDetector&gt;</c>
    /// so multiple detectors can vote on the same case.
    /// </summary>
    public static IServiceCollection AddNickErpInspectionDetection(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICrossRecordScanDetector, CrossRecordScanDetector>());
        return services;
    }
}
