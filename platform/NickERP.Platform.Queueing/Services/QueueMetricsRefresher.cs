using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace NickERP.Platform.Queueing.Services;

/// <summary>
/// Periodically refreshes <c>queueing.queue_metrics</c>, the
/// materialised view that backs the <c>/api/_module/queues</c>
/// observability endpoint and the portal dashboard's queue-health
/// cards.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a materialised view.</b> Queue depth across N tables is a
/// UNION ALL with one COUNT per table; on a busy system the live query
/// runs on every dashboard refresh and adds up. A 5-second-stale MV
/// gives the same operational signal at a tiny fraction of the cost.
/// </para>
/// <para>
/// <b>REFRESH MATERIALIZED VIEW CONCURRENTLY.</b> Requires a unique
/// index on the MV (the migration creates one on
/// <c>queue_name</c>). Concurrency lets readers continue to query the
/// view while the refresh runs — important for a dashboard that polls
/// every few seconds.
/// </para>
/// <para>
/// <b>Cost.</b> One short-running statement per interval; cheap enough
/// to run every 5s on a single instance. Multi-instance deployments
/// stagger startup so they don't all refresh at the same instant.
/// </para>
/// </remarks>
public sealed class QueueMetricsRefresher : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly QueueMetricsRefresherOptions _options;
    private readonly ILogger<QueueMetricsRefresher> _logger;

    public QueueMetricsRefresher(
        NpgsqlDataSource dataSource,
        QueueMetricsRefresherOptions options,
        ILogger<QueueMetricsRefresher> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Stagger startup so multi-instance deploys don't all refresh
            // at the same instant.
            await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(0, 10)), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await _dataSource.OpenConnectionAsync(stoppingToken).ConfigureAwait(false);
                await using var cmd = new NpgsqlCommand(
                    "REFRESH MATERIALIZED VIEW CONCURRENTLY queueing.queue_metrics;", conn);
                cmd.CommandTimeout = (int)_options.RefreshTimeout.TotalSeconds;
                await cmd.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "QueueMetricsRefresher refresh failed; continuing");
            }

            try
            {
                await Task.Delay(_options.RefreshInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("QueueMetricsRefresher stopped");
    }
}

/// <summary>Configuration for <see cref="QueueMetricsRefresher"/>.</summary>
public sealed class QueueMetricsRefresherOptions
{
    /// <summary>How often to REFRESH MATERIALIZED VIEW CONCURRENTLY. Default 5 seconds.</summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Per-statement timeout for the REFRESH command. Default 30 seconds.</summary>
    public TimeSpan RefreshTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
