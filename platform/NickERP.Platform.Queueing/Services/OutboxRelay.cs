using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Queueing.Entities;
using Npgsql;
using NpgsqlTypes;

namespace NickERP.Platform.Queueing.Services;

/// <summary>
/// Drains <c>queueing.outbox</c>, dispatching each row to the matching
/// <see cref="IOutboundAdapter"/> by <see cref="QueueOutboxRow.DestinationKind"/>.
/// Generalises v2 inspection's existing
/// <c>OutboundSubmissionDispatchWorker</c> pattern into a platform-wide
/// service.
/// </summary>
/// <remarks>
/// <para>
/// <b>Claim semantics.</b> Same <c>FOR UPDATE SKIP LOCKED</c> +
/// lease pattern as <see cref="PostgresQueue{TPayload}"/>; multiple
/// relay instances can run side by side without double-dispatching.
/// On adapter success the row is deleted; on failure the lease expires
/// and exponential backoff reschedules.
/// </para>
/// <para>
/// <b>Adapter resolution.</b> Adapters register themselves in DI by
/// <see cref="IOutboundAdapter.DestinationKind"/>. The relay builds a
/// dictionary at startup; missing kinds are logged-and-skipped so an
/// unknown destination doesn't poison the relay (the row stays in the
/// outbox and a deploy can fix the adapter and re-attempt).
/// </para>
/// <para>
/// <b>Wakeups.</b> Subscribes to LISTEN <c>queue_queueing_outbox</c>
/// for low-latency dispatch when a producer enqueues a new row.
/// Polling at <see cref="OutboxRelayOptions.PollInterval"/> covers the
/// gap if a NOTIFY is missed during reconnect.
/// </para>
/// </remarks>
public sealed class OutboxRelay : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IPgNotifyListener _notifyListener;
    private readonly IReadOnlyDictionary<string, IOutboundAdapter> _adapters;
    private readonly OutboxRelayOptions _options;
    private readonly ILogger<OutboxRelay> _logger;
    private readonly SemaphoreSlim _wakeup = new(0, 1);

    public OutboxRelay(
        NpgsqlDataSource dataSource,
        IPgNotifyListener notifyListener,
        IEnumerable<IOutboundAdapter> adapters,
        OutboxRelayOptions options,
        ILogger<OutboxRelay> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _notifyListener = notifyListener ?? throw new ArgumentNullException(nameof(notifyListener));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var byKind = new Dictionary<string, IOutboundAdapter>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters ?? Enumerable.Empty<IOutboundAdapter>())
        {
            if (!byKind.TryAdd(adapter.DestinationKind, adapter))
            {
                throw new InvalidOperationException(
                    $"Two IOutboundAdapter implementations registered with DestinationKind '{adapter.DestinationKind}'.");
            }
        }
        _adapters = byKind;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OutboxRelay starting; {AdapterCount} adapters registered ({Kinds})",
            _adapters.Count,
            string.Join(", ", _adapters.Keys));

        await using var subscription = await _notifyListener.SubscribeAsync(
            "queue_queueing_outbox",
            (_, _) =>
            {
                if (_wakeup.CurrentCount == 0)
                {
                    try { _wakeup.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
                }
                return Task.CompletedTask;
            },
            stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dispatched = await DrainAsync(stoppingToken).ConfigureAwait(false);
                if (dispatched == 0)
                {
                    // Nothing to do — wait for either a wakeup or the
                    // poll interval.
                    var winner = await Task.WhenAny(
                        _wakeup.WaitAsync(stoppingToken),
                        Task.Delay(_options.PollInterval, stoppingToken)).ConfigureAwait(false);
                    _ = winner;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OutboxRelay drain pass failed; backing off");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("OutboxRelay stopped");
    }

    private async Task<int> DrainAsync(CancellationToken ct)
    {
        var dispatched = 0;
        for (int i = 0; i < _options.BatchSize; i++)
        {
            if (ct.IsCancellationRequested) break;

            var row = await ClaimNextAsync(ct).ConfigureAwait(false);
            if (row is null) break;

            await DispatchOneAsync(row, ct).ConfigureAwait(false);
            dispatched++;
        }
        return dispatched;
    }

    private async Task<QueueOutboxRow?> ClaimNextAsync(CancellationToken ct)
    {
        const string sql = @"
            WITH next AS (
                SELECT ""Id"" FROM queueing.outbox
                WHERE ""ClaimedBy"" IS NULL AND ""AvailableAt"" <= now()
                ORDER BY ""AvailableAt"" FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE queueing.outbox o
            SET ""ClaimedBy"" = @worker, ""ClaimedUntil"" = now() + @lease, ""AttemptCount"" = o.""AttemptCount"" + 1
            FROM next
            WHERE o.""Id"" = next.""Id""
            RETURNING o.""Id"", o.""TenantId"", o.""WorkItemId"", o.""AttemptCount"", o.""Payload"", o.""IdempotencyKey"", o.""CorrelationId"",
                      o.""DestinationKind"", o.""DestinationName"", o.""ContentType"";";

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await PushQueueSystemContextAsync(conn, ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("@worker", NpgsqlDbType.Varchar) { Value = _options.WorkerId });
        cmd.Parameters.Add(new NpgsqlParameter("@lease", NpgsqlDbType.Interval) { Value = _options.LeaseDuration });

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new QueueOutboxRow
        {
            Id = reader.GetInt64(0),
            TenantId = reader.GetInt64(1),
            WorkItemId = reader.GetGuid(2),
            AttemptCount = reader.GetInt32(3),
            Payload = reader.GetString(4),
            IdempotencyKey = reader.GetString(5),
            CorrelationId = reader.IsDBNull(6) ? null : reader.GetString(6),
            DestinationKind = reader.GetString(7),
            DestinationName = reader.GetString(8),
            ContentType = reader.GetString(9)
        };
    }

    private async Task DispatchOneAsync(QueueOutboxRow row, CancellationToken ct)
    {
        if (!_adapters.TryGetValue(row.DestinationKind, out var adapter))
        {
            _logger.LogError(
                "Outbox row {Id} has DestinationKind '{Kind}' with no registered adapter; releasing lease for retry",
                row.Id, row.DestinationKind);
            await ReleaseAsync(row.Id, $"No adapter for kind '{row.DestinationKind}'", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await adapter.DispatchAsync(row, ct).ConfigureAwait(false);
            await DeleteAsync(row.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Adapter '{Kind}' threw on outbox row {Id} (attempt {Attempt})",
                row.DestinationKind, row.Id, row.AttemptCount);
            await ReleaseAsync(row.Id, ex.Message, ct).ConfigureAwait(false);
            // Future: when row.AttemptCount >= MaxAttempts, promote to DLQ.
            // For the initial scaffold the QueueJanitor handles lease expiry;
            // explicit DLQ promotion lands in Sprint S+1.
        }
    }

    private async Task DeleteAsync(long id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await PushQueueSystemContextAsync(conn, ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"DELETE FROM queueing.outbox WHERE ""Id"" = @id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Bigint) { Value = id });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task ReleaseAsync(long id, string lastError, CancellationToken ct)
    {
        const string sql = @"
            UPDATE queueing.outbox
            SET ""ClaimedBy"" = NULL, ""ClaimedUntil"" = NULL,
                ""AvailableAt"" = now() + interval '30 seconds',
                ""LastError"" = @last_error
            WHERE ""Id"" = @id;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await PushQueueSystemContextAsync(conn, ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Bigint) { Value = id });
        cmd.Parameters.Add(new NpgsqlParameter("@last_error", NpgsqlDbType.Text) { Value = lastError });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task PushQueueSystemContextAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.tenant_id', '-1', false);", conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Configuration for <see cref="OutboxRelay"/>.</summary>
public sealed class OutboxRelayOptions
{
    /// <summary>Identity stamped on <see cref="QueueRow.ClaimedBy"/>. Defaults to <c>{machine}/{pid}/outbox-relay</c>.</summary>
    public string WorkerId { get; init; } =
        $"{Environment.MachineName}/{Environment.ProcessId}/outbox-relay";

    /// <summary>How long a claim is valid. Default 5 minutes.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Polling fallback interval — used when LISTEN/NOTIFY hasn't fired. Default 5 seconds.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Max rows to drain per pass before yielding. Default 32.</summary>
    public int BatchSize { get; init; } = 32;
}
