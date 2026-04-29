namespace NickERP.Inspection.Application.Thresholds;

/// <summary>
/// Strongly-typed projection of <c>ScannerThresholdProfile.ValuesJson</c>
/// (§6.5.2). Hot-path consumers (Canny, percentile-stretch,
/// container-split orchestrator, watchdogs, decoder limits) read this
/// directly and never touch the JSON.
///
/// <para>
/// Any field absent from the persisted profile falls back to the v1
/// hardcoded constant (§6.5.4) — same defaults the bootstrap
/// migration stamps. That keeps a partially-migrated profile (e.g. a
/// schema-bump that added a new key) from crashing the pipeline before
/// the auto-tune backfill catches up.
/// </para>
/// </summary>
public sealed record ScannerThresholdSnapshot(
    int Version,
    int CannyLow,
    int CannyHigh,
    double PercentileLow,
    double PercentileHigh,
    int SplitDisagreementGuardPx,
    int PendingWithoutImagesHours,
    int MaxImageDimPx)
{
    /// <summary>v1 hardcoded values (§6.5.4) — used for missing-key fallback and as the bootstrap defaults.</summary>
    public static ScannerThresholdSnapshot V1Defaults(int version = 0) => new(
        Version: version,
        CannyLow: 50,
        CannyHigh: 150,
        PercentileLow: 0.5,
        PercentileHigh: 99.5,
        SplitDisagreementGuardPx: 50,
        PendingWithoutImagesHours: 72,
        MaxImageDimPx: 16384);
}
