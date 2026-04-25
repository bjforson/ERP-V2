using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NickERP.Platform.Telemetry;

/// <summary>
/// Standard NickERP OpenTelemetry wiring. Every service should call
/// <see cref="UseNickErpTelemetry"/> at startup so traces, metrics, and the
/// resource attributes (<c>service.name</c>, <c>service.version</c>,
/// <c>deployment.environment</c>) flow uniformly into Seq's OTLP receiver
/// (or any OTel-compatible backend) and into the console for local dev.
/// </summary>
/// <remarks>
/// Auto-instrumented out of the box:
/// <list type="bullet">
///   <item><description>ASP.NET Core inbound HTTP requests</description></item>
///   <item><description>HttpClient outbound calls</description></item>
///   <item><description>.NET runtime metrics (GC, threads, exceptions, memory)</description></item>
/// </list>
/// EF Core / Npgsql instrumentation is the consuming service's concern —
/// add the relevant package + <c>.AddSource(...)</c> on the tracer if needed.
/// </remarks>
public static class NickErpTelemetryExtensions
{
    /// <summary>Default OTLP endpoint — Seq's built-in OTLP receiver on TEST-SERVER.</summary>
    public const string DefaultOtlpEndpoint = "http://localhost:5341/ingest/otlp";

    /// <summary>The canonical NickERP <see cref="ActivitySource"/> name. Use it when manually creating spans inside any module.</summary>
    public const string DefaultActivitySource = "NickERP";

    /// <summary>The canonical NickERP <see cref="System.Diagnostics.Metrics.Meter"/> name.</summary>
    public const string DefaultMeter = "NickERP";

    /// <summary>
    /// Register OpenTelemetry tracing + metrics with NickERP conventions.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="serviceName">Stable service identifier (matches <c>UseNickErpLogging</c>).</param>
    /// <param name="additionalActivitySources">Extra <see cref="ActivitySource"/> names this service emits — the platform Source <c>NickERP</c> is always added.</param>
    /// <param name="additionalMeters">Extra <see cref="System.Diagnostics.Metrics.Meter"/> names this service emits — the platform Meter <c>NickERP</c> is always added.</param>
    /// <returns>The same builder for chaining.</returns>
    public static IHostApplicationBuilder UseNickErpTelemetry(
        this IHostApplicationBuilder builder,
        string serviceName,
        IEnumerable<string>? additionalActivitySources = null,
        IEnumerable<string>? additionalMeters = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var otlpEndpoint = builder.Configuration["NickErp:Telemetry:OtlpEndpoint"] ?? DefaultOtlpEndpoint;
        var consoleExporter = builder.Configuration.GetValue("NickErp:Telemetry:ConsoleExporter", builder.Environment.IsDevelopment());
        var environment = builder.Configuration["NickErp:Telemetry:Environment"] ?? builder.Environment.EnvironmentName;
        var serviceVersion = builder.Configuration["NickErp:Telemetry:ServiceVersion"]
            ?? typeof(NickErpTelemetryExtensions).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";
        var otlpHeaders = builder.Configuration["NickErp:Telemetry:OtlpHeaders"];

        var sources = new[] { DefaultActivitySource }
            .Concat(additionalActivitySources ?? Array.Empty<string>())
            .Distinct()
            .ToArray();

        var meters = new[] { DefaultMeter }
            .Concat(additionalMeters ?? Array.Empty<string>())
            .Distinct()
            .ToArray();

        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(serviceName: serviceName, serviceVersion: serviceVersion)
            .AddAttributes(new KeyValuePair<string, object>[]
            {
                new("deployment.environment", environment),
                new("nickerp.service", serviceName)
            });

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(rb => rb
                .AddService(serviceName: serviceName, serviceVersion: serviceVersion)
                .AddAttributes(new KeyValuePair<string, object>[]
                {
                    new("deployment.environment", environment),
                    new("nickerp.service", serviceName)
                }))
            .WithTracing(t =>
            {
                t.AddSource(sources);
                t.AddAspNetCoreInstrumentation();
                t.AddHttpClientInstrumentation();
                t.AddOtlpExporter(opts => ConfigureOtlp(opts, otlpEndpoint, otlpHeaders, "/v1/traces"));
                if (consoleExporter)
                {
                    t.AddConsoleExporter();
                }
            })
            .WithMetrics(m =>
            {
                m.AddMeter(meters);
                m.AddAspNetCoreInstrumentation();
                m.AddHttpClientInstrumentation();
                m.AddRuntimeInstrumentation();
                m.AddOtlpExporter(opts => ConfigureOtlp(opts, otlpEndpoint, otlpHeaders, "/v1/metrics"));
                if (consoleExporter)
                {
                    m.AddConsoleExporter();
                }
            });

        return builder;
    }

    /// <summary>
    /// Configure an OTLP exporter for a specific signal. The OTel .NET SDK does
    /// NOT auto-append signal paths when the Endpoint is set explicitly (only
    /// when reading from <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>), so we build the
    /// per-signal URL here. Detects an already-suffixed URL and leaves it alone.
    /// </summary>
    private static void ConfigureOtlp(
        OpenTelemetry.Exporter.OtlpExporterOptions opts,
        string endpoint,
        string? headers,
        string signalPath)
    {
        var trimmed = endpoint.TrimEnd('/');
        var fullUri = trimmed.EndsWith(signalPath, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + signalPath;
        opts.Endpoint = new Uri(fullUri);
        opts.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
        if (!string.IsNullOrWhiteSpace(headers))
        {
            opts.Headers = headers;
        }
    }
}
