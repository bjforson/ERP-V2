using NickERP.Inspection.Core.Retention;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// One consignment going through inspection at one <see cref="Location"/>.
/// The atom of the inspection workflow — every <see cref="Scan"/>,
/// <see cref="AuthorityDocument"/>, <see cref="ReviewSession"/>, and
/// <see cref="Verdict"/> hangs off a case.
/// </summary>
public sealed class InspectionCase : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Where the inspection is happening.</summary>
    public Guid LocationId { get; set; }
    public Location? Location { get; set; }

    /// <summary>Optional — which station was used (set when the first scan lands).</summary>
    public Guid? StationId { get; set; }
    public Station? Station { get; set; }

    /// <summary>What kind of thing is being inspected.</summary>
    public CaseSubjectType SubjectType { get; set; } = CaseSubjectType.Container;

    /// <summary>The natural identifier — container number, plate / VIN, parcel barcode, etc.</summary>
    public string SubjectIdentifier { get; set; } = string.Empty;

    /// <summary>Type-specific subject details as JSON. Schema varies by <see cref="SubjectType"/>.</summary>
    public string SubjectPayloadJson { get; set; } = "{}";

    /// <summary>Current workflow state. Transitions emit DomainEvents.</summary>
    public InspectionWorkflowState State { get; set; } = InspectionWorkflowState.Open;

    /// <summary>When the case was opened.</summary>
    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>When the current state was entered (for SLA / dwell-time analytics).</summary>
    public DateTimeOffset StateEnteredAt { get; set; }

    /// <summary>When the case reached a terminal state (Closed or Cancelled). Null while open.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>The user who opened the case (or null for system-opened cases).</summary>
    public Guid? OpenedByUserId { get; set; }

    /// <summary>Currently-assigned analyst, when in <see cref="InspectionWorkflowState.Assigned"/>.</summary>
    public Guid? AssignedAnalystUserId { get; set; }

    /// <summary>Cross-service correlation id (matches the structured-log <c>CorrelationId</c> for the request that created the case).</summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Sprint 34 / B6 — review queue priority bucket. Drives ordering
    /// of cases on the analyst's <c>/reviews/queue</c> page and on the
    /// supervisor audit queue. Defaults to <see cref="ReviewQueue.Standard"/>;
    /// can be promoted by an admin or by the system (e.g. a rule
    /// violation in the validation engine flips the case to
    /// <see cref="ReviewQueue.Exception"/>). Vendor-neutral: specific
    /// SLA tiers / time budgets live on the <see cref="SlaWindow"/>
    /// settings, not here.
    /// </summary>
    public ReviewQueue ReviewQueue { get; set; } = ReviewQueue.Standard;

    /// <summary>
    /// Sprint 39 — retention posture for the case. Default
    /// <see cref="RetentionClass.Standard"/>. Drives the
    /// <c>RetentionEnforcerWorker</c>'s purge-candidate selection and
    /// the <c>RetentionService</c>'s admin reclassification action.
    /// Adopted from doc-analysis §9 + §13 + Annex D Table 35.
    /// </summary>
    public RetentionClass RetentionClass { get; set; } = RetentionClass.Standard;

    /// <summary>
    /// Sprint 39 — wallclock the current retention class was assigned.
    /// Updates on every <c>RetentionService.SetRetentionClassAsync</c>
    /// call. Captured for the audit-event payload + admin-UI history.
    /// </summary>
    public DateTimeOffset? RetentionClassSetAt { get; set; }

    /// <summary>
    /// Sprint 39 — Identity user id of the operator who set the current
    /// retention class. Null when the case is at the default class
    /// (no explicit set has happened yet).
    /// </summary>
    public Guid? RetentionClassSetByUserId { get; set; }

    /// <summary>
    /// Sprint 39 — legal-hold flag. When <see langword="true"/> the case
    /// is under investigation or litigation; the
    /// <c>RetentionEnforcerWorker</c> SKIPS this case regardless of
    /// retention class. WORM posture is documented (the bool +
    /// audit-trailed apply/release + cascading to artifacts gives
    /// evidentiary handling sufficient for pilot). Real WORM (write-once
    /// filesystem, immutable storage tier) is a post-pilot operator
    /// decision.
    /// </summary>
    public bool LegalHold { get; set; }

    /// <summary>
    /// Sprint 39 — wallclock the legal hold was applied. Null when the
    /// case is not (and never has been) under hold. Persists after
    /// release for audit-trail continuity.
    /// </summary>
    public DateTimeOffset? LegalHoldAppliedAt { get; set; }

    /// <summary>
    /// Sprint 39 — Identity user id of the operator who applied the
    /// most-recent legal hold. Persists after release.
    /// </summary>
    public Guid? LegalHoldAppliedByUserId { get; set; }

    /// <summary>
    /// Sprint 39 — free-text reason captured at hold-apply time
    /// (e.g. "subpoena 24-001", "internal investigation IR-742").
    /// Bounded to 500 chars. Persists after release.
    /// </summary>
    public string? LegalHoldReason { get; set; }

    public long TenantId { get; set; }

    public List<Scan> Scans { get; set; } = new();
    public List<AuthorityDocument> Documents { get; set; } = new();
    public List<ReviewSession> ReviewSessions { get; set; } = new();
    public List<OutboundSubmission> Submissions { get; set; } = new();
    public Verdict? Verdict { get; set; }
}
