namespace NickERP.Inspection.Application.Thresholds;

/// <summary>
/// Hot-path lookup contract for per-scanner threshold values (§6.5.3).
/// Every Canny call, every percentile-stretch, every split-orchestrator
/// invocation reads through this — implementations are required to be
/// in-process (≤ 1 ms p99).
///
/// <para>
/// The default implementation
/// (<see cref="ScannerThresholdResolver"/>) caches the active profile
/// per <c>ScannerDeviceInstanceId</c> indefinitely and listens on the
/// Postgres <c>threshold_profile_updated</c> NOTIFY channel for
/// cache-eviction signals. A 1-hour belt-and-braces TTL covers the
/// rare case where a NOTIFY is missed (§6.5.8).
/// </para>
/// </summary>
public interface IScannerThresholdResolver
{
    /// <summary>
    /// Returns the active threshold profile for <paramref name="scannerDeviceInstanceId"/>.
    /// The first call for a given scanner does a single SQL fetch + JSON
    /// parse + schema-fallback merge (≤ 20 ms p99). Subsequent calls hit
    /// the in-process cache until a NOTIFY evicts it.
    /// </summary>
    ValueTask<ScannerThresholdSnapshot> GetActiveAsync(
        Guid scannerDeviceInstanceId, CancellationToken ct);
}
