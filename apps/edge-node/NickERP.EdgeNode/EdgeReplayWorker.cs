using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NickERP.EdgeNode;

/// <summary>
/// Sprint 11 / P2 — background drain loop for the edge node's local
/// SQLite buffer. On every tick:
/// <list type="number">
///   <item><description>Probe <c>{server}/healthz/ready</c>. If unreachable, log and skip.</description></item>
///   <item><description>Read up to <see cref="EdgeNodeOptions.MaxBatchSize"/> rows where <c>ReplayedAt IS NULL ORDER BY Id ASC</c>.</description></item>
///   <item><description>POST the batch to <c>{server}/api/edge/replay</c>. Per-entry results: success → mark <c>ReplayedAt = now</c>; 4xx → log permanent error, increment attempts, leave <c>ReplayedAt = null</c>. 5xx (transport) → bump attempts only, no permanent error tag.</description></item>
///   <item><description>After 5 failed attempts on a single row, emit an error log line so an ops dashboard can flag the edge — but keep retrying. Events are never silently dropped.</description></item>
/// </list>
///
/// <para>
/// FIFO order is the load-bearing invariant. Per-edge ordering is
/// preserved by the strict id-asc scan; cross-edge merging is the
/// server's problem (decision: server doesn't try, edges interleave by
/// their own <see cref="EdgeOutboxEntry.EdgeTimestamp"/>).
/// </para>
///
/// <para>
/// Implements <see cref="IEdgeReplayProbe"/> so the
/// <c>/edge/healthz</c> endpoint can report queue depth + last
/// successful replay without reaching into the worker via reflection.
/// </para>
/// </summary>
public sealed class EdgeReplayWorker : BackgroundService, IEdgeReplayProbe
{
    /// <summary>
    /// Threshold above which a single row's repeated failures escalate
    /// from <see cref="LogLevel.Warning"/> to <see cref="LogLevel.Error"/>.
    /// Below threshold the warning log line is per-tick noise; above
    /// it the row is genuinely stuck and an ops dashboard should
    /// flag it. Match the spec text.
    /// </summary>
    public const int FailureAlertThreshold = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<EdgeNodeOptions> _opts;
    private readonly TimeProvider _clock;
    private readonly ILogger<EdgeReplayWorker> _logger;

    private long _queueDepth;
    private DateTimeOffset? _lastSuccessfulReplayAt;

    public EdgeReplayWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<EdgeNodeOptions> opts,
        ILogger<EdgeReplayWorker> logger,
        TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public long QueueDepth => Interlocked.Read(ref _queueDepth);

    /// <inheritdoc />
    public DateTimeOffset? LastSuccessfulReplayAt => _lastSuccessfulReplayAt;

    /// <inheritdoc />
    public string EdgeNodeId => _opts.Value.Id;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _opts.Value.ReplayIntervalSeconds));
        _logger.LogInformation(
            "EdgeReplayWorker starting; interval={Interval}s, batch={Batch}, edgeNodeId={Id}",
            interval.TotalSeconds, _opts.Value.MaxBatchSize, _opts.Value.Id);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Belt + braces — never let an unexpected exception
                // wedge the worker. Log and try again next tick.
                _logger.LogError(ex, "EdgeReplayWorker tick failed; will retry on next interval.");
            }

            try
            {
                await Task.Delay(interval, _clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("EdgeReplayWorker stopping (cancellation requested).");
    }

    /// <summary>
    /// Single replay pass. Public for tests that drive the loop in
    /// deterministic increments (no real time delay, no service-host
    /// boilerplate).
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<EdgeBufferDbContext>();
        var client = sp.GetRequiredService<IEdgeReplayClient>();

        // Refresh queue-depth view first so the /edge/healthz endpoint
        // can report a current value even when the server is offline.
        var pendingCount = await db.Outbox.CountAsync(o => o.ReplayedAt == null, ct);
        Interlocked.Exchange(ref _queueDepth, pendingCount);

        if (pendingCount == 0)
        {
            // Nothing to drain — skip the probe (unnecessary network) and
            // bail. The /healthz endpoint still reports zero depth.
            return;
        }

        if (!await client.IsServerReachableAsync(ct))
        {
            _logger.LogDebug("Server not reachable; skipping replay tick. Queue depth = {Depth}.", pendingCount);
            return;
        }

        var batchSize = Math.Max(1, _opts.Value.MaxBatchSize);
        var batch = await db.Outbox
            .Where(o => o.ReplayedAt == null)
            .OrderBy(o => o.Id)
            .Take(batchSize)
            .ToListAsync(ct);
        if (batch.Count == 0) return;

        EdgeReplayResponse response;
        try
        {
            response = await client.SendBatchAsync(batch, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Transient — bump attempts on every row in the batch but
            // don't tag a permanent error. 5xx is the expected channel.
            _logger.LogWarning(ex, "Edge replay batch failed transiently; will retry. Batch size = {Size}.", batch.Count);
            foreach (var row in batch)
            {
                row.ReplayAttempts += 1;
            }
            await db.SaveChangesAsync(ct);
            return;
        }

        if (response.Results.Count != batch.Count)
        {
            _logger.LogError(
                "Server returned {ResultCount} results for a batch of {BatchSize}; treating remaining as transient.",
                response.Results.Count, batch.Count);
        }

        var now = _clock.GetUtcNow();
        var anySuccess = false;

        for (var i = 0; i < batch.Count; i++)
        {
            var row = batch[i];
            row.ReplayAttempts += 1;
            if (i >= response.Results.Count)
            {
                // Server short-replied — treat the rest as transient.
                continue;
            }

            var result = response.Results[i];
            if (result.Ok)
            {
                row.ReplayedAt = now;
                // Intentionally retain LastReplayError so an on-disk
                // audit shows the prior failure before the eventual
                // success — useful when triaging "why did this take so
                // long".
                anySuccess = true;
            }
            else
            {
                row.LastReplayError = result.Error ?? "(unspecified server-side rejection)";
                if (row.ReplayAttempts >= FailureAlertThreshold)
                {
                    _logger.LogError(
                        "Edge outbox row id={Id} stuck after {Attempts} attempts: {Error}. Will keep retrying.",
                        row.Id, row.ReplayAttempts, row.LastReplayError);
                }
                else
                {
                    _logger.LogWarning(
                        "Edge outbox row id={Id} rejected (attempt {Attempts}): {Error}.",
                        row.Id, row.ReplayAttempts, row.LastReplayError);
                }
            }
        }

        await db.SaveChangesAsync(ct);

        if (anySuccess)
        {
            _lastSuccessfulReplayAt = now;
        }

        // Refresh queue depth after the drain so the next /healthz
        // read sees the updated value.
        Interlocked.Exchange(ref _queueDepth, await db.Outbox.CountAsync(o => o.ReplayedAt == null, ct));
    }
}

/// <summary>
/// Read-only probe over the worker's runtime state, exposed via
/// <c>/edge/healthz</c> and consumable by tests.
/// </summary>
public interface IEdgeReplayProbe
{
    /// <summary>Number of unreplayed rows in the local outbox as of the most recent tick.</summary>
    long QueueDepth { get; }

    /// <summary>Timestamp of the last replay attempt that succeeded for at least one row. Null at boot before the first success.</summary>
    DateTimeOffset? LastSuccessfulReplayAt { get; }

    /// <summary>Configured edge-node id. Echoed in the healthz body so the response is self-identifying.</summary>
    string EdgeNodeId { get; }
}
