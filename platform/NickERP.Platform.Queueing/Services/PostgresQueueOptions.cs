namespace NickERP.Platform.Queueing.Services;

/// <summary>
/// Per-queue configuration. One instance per registered
/// <c>IQueue&lt;TPayload&gt;</c>; supplied at registration time via
/// <c>services.AddPostgresQueue&lt;TPayload&gt;(configure)</c>.
/// </summary>
public sealed class PostgresQueueOptions
{
    /// <summary>
    /// Logical name of the queue — matches the suffix of the physical
    /// table after the <c>queue_</c> prefix. The full table identifier
    /// is <c>{Schema}.queue_{Name}</c>. Required.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Schema the queue table lives in (e.g. <c>"inspection"</c>,
    /// <c>"queueing"</c>). Modules typically use their own schema; the
    /// shared platform queues (outbox, dead_letter) live in
    /// <c>queueing</c>. Required.
    /// </summary>
    public string Schema { get; set; } = string.Empty;

    /// <summary>
    /// Maximum attempts before a row is promoted to the dead-letter
    /// queue. Default 5 — calibrated for transient-failure tolerance
    /// (network blips, brief downstream hiccups) without letting a
    /// genuinely poison message drown a worker pool.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Default lease duration when callers don't specify. 5 minutes
    /// matches the v1 cache TTLs and is long enough for most
    /// inspection-pipeline stages without being so long that a dead
    /// worker holds work for an unreasonable time.
    /// </summary>
    public TimeSpan DefaultLeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Backoff schedule applied when a row fails and gets requeued.
    /// Index = attempt count just attempted (0-based — first failure
    /// uses index 0). Calibrated as a geometric progression: 5s, 15s,
    /// 1min, 5min, 15min. After the last entry, attempts continue at
    /// the last interval until <see cref="MaxAttempts"/> is hit.
    /// </summary>
    public IReadOnlyList<TimeSpan> RetryBackoff { get; set; } = new[]
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15)
    };

    /// <summary>
    /// Fully-qualified table identifier — <c>"{Schema}.queue_{Name}"</c>.
    /// Quoted form for safe interpolation into raw SQL.
    /// </summary>
    public string QualifiedTableName => $"\"{Schema}\".\"queue_{Name}\"";

    /// <summary>The LISTEN channel name producers NOTIFY on. Same as the table identifier sans the quotes.</summary>
    public string NotifyChannel => $"queue_{Schema}_{Name}";

    /// <summary>Validate that required fields are set. Called by the DI helper after the configure callback runs.</summary>
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException("PostgresQueueOptions.Name is required.");
        if (string.IsNullOrWhiteSpace(Schema))
            throw new InvalidOperationException("PostgresQueueOptions.Schema is required.");
        if (MaxAttempts < 1)
            throw new InvalidOperationException("PostgresQueueOptions.MaxAttempts must be >= 1.");
    }
}
