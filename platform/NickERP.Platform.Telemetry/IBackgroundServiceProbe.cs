namespace NickERP.Platform.Telemetry;

/// <summary>
/// Sprint 9 / FU-host-status — observable surface every long-running
/// <c>BackgroundService</c> exposes so an aggregating endpoint
/// (<c>/healthz/workers</c>) can report per-worker liveness without
/// log-grepping.
///
/// <para>
/// Lives in <see cref="NickERP.Platform.Telemetry"/> because Telemetry is
/// a dependency-leaf project (no project refs of its own) and every
/// project that hosts a worker — <c>Audit.Database</c>,
/// <c>Inspection.Imaging</c>, <c>Inspection.Web</c> — already references
/// it (or can reference it without introducing a cycle). Putting the
/// interface here lets all four workers implement the same contract
/// and the inspection-web aggregator endpoint resolve the probes
/// uniformly.
/// </para>
///
/// <para>
/// Thread-safety: implementations MUST be safe to call from a thread
/// other than the worker's tick loop — the endpoint reads
/// <see cref="GetState"/> from an HTTP request thread while the worker
/// updates state on its own loop thread. Recommended pattern: use
/// <c>Interlocked</c> for the counters and a single
/// atomically-replaced state record for the datetime / string fields
/// (or wrap mutations in a private <c>lock</c>). Don't tie state
/// updates to scoped DbContext lifetimes — the worker's existing scope
/// rebuild is per-cycle, but the probe must live for the worker's
/// whole lifetime.
/// </para>
/// </summary>
public interface IBackgroundServiceProbe
{
    /// <summary>
    /// Stable, human-readable identifier surfaced in the
    /// <c>/healthz/workers</c> JSON. Conventionally the worker's class
    /// name (e.g. <c>"PreRenderWorker"</c>); ops dashboards key off this.
    /// </summary>
    string WorkerName { get; }

    /// <summary>
    /// Snapshot of the worker's current liveness state. Must be safe to
    /// call concurrently with the worker's tick loop.
    /// </summary>
    BackgroundServiceState GetState();
}

/// <summary>
/// Immutable snapshot of a worker's liveness state. The aggregator
/// endpoint serializes this directly to JSON; renaming the properties
/// is a public API change.
///
/// <para>
/// <see cref="Health"/> is a derived classification computed by the
/// implementation against its own poll interval:
/// </para>
/// <list type="bullet">
///   <item><c>Healthy</c> — ticked within <c>poll-interval × 3</c> AND
///   no recent error (no error since the last successful tick).</item>
///   <item><c>Degraded</c> — ticked within <c>poll-interval × 3</c> but
///   the last cycle errored (the loop is alive but the work is
///   failing).</item>
///   <item><c>Unhealthy</c> — no tick in <c>poll-interval × 5+</c> (the
///   loop is wedged) OR the worker has never ticked since startup
///   despite enough time having elapsed.</item>
/// </list>
/// </summary>
/// <param name="LastTickAt">UTC timestamp of the most recent loop iteration entering work, or null if never.</param>
/// <param name="LastSuccessAt">UTC timestamp of the most recent successful cycle, or null if never.</param>
/// <param name="TickCount">Total number of cycles entered since startup.</param>
/// <param name="ErrorCount">Total number of cycles that threw, since startup.</param>
/// <param name="LastError">Truncated error message from the most recent failed cycle, or null.</param>
/// <param name="LastErrorAt">UTC timestamp of the most recent failed cycle, or null.</param>
/// <param name="Health">Derived liveness classification — see remarks on the type.</param>
public sealed record BackgroundServiceState(
    DateTimeOffset? LastTickAt,
    DateTimeOffset? LastSuccessAt,
    long TickCount,
    long ErrorCount,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    BackgroundServiceHealth Health);

/// <summary>
/// Liveness verdict for a single worker. Aggregated across all workers
/// to produce the <c>/healthz/workers</c> overall verdict — any
/// <c>Unhealthy</c> wins; otherwise any <c>Degraded</c> wins; otherwise
/// <c>Healthy</c>.
/// </summary>
public enum BackgroundServiceHealth
{
    /// <summary>Recent tick, no recent error.</summary>
    Healthy,

    /// <summary>Recent tick, but the last cycle errored.</summary>
    Degraded,

    /// <summary>No tick in too long — loop is wedged or never started.</summary>
    Unhealthy
}
