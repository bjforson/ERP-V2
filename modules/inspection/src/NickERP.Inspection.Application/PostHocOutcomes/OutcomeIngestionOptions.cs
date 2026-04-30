namespace NickERP.Inspection.Application.PostHocOutcomes;

/// <summary>
/// Configuration for the post-hoc outcome ingestion pipeline (§6.11.3).
/// Bound from <c>PostHocOutcomes:</c> in <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// Defaults match v1 production cadence (cloned from
/// <c>IcumPipelineOrchestratorService.BatchIntervalMinutes = 30</c> in
/// <c>NickScanCentralImagingPortal.API/appsettings.json:282</c>) — see
/// §6.11.3. Per-instance overrides land in
/// <c>ExternalSystemConfig.payload.posthoc_pull</c>.
/// </remarks>
public sealed class OutcomeIngestionOptions
{
    /// <summary>
    /// Master enable. Defaults to <c>true</c> so a host that boots without
    /// any <c>PostHocRolloutPhase</c> rows simply iterates an empty set
    /// and idles; turning this off skips even the discovery sweep.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default cycle interval — every 30 minutes per §6.11.3 / v1 parity.
    /// Per-instance override coming via the rollout-phase row's payload
    /// (Sprint 14 follow-up, see §6.11.3 hybrid-mode cadence section).
    /// </summary>
    public TimeSpan PullInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// First-cycle delay — gives the host a chance to finish migrations
    /// and plugin discovery before the worker tries to resolve adapters.
    /// </summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Window overlap on every pull — §6.11.8. Ensures authority-side
    /// late commits in the prior window don't slip through the cracks.
    /// </summary>
    public TimeSpan WindowOverlap { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Skew buffer on the upper bound of every pull window — §6.11.8.
    /// Never query rows the authority hasn't had a moment to settle.
    /// </summary>
    public TimeSpan SkewBuffer { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Default <c>OutcomeWindowKind</c> when an instance config doesn't
    /// pin one explicitly. <c>DecidedAt</c> is the most common authority
    /// semantic (see §6.11.2 / Q-N3); per-authority adapters override per
    /// their API. Stored as a string so this options class doesn't need
    /// a project reference to <c>NickERP.Inspection.ExternalSystems.Abstractions</c>;
    /// the worker parses it via <see cref="System.Enum.TryParse{TEnum}(string, bool, out TEnum)"/>.
    /// </summary>
    public string DefaultWindowKind { get; set; } = "DecidedAt";
}
