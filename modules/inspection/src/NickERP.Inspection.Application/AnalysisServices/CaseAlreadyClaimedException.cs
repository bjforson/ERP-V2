namespace NickERP.Inspection.Application.AnalysisServices;

/// <summary>
/// Sprint 14 / VP6 Phase C — thrown by <see cref="CaseClaimService"/>
/// when a claim acquire loses the race against another analyst's claim
/// on the same case. Carries the existing claim's metadata so the UI can
/// show "claimed by [user] in [service]" without a follow-up round-trip.
/// </summary>
public sealed class CaseAlreadyClaimedException : InvalidOperationException
{
    /// <summary>The already-active claim's id.</summary>
    public Guid ExistingClaimId { get; }

    /// <summary>The user id that holds the active claim.</summary>
    public Guid ExistingClaimedByUserId { get; }

    /// <summary>The AnalysisService id under which the active claim was acquired.</summary>
    public Guid ExistingAnalysisServiceId { get; }

    /// <summary>UTC timestamp of the existing claim acquire.</summary>
    public DateTimeOffset ExistingClaimedAt { get; }

    public CaseAlreadyClaimedException(
        Guid caseId,
        Guid existingClaimId,
        Guid existingClaimedByUserId,
        Guid existingAnalysisServiceId,
        DateTimeOffset existingClaimedAt)
        : base(
            $"Case {caseId} is already claimed by user {existingClaimedByUserId} "
            + $"under AnalysisService {existingAnalysisServiceId} at {existingClaimedAt:u}.")
    {
        ExistingClaimId = existingClaimId;
        ExistingClaimedByUserId = existingClaimedByUserId;
        ExistingAnalysisServiceId = existingAnalysisServiceId;
        ExistingClaimedAt = existingClaimedAt;
    }
}
