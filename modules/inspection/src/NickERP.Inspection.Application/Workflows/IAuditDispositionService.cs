using NickERP.Inspection.Core.Entities;

namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Background-safe application service that applies a completed audit
/// review's disposition to the case lifecycle.
/// </summary>
public interface IAuditDispositionService
{
    Task<AuditDispositionResult> RouteAsync(
        AuditReviewPayload payload,
        Guid workItemId,
        string? correlationId,
        CancellationToken ct = default);
}

public sealed record AuditDispositionResult(
    Guid CaseId,
    Guid ReviewId,
    string Outcome,
    VerdictDecision? VerdictDecision,
    bool EnqueuedSubmission);
