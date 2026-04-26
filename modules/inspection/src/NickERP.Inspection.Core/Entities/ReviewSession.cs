using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// One analyst's session reviewing a case. A case can have multiple
/// sessions (different analysts, dual-review enforcement, re-reviews
/// after challenges); the latest active session drives the workflow.
/// </summary>
public sealed class ReviewSession : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CaseId { get; set; }
    public InspectionCase? Case { get; set; }

    /// <summary>Canonical user id of the analyst.</summary>
    public Guid AnalystUserId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Null while the session is in progress.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Outcome label — "completed", "abandoned", "escalated", etc. Free-form.</summary>
    public string Outcome { get; set; } = "in-progress";

    public long TenantId { get; set; }

    public List<AnalystReview> Reviews { get; set; } = new();
}
