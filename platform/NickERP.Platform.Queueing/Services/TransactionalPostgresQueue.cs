using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NickERP.Platform.Queueing.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace NickERP.Platform.Queueing.Services;

/// <summary>
/// Inserts queue rows through an existing EF Core transaction. This is the
/// producer path for state machines: the state update, transition audit row,
/// and queue handoff share one commit boundary.
/// </summary>
/// <typeparam name="TPayload">Per-row consumer-specific data type.</typeparam>
public sealed class TransactionalPostgresQueue<TPayload> : ITransactionalQueue<TPayload>
{
    private readonly PostgresQueueOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public TransactionalPostgresQueue(PostgresQueueOptions options, JsonSerializerOptions jsonOptions)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
    }

    /// <inheritdoc />
    public async Task<long> EnqueueAsync(
        DbContext db,
        EnqueueRequest<TPayload> request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(request);

        var currentTransaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                $"Queue '{_options.Name}' transactional enqueue requires an active EF Core transaction.");

        if (db.Database.GetDbConnection() is not NpgsqlConnection conn)
        {
            throw new InvalidOperationException(
                $"Queue '{_options.Name}' transactional enqueue requires an NpgsqlConnection.");
        }

        if (currentTransaction.GetDbTransaction() is not NpgsqlTransaction tx)
        {
            throw new InvalidOperationException(
                $"Queue '{_options.Name}' transactional enqueue requires an NpgsqlTransaction.");
        }

        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
        }

        var payloadJson = JsonSerializer.Serialize(request.Payload, _jsonOptions);
        var sql = $@"
            INSERT INTO {_options.QualifiedTableName}
                (""WorkItemId"", ""AvailableAt"", ""Payload"", ""IdempotencyKey"", ""CorrelationId"")
            VALUES
                (@work_item_id, COALESCE(@available_at, now()), @payload::jsonb, @idempotency_key, @correlation_id)
            ON CONFLICT (""IdempotencyKey"") DO NOTHING
            RETURNING ""Id"";";

        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
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
                await NotifyAsync(conn, tx, request.WorkItemId.ToString(), ct).ConfigureAwait(false);
                return id;
            }
        }

        await using (var lookup = new NpgsqlCommand(
            $@"SELECT ""Id"" FROM {_options.QualifiedTableName} WHERE ""IdempotencyKey"" = @key",
            conn,
            tx))
        {
            lookup.Parameters.Add(new NpgsqlParameter("@key", NpgsqlDbType.Varchar) { Value = request.IdempotencyKey });
            var existing = await lookup.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (existing is long existingId)
            {
                return existingId;
            }
        }

        throw new InvalidOperationException(
            $"Queue '{_options.Name}' INSERT conflicted on IdempotencyKey '{request.IdempotencyKey}' but no existing row found.");
    }

    private async Task NotifyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string payload, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_notify(@channel, @payload);", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("@channel", NpgsqlDbType.Text) { Value = _options.NotifyChannel });
        cmd.Parameters.Add(new NpgsqlParameter("@payload", NpgsqlDbType.Text) { Value = payload });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
