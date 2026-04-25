using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace NickERP.Platform.Logging;

/// <summary>
/// Standard NickERP Serilog wiring. Every service should call
/// <see cref="UseNickErpLogging"/> at startup so logs flow uniformly into
/// Seq (primary), a per-service rolling file (fallback), and the console
/// (dev). Enrichers add <c>ServiceName</c>, <c>MachineName</c>,
/// <c>ProcessId</c>, and <c>CorrelationId</c> automatically so cross-service
/// traces are searchable in Seq without per-service convention drift.
/// </summary>
public static class NickErpLoggingExtensions
{
    /// <summary>Default Seq endpoint on TEST-SERVER. Override via configuration <c>NickErp:Logging:SeqUrl</c>.</summary>
    public const string DefaultSeqUrl = "http://localhost:5341";

    /// <summary>Default file-sink root on TEST-SERVER. Override via configuration <c>NickErp:Logging:FileRoot</c>.</summary>
    public const string DefaultFileRoot = @"C:\Shared\Logs";

    /// <summary>
    /// Register Serilog as the host's logging provider with NickERP conventions.
    /// </summary>
    /// <param name="builder">The host builder (works with <c>WebApplication</c>, <c>Host.CreateApplicationBuilder</c>, etc.).</param>
    /// <param name="serviceName">Stable identifier for the service — appears in every log entry as <c>ServiceName</c>. Use the deployable unit name (e.g. <c>NSCIM.API</c>, <c>NickHR.WebApp</c>).</param>
    /// <returns>The same builder for chaining.</returns>
    public static IHostApplicationBuilder UseNickErpLogging(
        this IHostApplicationBuilder builder,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var seqUrl = builder.Configuration["NickErp:Logging:SeqUrl"] ?? DefaultSeqUrl;
        var fileRoot = builder.Configuration["NickErp:Logging:FileRoot"] ?? DefaultFileRoot;
        var minLevel = ParseLevel(builder.Configuration["NickErp:Logging:MinimumLevel"]);
        var seqApiKey = builder.Configuration["NickErp:Logging:SeqApiKey"];

        builder.Services.AddSerilog((sp, config) =>
        {
            config
                .ReadFrom.Configuration(builder.Configuration)
                .MinimumLevel.Is(minLevel)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithMachineName()
                .Enrich.WithProcessId()
                .Enrich.WithThreadId()
                .Enrich.WithProperty("ServiceName", serviceName)
                .Enrich.With(new CorrelationIdEnricher())
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {ServiceName} {CorrelationId} {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(
                    path: Path.Combine(fileRoot, serviceName, "log-.txt"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {ServiceName} {CorrelationId} {SourceContext} {Message:lj}{NewLine}{Exception}")
                .WriteTo.Seq(
                    serverUrl: seqUrl,
                    apiKey: string.IsNullOrWhiteSpace(seqApiKey) ? null : seqApiKey,
                    restrictedToMinimumLevel: LogEventLevel.Verbose);
        });

        return builder;
    }

    private static LogEventLevel ParseLevel(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return LogEventLevel.Information;
        return Enum.TryParse<LogEventLevel>(configured, ignoreCase: true, out var lvl)
            ? lvl
            : LogEventLevel.Information;
    }
}
