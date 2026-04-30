using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace NickERP.Platform.Observability;

/// <summary>
/// Replaces the default host logger with a NickERP-standard Serilog pipeline.
/// </summary>
public static class LoggingExtensions
{
    /// <summary>
    /// Replaces the host's default logger with Serilog: structured-JSON
    /// to a configured directory (rolling daily, 14-day retention) plus
    /// console for dev (human-readable). In Production, console uses
    /// Compact JSON so the Windows Service event-stream is parseable by
    /// log shippers.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="appName">Logical service name; flows into <c>AppName</c> log property.</param>
    public static IHostApplicationBuilder AddNickErpLogging(
        this IHostApplicationBuilder builder,
        string appName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);

        var appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";

        var defaultLogDir = Path.Combine("C:\\Logs\\NickERP", appName);
        var logDir = builder.Configuration["NickERP:Logging:Directory"] ?? defaultLogDir;

        // Best-effort directory creation — never fail startup over a log dir.
        try
        {
            Directory.CreateDirectory(logDir);
        }
        catch (Exception ex)
        {
            // Fall back to console-only — surface the reason once on startup.
            Console.Error.WriteLine($"[NickErpLogging] Could not create log dir '{logDir}': {ex.Message}. Logging to console only.");
            logDir = string.Empty;
        }

        var isDevelopment = builder.Environment.IsDevelopment();

        var cfg = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithProcessId()
            .Enrich.WithProcessName()
            .Enrich.WithEnvironmentName()
            .Enrich.WithThreadId()
            .Enrich.WithProperty("AppName", appName)
            .Enrich.WithProperty("AppVersion", appVersion);

        if (isDevelopment)
        {
            cfg.WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        }
        else
        {
            cfg.WriteTo.Console(new CompactJsonFormatter());
        }

        if (!string.IsNullOrEmpty(logDir))
        {
            var pattern = Path.Combine(logDir, $"{appName}-.log");
            cfg.WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: pattern,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true);
        }

        Log.Logger = cfg.CreateLogger();

        // Replace the default ILoggerFactory with Serilog's. dispose=true so
        // the logger flushes on host shutdown.
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: true);

        return builder;
    }
}
