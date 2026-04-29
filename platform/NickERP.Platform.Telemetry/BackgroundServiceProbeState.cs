namespace NickERP.Platform.Telemetry;

/// <summary>
/// Sprint 9 / FU-host-status — reusable, thread-safe scratchpad backing
/// an <see cref="IBackgroundServiceProbe"/> implementation. Workers
/// compose-in a single instance and call:
///
/// <list type="bullet">
///   <item><see cref="RecordTickStart"/> at the top of each cycle.</item>
///   <item><see cref="RecordTickSuccess"/> when the cycle finishes without throwing.</item>
///   <item><see cref="RecordTickFailure"/> when the cycle's catch fires.</item>
/// </list>
///
/// <para>
/// Concurrency strategy: counters live under <see cref="Interlocked"/>;
/// the datetime / string fields live on a single record that's replaced
/// atomically via <see cref="Volatile.Write{T}(ref T, T)"/>. Readers
/// (the <c>/healthz/workers</c> endpoint) get a consistent snapshot — no
/// lock needed because the record is immutable. Writers (the worker
/// loop, single-threaded) compose-and-replace via <c>with { ... }</c>.
/// </para>
///
/// <para>
/// Health classification is derived from the recorded fields against a
/// poll interval the worker tells us at startup via
/// <see cref="SetPollInterval"/>:
/// </para>
/// <list type="bullet">
///   <item>Never ticked → <see cref="BackgroundServiceHealth.Unhealthy"/></item>
///   <item>Last tick &gt; poll × 5 ago → <see cref="BackgroundServiceHealth.Unhealthy"/></item>
///   <item>Most recent cycle errored (LastErrorAt &gt; LastSuccessAt) → <see cref="BackgroundServiceHealth.Degraded"/></item>
///   <item>Else → <see cref="BackgroundServiceHealth.Healthy"/></item>
/// </list>
/// </summary>
public sealed class BackgroundServiceProbeState
{
    /// <summary>Optional override for "now" — tests inject a stub clock.</summary>
    private readonly Func<DateTimeOffset> _clock;

    private long _tickCount;
    private long _errorCount;
    private ProbeFields _fields = new(null, null, null, null);
    private TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Default ctor — uses <see cref="DateTimeOffset.UtcNow"/> as the clock.</summary>
    public BackgroundServiceProbeState() : this(static () => DateTimeOffset.UtcNow) { }

    /// <summary>Test ctor — injects a clock so health classification is deterministic.</summary>
    public BackgroundServiceProbeState(Func<DateTimeOffset> clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Tell the probe what poll interval the worker uses. Drives the
    /// "no tick in poll × 5" Unhealthy threshold. Call once at startup
    /// from the worker's ExecuteAsync — pre-startup the default of 5s
    /// is fine because no tick has happened yet anyway.
    /// </summary>
    public void SetPollInterval(TimeSpan poll)
    {
        // Direct assignment is safe — TimeSpan is a struct; the worker
        // sets it once before the loop starts; readers see either the
        // default or the new value, both well-formed.
        _pollInterval = poll > TimeSpan.Zero ? poll : TimeSpan.FromSeconds(5);
    }

    /// <summary>Stamp the entry point of a tick (call before doing work).</summary>
    public void RecordTickStart()
    {
        var now = _clock();
        Volatile.Write(ref _fields, Volatile.Read(ref _fields) with { LastTickAt = now });
    }

    /// <summary>Stamp a successful tick completion. Increments TickCount.</summary>
    public void RecordTickSuccess()
    {
        Interlocked.Increment(ref _tickCount);
        var now = _clock();
        Volatile.Write(ref _fields, Volatile.Read(ref _fields) with { LastSuccessAt = now });
    }

    /// <summary>
    /// Stamp a failed tick completion. Increments TickCount AND
    /// ErrorCount and records the message (truncated to 1900 chars).
    /// </summary>
    public void RecordTickFailure(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        Interlocked.Increment(ref _tickCount);
        Interlocked.Increment(ref _errorCount);
        var now = _clock();
        var msg = ex.Message;
        if (msg.Length > 1900) msg = msg[..1900];
        Volatile.Write(
            ref _fields,
            Volatile.Read(ref _fields) with { LastError = msg, LastErrorAt = now });
    }

    /// <summary>Snapshot the probe state for an <see cref="IBackgroundServiceProbe.GetState"/> call.</summary>
    public BackgroundServiceState Snapshot()
    {
        var fields = Volatile.Read(ref _fields);
        var ticks = Interlocked.Read(ref _tickCount);
        var errors = Interlocked.Read(ref _errorCount);
        return new BackgroundServiceState(
            LastTickAt: fields.LastTickAt,
            LastSuccessAt: fields.LastSuccessAt,
            TickCount: ticks,
            ErrorCount: errors,
            LastError: fields.LastError,
            LastErrorAt: fields.LastErrorAt,
            Health: Classify(fields, _pollInterval, _clock()));
    }

    private static BackgroundServiceHealth Classify(ProbeFields f, TimeSpan poll, DateTimeOffset now)
    {
        if (f.LastTickAt is null) return BackgroundServiceHealth.Unhealthy;

        var sinceTick = now - f.LastTickAt.Value;
        if (sinceTick > poll * 5) return BackgroundServiceHealth.Unhealthy;

        var lastErrorAfterSuccess =
            f.LastErrorAt is not null
            && (f.LastSuccessAt is null || f.LastErrorAt > f.LastSuccessAt);
        if (lastErrorAfterSuccess) return BackgroundServiceHealth.Degraded;

        return BackgroundServiceHealth.Healthy;
    }

    /// <summary>
    /// Immutable holder for the non-counter probe fields. Replaced
    /// atomically via <see cref="Volatile.Write{T}(ref T, T)"/> so
    /// readers see a consistent snapshot.
    /// </summary>
    private sealed record ProbeFields(
        DateTimeOffset? LastTickAt,
        DateTimeOffset? LastSuccessAt,
        string? LastError,
        DateTimeOffset? LastErrorAt);
}
