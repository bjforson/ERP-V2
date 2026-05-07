using NickERP.Platform.Queueing.Abstractions;

namespace NickERP.Platform.Queueing.Services;

/// <summary>
/// Concrete <see cref="IQueueClaim{TPayload}"/> wrapping a single claimed
/// row from a <see cref="PostgresQueue{TPayload}"/>. Tracks
/// complete/fail state so disposal can default-fail uncompleted claims
/// (captured as <c>"claim disposed without complete or fail"</c> in
/// <see cref="Entities.QueueRow.LastError"/>).
/// </summary>
internal sealed class PostgresQueueClaim<TPayload> : IQueueClaim<TPayload>
    where TPayload : class
{
    private readonly PostgresQueue<TPayload> _queue;
    private bool _resolved;

    public PostgresQueueClaim(
        PostgresQueue<TPayload> queue,
        long id,
        long tenantId,
        Guid workItemId,
        int attemptCount,
        TPayload payload,
        DateTimeOffset claimedUntil,
        string? correlationId)
    {
        _queue = queue;
        Id = id;
        TenantId = tenantId;
        WorkItemId = workItemId;
        AttemptCount = attemptCount;
        Payload = payload;
        ClaimedUntil = claimedUntil;
        CorrelationId = correlationId;
    }

    /// <inheritdoc />
    public long Id { get; }

    /// <inheritdoc />
    public long TenantId { get; }

    /// <inheritdoc />
    public Guid WorkItemId { get; }

    /// <inheritdoc />
    public int AttemptCount { get; }

    /// <inheritdoc />
    public TPayload Payload { get; }

    /// <inheritdoc />
    public DateTimeOffset ClaimedUntil { get; private set; }

    /// <inheritdoc />
    public string? CorrelationId { get; }

    /// <inheritdoc />
    public async Task CompleteAsync(CancellationToken ct = default)
    {
        if (_resolved) return;
        await _queue.CompleteAsync(Id, ct).ConfigureAwait(false);
        _resolved = true;
    }

    /// <inheritdoc />
    public async Task FailAsync(Exception error, TimeSpan? retryAfter = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (_resolved) return;
        await _queue.FailAsync(Id, AttemptCount, error, retryAfter, ct).ConfigureAwait(false);
        _resolved = true;
    }

    /// <inheritdoc />
    public async Task RenewLeaseAsync(TimeSpan additional, CancellationToken ct = default)
    {
        if (_resolved)
            throw new InvalidOperationException("Cannot renew a claim that has already been completed or failed.");
        await _queue.RenewLeaseAsync(Id, additional, ct).ConfigureAwait(false);
        ClaimedUntil = ClaimedUntil.Add(additional);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_resolved) return;
        // Default-fail with a synthetic exception so the row gets
        // requeued / DLQ'd rather than silently leaked.
        await _queue.FailAsync(
            Id,
            AttemptCount,
            new InvalidOperationException("Claim disposed without complete or fail."),
            retryAfter: null,
            CancellationToken.None).ConfigureAwait(false);
        _resolved = true;
    }
}
