using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NickERP.Platform.Observability;

/// <summary>
/// Wires the platform-standard OpenTelemetry stack into a host. One call from
/// each app's <c>Program.cs</c> — no per-app boilerplate, no per-app exporter
/// configuration.
/// </summary>
public static class OpenTelemetryExtensions
{
    /// <summary>
    /// Wires OpenTelemetry traces + metrics with NickERP defaults: ASP.NET Core +
    /// HttpClient + Runtime instrumentation, OTLP exporter to
    /// <c>NickERP:Observability:OtlpEndpoint</c> (default <c>http://localhost:4317</c>),
    /// service name = <paramref name="appName"/> with version from the entry
    /// assembly when not supplied, parent-based always-on sampler. Adds
    /// Npgsql tracing automatically (best-effort).
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="appName">Logical service name (e.g. <c>NickFinance.WebApp</c>).</param>
    /// <param name="appVersion">Optional; falls back to entry assembly version.</param>
    public static IHostApplicationBuilder AddNickErpObservability(
        this IHostApplicationBuilder builder,
        string appName,
        string? appVersion = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);

        var resolvedVersion = appVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? "0.0.0";

        var cfg = builder.Configuration;
        var tracingEnabled = cfg.GetValue("NickERP:Observability:Tracing:Enabled", true);
        var metricsEnabled = cfg.GetValue("NickERP:Observability:Metrics:Enabled", true);
        var otlpEndpoint = cfg["NickERP:Observability:OtlpEndpoint"] ?? "http://localhost:4317";

        var resourceBuilder = ResourceBuilder
            .CreateDefault()
            .AddService(serviceName: appName, serviceVersion: resolvedVersion)
            .AddAttributes(new KeyValuePair<string, object>[]
            {
                new("nickerp.app.name", appName),
                new("nickerp.app.version", resolvedVersion),
            });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(rb => rb
                .AddService(serviceName: appName, serviceVersion: resolvedVersion)
                .AddAttributes(new KeyValuePair<string, object>[]
                {
                    new("nickerp.app.name", appName),
                    new("nickerp.app.version", resolvedVersion),
                }));

        if (tracingEnabled)
        {
            otel.WithTracing(t =>
            {
                t.SetSampler(new ParentBasedSampler(new AlwaysOnSampler()));
                t.AddAspNetCoreInstrumentation();
                t.AddHttpClientInstrumentation();

                // Best-effort: Npgsql tracing. The package reference is direct,
                // but if some future consuming app strips it the try/catch
                // keeps observability silent rather than crashing startup.
                try
                {
                    t.AddNpgsql();
                }
                catch
                {
                    // Npgsql instrumentation is best-effort.
                }

                t.AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(otlpEndpoint);
                });
            });
        }

        if (metricsEnabled)
        {
            otel.WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation();
                m.AddHttpClientInstrumentation();
                m.AddRuntimeInstrumentation();
                m.AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(otlpEndpoint);
                });

                // Prometheus scraping endpoint reuses the same MeterProvider.
                // The /metrics route is mapped separately by MapNickErpMetrics.
                m.AddPrometheusExporter();
            });
        }

        // Stash the resource builder so other components (e.g. Serilog) can
        // pick up the same service identity if they wish.
        builder.Services.AddSingleton(resourceBuilder);

        return builder;
    }
}
