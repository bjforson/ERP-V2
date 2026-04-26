namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Workflow state for an <see cref="InspectionCase"/>. Linear progression;
/// transitions are enforced by application code, not by the DB. Each
/// transition emits a <c>DomainEvent</c> so the audit log captures the
/// full path.
/// </summary>
public enum InspectionWorkflowState
{
    /// <summary>Case opened. No scans yet, no documents pulled, no analyst assigned.</summary>
    Open = 0,

    /// <summary>Authority documents fetched + completeness rules passed; ready to assign.</summary>
    Validated = 10,

    /// <summary>An analyst has picked up the case (or it's been assigned to one).</summary>
    Assigned = 20,

    /// <summary>Analyst submitted findings; awaiting verdict.</summary>
    Reviewed = 30,

    /// <summary>Verdict recorded; awaiting outbound submission.</summary>
    Verdict = 40,

    /// <summary>Verdict submitted to the external authority system; awaiting closure.</summary>
    Submitted = 50,

    /// <summary>Terminal — case closed, archive-only.</summary>
    Closed = 60,

    /// <summary>Terminal — abandoned (e.g. scanner pulled the wrong subject, manual cancel).</summary>
    Cancelled = 90
}
