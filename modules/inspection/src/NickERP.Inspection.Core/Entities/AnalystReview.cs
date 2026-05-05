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

    /// <summary>
    /// Sprint 34 / B6 — kind of review work this row represents. Drives
    /// the throughput-by-review-type dashboards and gates which review
    /// page rendered the row. Defaults to <see cref="ReviewType.Standard"/>
    /// for backwards-compat: existing rows seeded by
    /// <c>CaseWorkflowService.SetVerdictAsync</c> stay on the legacy
    /// flow without a backfill.
    /// </summary>
    public ReviewType ReviewType { get; set; } = ReviewType.Standard;

    /// <summary>
    /// Sprint 34 / B6 — terminal outcome of this review. Free-form (
    /// e.g. <c>completed</c>, <c>concur</c>, <c>dissent</c>,
    /// <c>escalated</c>, <c>abandoned</c>). Null while the review is
    /// in progress; populated by
    /// <c>IReviewWorkflow.CompleteReviewAsync</c>. Distinct from
    /// <see cref="ReviewSession.Outcome"/>: a session can hold multiple
    /// reviews (analyst pass + supervisor pass + ...), each with its
    /// own outcome.
    /// </summary>
    public string? Outcome { get; set; }

    /// <summary>
    /// Sprint 34 / B6 — when the review entered a terminal state. Null
    /// while in-progress.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Sprint 34 / B6 — user who started this review. Distinct from
    /// <see cref="ReviewSession.AnalystUserId"/> for the audit-review
    /// case where a supervisor reviews an analyst's session — the
    /// session's analyst is the analyst, the AnalystReview's
    /// StartedByUserId is the supervisor.
    /// </summary>
    public Guid? StartedByUserId { get; set; }

    public long TenantId { get; set; }

    public List<Finding> Findings { get; set; } = new();
}
