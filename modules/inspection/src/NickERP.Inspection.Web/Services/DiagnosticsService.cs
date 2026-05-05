using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Telemetry;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 33 / B7.2 — read-only aggregator for the
/// <c>/admin/diagnostics</c> family (health, workers, log viewer
/// pointer). Reads health-check results + every registered
/// <see cref="IBackgroundServiceProbe"/> directly from DI, plus
/// surfaces the configured Seq URL for the log-viewer link-out.
///
/// <para>
/// Vendor-neutral by design: no module-specific assumptions. Every
/// counter / probe surfaces here generically.
/// </para>
///
/// <para>
/// Why no HTTP fetch from <c>/healthz</c>? In v2's Blazor Server host
/// the diagnostics page lives in the same process as the workers — we
/// can resolve the probe collection directly from
/// <see cref="IServiceProvider"/>. Going out over HTTP would buy us
/// nothing and add deploy-environment surprises (mTLS, host header
/// mismatch). For multi-host deployments this service can be extended
/// with an <c>HttpClient</c>-backed adapter later (out of scope for
/// B7).
/// </para>
/// </summary>
public class DiagnosticsService
{
    /// <summary>
    /// Configuration key for the Seq UI URL — when set, the logs page
    /// renders a link-out to Seq instead of attempting an inline log
    /// reader. The browser-facing URL may differ from the
    /// <c>NickErp:Logging:SeqUrl</c> ingestion endpoint (different host
    /// header, reverse proxy in front, etc.) — operators that want a
    /// distinct URL set this key.
    /// </summary>
    public const string SeqUrlConfigKey = "NickErp:Logging:SeqUrl";

    /// <summary>Alternate config key — operators who run Seq behind a public URL distinct from the ingestion endpoint.</summary>
    public const string SeqUiUrlConfigKey = "NickErp:Logging:SeqUiUrl";

    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<DiagnosticsService> _logger;

    public DiagnosticsService(
        IServiceProvider services,
        IConfiguration config,
        ILogger<DiagnosticsService> logger)
    {
        _services = services;
        _config = config;
        _logger = logger;
    }

    // -----------------------------------------------------------------
    // Workers
    // -----------------------------------------------------------------

    /// <summary>
    /// Snapshot every registered <see cref="IBackgroundServiceProbe"/>.
    /// Mirrors the shape served by <c>/healthz/workers</c> but
    /// resolved in-process so the page can render without an HTTP
    /// round-trip.
    /// </summary>
    public IReadOnlyList<WorkerProbeSnapshot> GetWorkerSnapshots()
    {
        var probes = _services.GetServices<IBackgroundServiceProbe>().ToList();
        var rows = new List<WorkerProbeSnapshot>(probes.Count);
        foreach (var p in probes)
        {
            try
            {
                var s = p.GetState();
                rows.Add(new WorkerProbeSnapshot(
                    Name: p.WorkerName,
                    Health: s.Health.ToString(),
                    LastTickAt: s.LastTickAt,
                    LastSuccessAt: s.LastSuccessAt,
                    TickCount: s.TickCount,
                    ErrorCount: s.ErrorCount,
                    LastError: s.LastError,
                    LastErrorAt: s.LastErrorAt));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "DiagnosticsService: failed to snapshot worker {Worker}; surfacing as Unhealthy",
                    p.WorkerName);
                rows.Add(new WorkerProbeSnapshot(
                    Name: p.WorkerName,
                    Health: nameof(BackgroundServiceHealth.Unhealthy),
                    LastTickAt: null,
                    LastSuccessAt: null,
                    TickCount: 0,
                    ErrorCount: 0,
                    LastError: ex.Message,
                    LastErrorAt: DateTimeOffset.UtcNow));
            }
        }
        return rows
            .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Aggregate verdict across every probe. Any Unhealthy → Unhealthy;
    /// else any Degraded → Degraded; else Healthy. Empty probe set →
    /// Healthy (nothing to wedge on).
    /// </summary>
    public string GetOverallWorkerHealth(IEnumerable<WorkerProbeSnapshot> snapshots)
    {
        var states = snapshots.Select(s => s.Health).ToList();
        if (states.Count == 0) return nameof(BackgroundServiceHealth.Healthy);
        if (states.Any(s => s == nameof(BackgroundServiceHealth.Unhealthy)))
            return nameof(BackgroundServiceHealth.Unhealthy);
        if (states.Any(s => s == nameof(BackgroundServiceHealth.Degraded)))
            return nameof(BackgroundServiceHealth.Degraded);
        return nameof(BackgroundServiceHealth.Healthy);
    }

    // -----------------------------------------------------------------
    // Health
    // -----------------------------------------------------------------

    /// <summary>
    /// Run every registered <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck"/>
    /// and project the result set to a dashboard-friendly DTO. Avoids
    /// going over HTTP — we already have the same DI container as the
    /// /healthz endpoint, so we ask it directly.
    /// </summary>
    public async Task<HealthSnapshot> GetHealthSnapshotAsync(CancellationToken ct = default)
    {
        var svc = _services.GetService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>();
        if (svc is null)
        {
            return new HealthSnapshot(
                Overall: "Unknown",
                CheckedAt: DateTimeOffset.UtcNow,
                Entries: Array.Empty<HealthCheckEntry>(),
                Note: "HealthCheckService is not registered in this host.");
        }

        try
        {
            var report = await svc.CheckHealthAsync(ct).ConfigureAwait(false);
            var entries = report.Entries
                .Select(kv => new HealthCheckEntry(
                    Name: kv.Key,
                    Status: kv.Value.Status.ToString(),
                    Description: kv.Value.Description,
                    Duration: kv.Value.Duration,
                    Tags: kv.Value.Tags?.ToArray() ?? Array.Empty<string>(),
                    ErrorMessage: kv.Value.Exception?.Message))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new HealthSnapshot(
                Overall: report.Status.ToString(),
                CheckedAt: DateTimeOffset.UtcNow,
                Entries: entries,
                Note: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DiagnosticsService: health check service threw");
            return new HealthSnapshot(
                Overall: "Unhealthy",
                CheckedAt: DateTimeOffset.UtcNow,
                Entries: Array.Empty<HealthCheckEntry>(),
                Note: $"Health check service threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------
    // Logs (Seq pointer)
    // -----------------------------------------------------------------

    /// <summary>
    /// Resolve the Seq log-viewer URL for the diagnostics logs page.
    /// Returns <see cref="LogViewerInfo.NotConfigured"/> when neither
    /// of the two config keys is set — the page renders a "configure
    /// Seq for log viewing" placeholder + a P2 follow-up note.
    ///
    /// <para>
    /// Resolution order (caller writes whichever is browser-routable):
    /// </para>
    /// <list type="bullet">
    ///   <item><description><see cref="SeqUiUrlConfigKey"/> — explicit UI URL (preferred when ingestion + UI live behind separate hosts).</description></item>
    ///   <item><description><see cref="SeqUrlConfigKey"/> — falls back to the ingestion endpoint when no separate UI URL is configured.</description></item>
    /// </list>
    /// </summary>
    public LogViewerInfo GetLogViewerInfo()
    {
        var uiUrl = _config[SeqUiUrlConfigKey];
        var ingestUrl = _config[SeqUrlConfigKey];
        var url = !string.IsNullOrWhiteSpace(uiUrl)
            ? uiUrl
            : (!string.IsNullOrWhiteSpace(ingestUrl) ? ingestUrl : null);

        if (string.IsNullOrWhiteSpace(url))
        {
            return LogViewerInfo.NotConfigured;
        }

        return new LogViewerInfo(
            IsConfigured: true,
            SeqUrl: url,
            ConfiguredVia: !string.IsNullOrWhiteSpace(uiUrl) ? SeqUiUrlConfigKey : SeqUrlConfigKey);
    }
}

// =====================================================================
// DTOs
// =====================================================================

/// <summary>One worker's snapshot for the diagnostics workers page.</summary>
public sealed record WorkerProbeSnapshot(
    string Name,
    string Health,
    DateTimeOffset? LastTickAt,
    DateTimeOffset? LastSuccessAt,
    long TickCount,
    long ErrorCount,
    string? LastError,
    DateTimeOffset? LastErrorAt);

/// <summary>One health-check entry in the diagnostics health page.</summary>
public sealed record HealthCheckEntry(
    string Name,
    string Status,
    string? Description,
    TimeSpan Duration,
    IReadOnlyList<string> Tags,
    string? ErrorMessage);

/// <summary>Roll-up health snapshot for the diagnostics health page.</summary>
public sealed record HealthSnapshot(
    string Overall,
    DateTimeOffset CheckedAt,
    IReadOnlyList<HealthCheckEntry> Entries,
    string? Note);

/// <summary>
/// Surfaces the configured Seq log-viewer URL — or its absence — to
/// the diagnostics logs page.
/// </summary>
public sealed record LogViewerInfo(
    bool IsConfigured,
    string? SeqUrl,
    string? ConfiguredVia)
{
    /// <summary>Singleton placeholder for the unconfigured case.</summary>
    public static LogViewerInfo NotConfigured { get; } = new(
        IsConfigured: false,
        SeqUrl: null,
        ConfiguredVia: null);
}
