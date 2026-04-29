using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NickERP.Inspection.Application.Thresholds;

/// <summary>
/// Asserts the <see cref="ScannerThresholdResolver"/>'s long-lived
/// LISTEN/NOTIFY subscription is connected (§6.5.6 propagation budget).
/// Goes <c>Degraded</c> rather than <c>Unhealthy</c> when the connection
/// is down — the resolver still serves from cache + the 1-hour TTL keeps
/// stale entries from being permanent, so a bouncing Postgres link
/// shouldn't bring readiness probes down.
/// </summary>
public sealed class ScannerThresholdResolverHealthCheck : IHealthCheck
{
    private readonly ScannerThresholdResolver _resolver;

    public ScannerThresholdResolverHealthCheck(ScannerThresholdResolver resolver)
    {
        _resolver = resolver;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var s = _resolver.GetListenState();
        var data = new Dictionary<string, object>
        {
            ["isConnected"] = s.IsConnected,
            ["connectedAt"] = s.ConnectedAt?.ToString("o") ?? "(null)",
            ["lastDisconnectedAt"] = s.LastDisconnectedAt?.ToString("o") ?? "(null)",
            ["lastError"] = s.LastError ?? "(none)",
            ["notificationsReceived"] = s.NotificationsReceived,
            ["cacheEntries"] = s.CacheEntries
        };

        if (s.IsConnected)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                description: $"LISTEN/NOTIFY connected (entries={s.CacheEntries}, notifications={s.NotificationsReceived})",
                data: data));
        }

        // Resolver still serves cached values until the TTL pops, so a
        // disconnected listener is degraded — not unhealthy. The
        // last-error message ships in the data bag for ops triage.
        return Task.FromResult(HealthCheckResult.Degraded(
            description: $"LISTEN/NOTIFY disconnected — cache eviction relies on TTL until reconnect. lastError={s.LastError ?? "(none)"}",
            data: data));
    }
}
