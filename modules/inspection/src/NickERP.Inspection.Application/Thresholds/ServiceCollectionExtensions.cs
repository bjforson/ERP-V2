using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace NickERP.Inspection.Application.Thresholds;

/// <summary>
/// One-line wiring for the per-scanner threshold calibration subsystem
/// (§6.5). Hosts call
/// <c>builder.Services.AddScannerThresholdCalibration(builder.Configuration)</c>
/// in <c>Program.cs</c>.
///
/// <para>
/// Registers a single <see cref="ScannerThresholdResolver"/> instance
/// resolved into three DI slots — <see cref="IScannerThresholdResolver"/>
/// (hot path), <see cref="IHostedService"/> (LISTEN loop), and the
/// optional <see cref="IHealthCheck"/>. ONE worker instance is the
/// invariant: if <c>AddHostedService&lt;T&gt;()</c> alone were used,
/// the host would create a separate copy and the resolver lookups
/// would never see the cache evictions.
/// </para>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Wires <see cref="IScannerThresholdResolver"/>, the LISTEN/NOTIFY hosted service, and a health-check entry.</summary>
    public static IServiceCollection AddScannerThresholdCalibration(
        this IServiceCollection services,
        IConfiguration configuration,
        string healthCheckName = "scanner-threshold-resolver",
        string healthCheckTag = "ready")
    {
        services.AddOptions<ScannerThresholdOptions>()
            .Bind(configuration.GetSection(ScannerThresholdOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<ScannerThresholdResolver>();
        services.AddSingleton<IScannerThresholdResolver>(sp =>
            sp.GetRequiredService<ScannerThresholdResolver>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<ScannerThresholdResolver>());

        services.AddSingleton<ScannerThresholdResolverHealthCheck>();
        services.AddHealthChecks()
            .AddCheck<ScannerThresholdResolverHealthCheck>(
                name: healthCheckName,
                failureStatus: HealthStatus.Degraded,
                tags: new[] { healthCheckTag });

        return services;
    }
}
