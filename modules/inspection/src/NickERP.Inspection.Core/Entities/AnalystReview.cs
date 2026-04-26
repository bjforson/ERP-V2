using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// The analyst's work product within a <see cref="ReviewSession"/>. Carries
/// the ML-telemetry fields that make this layer feed future training data
/// (time-to-decision, ROI interactions, confidence, disagreement, post-hoc
/// outcome). Capturing this from day 1 is cheap; retrofitting later is
/// brutal.
/// </summary>
public sealed class AnalystReview : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ReviewSessionId { get; set; }
    public ReviewSession? Session { get; set; }

    /// <summary>How long between session start and verdict commit. Training signal for triage priority.</summary>
    public int TimeToDecisionMs { get; set; }

    /// <summary>
    /// JSON array of ROI interactions: <c>[{x,y,w,h,zoom,dwellMs}]</c>.
    /// Tells future ML which regions analysts focus on; enables auto-ROI suggestion.
    /// </summary>
    public string RoiInteractionsJson { get; set; } = "[]";

    /// <summary>Analyst confidence 0.0–1.0. <b>Required</b> field; calibrates analyst reliability over time.</summary>
    public double ConfidenceScore { get; set; }

    /// <summary>JSON array of prior verdict attempts (analyst flipped their mind). Uncertainty signal.</summary>
    public string VerdictChangesJson { get; set; } = "[]";

    /// <summary>Number of peer-review disagreements when dual-review is enabled. 0 when single-review.</summary>
    public int PeerDisagreementCount { get; set; }

    /// <summary>JSON of customs / authority feedback that came back later (seizure, clearance, false-positive). True label for supervised learning.</summary>
    public string? PostHocOutcomeJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public long TenantId { get; set; }

    public List<Finding> Findings { get; set; } = new();
}
