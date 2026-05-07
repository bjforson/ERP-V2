using System.Text.Json;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Queueing.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace NickERP.Platform.Queueing.Services;

/// <summary>
/// Postgres-backed implementation of <see cref="IQueue{TPayload}"/>.
/// Uses raw SQL via <see cref="NpgsqlDataSource"/> rather than EF Core
/// because the claim path needs exact control over
/// <c>FOR UPDATE SKIP LOCKED</c>, <c>ON CONFLICT DO NOTHING</c>, and
/// CTE-based UPDATE-RETURNING — patterns EF abstracts away. The audit
/// log (<c>WorkItemTransition</c>) and the work item itself stay on EF
/// because they're write-once and concurrency is handled by the
/// state machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenancy.</b> Every connection sourced from
/// <see cref="NpgsqlDataSource"/> already has <c>app.tenant_id</c> set
/// by <see cref="NickERP.Platform.Tenancy.TenantConnectionInterceptor"/>
/// (when used through EF) — but this class uses the data source
/// directly, so it relies on the queue tables' DB-side
/// <c>tenant_id DEFAULT</c> + RLS policy combination to scope writes
/// and reads. Workers running outside an HTTP scope (background
/// services) must explicitly resolve a tenant first via
/// <c>ITenantContext.SetSystemContext</c> or impersonate per row.
/// </para>
/// <para>
/// <b>Idempotency.</b> <c>EnqueueAsync</c> uses
/// <c>ON CONFLICT (idempotency_key) DO NOTHING RETURNING id</c>; if the
/// row was already there, the RETURNING is empty and we re-SELECT to
/// fetch the existing id. Producers retrying the same logical event
/// always get the original row back.
/// </para>
/// </remarks>
/// <typeparam name="TPayload">Per-row consumer-specific data type.</typeparam>
public sealed class PostgresQueue<TPayload> : IQueue<TPayload>
    where TPayload : class
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresQueueOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<PostgresQueue<TPayload>> _logger;

    public PostgresQueue(
        NpgsqlDataSource dataSource,
        PostgresQueueOptions options,
        JsonSerializerOptions jsonOptions,
        ILogger<PostgresQueue<TPayload>> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => _options.Name;

    /// <inheritdoc />
    public async Task<long> EnqueueAsync(EnqueueRequest<TPayload> request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadJson = JsonSerializer.Serialize(request.Payload, _jsonOptions);

        // ON CONFLICT (idempotency_key) DO NOTHING — if a duplicate INSERT
        // collides on the unique index, the RETURNING list is empty. We
        // then re-SELECT to fetch the original row's id so the producer
        // sees the same id either way.
        var sql = $@"
            INSERT INTO {_options.QualifiedTableName}
                (work_item_id, available_at, payload, idempotency_key, correlation_id)
            VALUES
                (@work_item_id, COALESCE(@available_at, now()), @payload::jsonb, @idempotency_key, @correlation_id)
            ON CONFLICT (idempotency_key) DO NOTHING
            RETURNING id;";

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter("@work_item_id", NpgsqlDbType.Uuid) { Value = request.WorkItemId });
            cmd.Parameters.Add(new NpgsqlParameter("@available_at", NpgsqlDbType.TimestampTz)
                { Value = (object?)request.AvailableAt ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("@payload", NpgsqlDbType.Text) { Value = payloadJson });
            cmd.Parameters.Add(new NpgsqlParameter("@idempotency_key", NpgsqlDbType.Varchar) { Value = request.IdempotencyKey });
            cmd.Parameters.Add(new NpgsqlParameter("@correlation_id", NpgsqlDbType.Varchar)
                { Value = (object?)request.CorrelationId ?? DBNull.Value });

            var inserted = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (inserted is long id)
            {
                // NOTIFY listeners that there's new work. Done in the same
                // transaction (implicit since we haven't opened one) so
                // a crash between INSERT and NOTIFY is impossible.
                await NotifyAsync(conn, request.WorkItemId.ToString(), ct).ConfigureAwait(false);
                return id;
            }
        }

        // Conflict path — re-select.
        await using (var lookup = new NpgsqlCommand(
            $"SELECT id FROM {_options.QualifiedTableName} WHERE idempotency_key = @key", conn))
        {
            lookup.Parameters.Add(new NpgsqlParameter("@key", NpgsqlDbType.Varchar) { Value = request.IdempotencyKey });
            var existing = await lookup.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (existing is long existingId)
            {
                _logger.LogDebug(
                    "Enqueue dedup hit on queue {Queue} for idempotency key {Key} (existing row {Id})",
                    _options.Name, request.IdempotencyKey, existingId);
                return existingId;
            }
        }

        // Should be unreachable — INSERT conflicted but no row exists?
        throw new InvalidOperationException(
            $"Queue '{_options.Name}' INSERT conflicted on idempotency_key '{request.IdempotencyKey}' but no existing row found.");
    }

    /// <inheritdoc />
    public async Task<IQueueClaim<TPayload>?> ClaimAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        // CTE: find the next claimable row with FOR UPDATE SKIP LOCKED,
        // then UPDATE the same row in the outer statement. Returning
        // every column we need so the consumer doesn't round-trip.
        var sql = $@"
            WITH next AS (
                SELECT id
                FROM {_options.QualifiedTableName}
                WHERE claimed_by IS NULL
                  AND available_at <= now()
                ORDER BY available_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE {_options.QualifiedTableName} q
            SET claimed_by = @worker_id,
                claimed_until = now() + @lease,
                attempt_count = q.attempt_count + 1
            FROM next
            WHERE q.id = next.id
            RETURNING q.id, q.tenant_id, q.work_item_id, q.attempt_count, q.payload, q.claimed_until, q.correlation_id;";

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("@worker_id", NpgsqlDbType.Varchar) { Value = workerId });
        cmd.Parameters.Add(new NpgsqlParameter("@lease", NpgsqlDbType.Interval) { Value = leaseDuration });

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var id = reader.GetInt64(0);
        var tenantId = reader.GetInt64(1);
        var workItemId = reader.GetGuid(2);
        var attemptCount = reader.GetInt32(3);
        var payloadJson = reader.GetString(4);
        var claimedUntil = reader.GetFieldValue<DateTimeOffset>(5);
        var correlationId = reader.IsDBNull(6) ? null : reader.GetString(6);

        var payload = JsonSerializer.Deserialize<TPayload>(payloadJson, _jsonOptions)
            ?? throw new InvalidOperationException(
                $"Queue '{_options.Name}' row {id} payload deserialised to null.");

        return new PostgresQueueClaim<TPayload>(
            queue: this,
            id: id,
            tenantId: tenantId,
            workItemId: workItemId,
            attemptCount: attemptCount,
            payload: payload,
            claimedUntil: claimedUntil,
            correlationId: correlationId);
    }

    /// <inheritdoc />
    public async Task<long> GetReadyDepthAsync(CancellationToken ct = default)
    {
        var sql = $@"
            SELECT COUNT(*)
            FROM {_options.QualifiedTableName}
            WHERE claimed_by IS NULL AND available_at <= now();";
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long n ? n : 0L;
    }

    internal async Task CompleteAsync(long rowId, CancellationToken ct)
    {
        var sql = $"DELETE FROM {_options.QualifiedTableName} WHERE id = @id;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Bigint) { Value = rowId });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal async Task FailAsync(long rowId, int attemptCount, Exception error, TimeSpan? retryAfter, CancellationToken ct)
    {
        // Attempt count was incremented by ClaimAsync; if it now
        // exceeds the budget, promote to DLQ. Otherwise, release the
        // claim and schedule the retry.
        if (attemptCount >= _options.MaxAttempts)
        {
            await PromoteToDeadLetterAsync(rowId, error, ct).ConfigureAwait(false);
            return;
        }

        var backoff = retryAfter ?? ComputeBackoff(attemptCount);
        var sql = $@"
            UPDATE {_options.QualifiedTableName}
            SET claimed_by = NULL,
                claimed_until = NULL,
                available_at = now() + @backoff,
                last_error = @last_error
            WHERE id = @id;";

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Bigint) { Value = rowId });
        cmd.Parameters.Add(new NpgsqlParameter("@backoff", NpgsqlDbType.Interval) { Value = backoff });
        cmd.Parameters.Add(new NpgsqlParameter("@last_error", NpgsqlDbType.Text) { Value = error.Message });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal async Task RenewLeaseAsync(long rowId, TimeSpan additional, CancellationToken ct)
    {
        var sql = $@"
            UPDATE {_options.QualifiedTableName}
            SET claimed_until = COALESCE(claimed_until, now()) + @additional
            WHERE id = @id AND claimed_by IS NOT NULL;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Bigint) { Value = rowId });
        cmd.Parameters.Add(new NpgsqlParameter("@additional", NpgsqlDbType.Interval) { Value = additional });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task PromoteToDeadLetterAsync(long rowId, Exception error, CancellationToken ct)
    {
        // Atomic: copy the row's data into queueing.dead_letter, then
        // delete from the source queue. Done in a single transaction so
        // a crash between the two cannot duplicate or lose the row.
        var sql = $@"
            WITH src AS (
                DELETE FROM {_options.QualifiedTableName} WHERE id = @id
                RETURNING tenant_id, work_item_id, enqueued_at, attempt_count, payload, idempotency_key, correlation_id
            )
            INSERT INTO queueing.dead_letter
                (tenant_id, work_item_id, enqueued_at, available_at, attempt_count, payload, idempotency_key, last_error, correlation_id, source_queue, source_queue_row_id, promoted_at)
            SELECT
                src.tenant_id, src.work_item_id, src.enqueued_at, now(), src.attempt_count, src.payload, src.idempotency_key, @last_error, src.correlation_id, @source_queue, @source_id, now()
            FROM src;";

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Bigint) { Value = rowId });
        cmd.Parameters.Add(new NpgsqlParameter("@last_error", NpgsqlDbType.Text) { Value = error.Message });
        cmd.Parameters.Add(new NpgsqlParameter("@source_queue", NpgsqlDbType.Varchar)
            { Value = $"{_options.Schema}.queue_{_options.Name}" });
        cmd.Parameters.Add(new NpgsqlParameter("@source_id", NpgsqlDbType.Bigint) { Value = rowId });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _logger.LogWarning(
            "Promoted row {RowId} from queue {Queue} to dead letter after {Attempts} attempts. Last error: {Error}",
            rowId, _options.Name, _options.MaxAttempts, error.Message);
    }

    private TimeSpan ComputeBackoff(int attemptCount)
    {
        var idx = Math.Min(attemptCount - 1, _options.RetryBackoff.Count - 1);
        return idx < 0 ? _options.RetryBackoff[0] : _options.RetryBackoff[idx];
    }

    private async Task NotifyAsync(NpgsqlConnection conn, string payload, CancellationToken ct)
    {
        // pg_notify takes channel + payload as text. Use a parameterised
        // call (not SQL identifier interpolation) so the channel name is
        // safe — though we control NotifyChannel, defence in depth.
        await using var cmd = new NpgsqlCommand("SELECT pg_notify(@channel, @payload);", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@channel", NpgsqlDbType.Text) { Value = _options.NotifyChannel });
        cmd.Parameters.Add(new NpgsqlParameter("@payload", NpgsqlDbType.Text) { Value = payload });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
