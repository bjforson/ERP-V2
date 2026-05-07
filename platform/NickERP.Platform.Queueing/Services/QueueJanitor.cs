using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace NickERP.Platform.Queueing.Services;

/// <summary>
/// Periodically releases expired leases across every registered queue
/// and across <c>queueing.outbox</c> + <c>queueing.dead_letter</c>. A
/// row whose <c>claimed_until</c> is in the past gets its
/// <c>claimed_by</c> reset to NULL so a different worker can pick it up
/// on the next claim.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> The janitor doesn't promote rows to the DLQ — that
/// happens inside <see cref="PostgresQueue{TPayload}.FailAsync"/> when
/// a worker explicitly fails or default-fails (claim disposed without
/// resolution). This service only releases stale leases so dead workers
/// don't permanently park rows.
/// </para>
/// <para>
/// <b>Cost.</b> Single UPDATE per pass, scoped to the partial index on
/// <c>claimed_until</c>. Run every 30s — well under any reasonable
/// lease duration so a dead worker's rows are picked up promptly.
/// </para>
/// </remarks>
public sealed class QueueJanitor : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly QueueJanitorOptions _options;
    private readonly ILogger<QueueJanitor> _logger;

    // Constructor accepts the internal IPostQueueRegistration enumeration
    // (the marker interface published by AddPostgresQueue<T>); marking the
    // ctor itself internal keeps the accessibility consistent. DI resolves
    // ctors regardless of accessibility, and the whole class is constructed
    // exclusively by DI / by AddHostedService<QueueJanitor>().
    internal QueueJanitor(
        NpgsqlDataSource dataSource,
        QueueJanitorOptions options,
        IEnumerable<IPostQueueRegistration> queueRegistrations,
        ILogger<QueueJanitor> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Merge the static options-supplied list (typically just the
        // shared queueing.outbox + queueing.dead_letter) with the
        // dynamically-discovered per-queue registrations.
        var optionsTables = options?.QualifiedTableNames ?? Array.Empty<string>();
        var dynamicTables = queueRegistrations?.Select(r => r.QualifiedTableName) ?? Array.Empty<string>();
        var merged = optionsTables.Concat(dynamicTables).Distinct(StringComparer.Ordinal).ToArray();
        _options = new QueueJanitorOptions
        {
            SweepInterval = options?.SweepInterval ?? TimeSpan.FromSeconds(30),
            CommandTimeout = options?.CommandTimeout ?? TimeSpan.FromSeconds(60),
            QualifiedTableNames = merged
        };
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger startup so multiple app instances don't all sweep at
        // the same instant.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(0, 30)), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var released = await SweepAsync(stoppingToken).ConfigureAwait(false);
                if (released > 0)
                {
                    _logger.LogInformation("QueueJanitor released {Released} expired leases", released);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "QueueJanitor sweep failed; continuing");
            }

            try
            {
                await Task.Delay(_options.SweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<int> SweepAsync(CancellationToken ct)
    {
        // Each registered queue table is swept by the same UPDATE;
        // the table list comes from QueueJanitorOptions so modules
        // self-register as they ship. A single multi-statement command
        // keeps the round trip count low.
        if (_options.QualifiedTableNames.Count == 0)
        {
            return 0;
        }

        var totalReleased = 0;
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        foreach (var qualified in _options.QualifiedTableNames)
        {
            var sql = $@"
                UPDATE {qualified}
                SET claimed_by = NULL, claimed_until = NULL
                WHERE claimed_by IS NOT NULL AND claimed_until < now();";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
            var n = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            totalReleased += n;
        }

        return totalReleased;
    }
}

/// <summary>Configuration for <see cref="QueueJanitor"/>.</summary>
public sealed class QueueJanitorOptions
{
    /// <summary>How often to sweep. Default 30 seconds.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Per-statement command timeout. The sweep is a partial-index UPDATE so
    /// it should always be fast; a generous timeout (60s) protects against
    /// pathological lock contention without masking real problems.
    /// </summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Fully-qualified queue table identifiers to sweep. Populated by
    /// <c>AddPostgresQueue&lt;T&gt;</c> registrations. Always includes
    /// <c>queueing.outbox</c> and <c>queueing.dead_letter</c>.
    /// </summary>
    public IReadOnlyList<string> QualifiedTableNames { get; init; } = Array.Empty<string>();
}
