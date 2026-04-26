using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// The composite decision on an <see cref="InspectionCase"/>. One verdict
/// per case (it can be revised — `RevisedVerdictId` chains to the
/// previous one when an analyst's call gets overturned by a supervisor).
/// </summary>
public sealed class Verdict : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CaseId { get; set; }
    public InspectionCase? Case { get; set; }

    /// <summary>The decision label.</summary>
    public VerdictDecision Decision { get; set; } = VerdictDecision.Inconclusive;

    /// <summary>Free-form basis (one or two sentences from the analyst).</summary>
    public string Basis { get; set; } = string.Empty;

    public DateTimeOffset DecidedAt { get; set; }

    /// <summary>Canonical user id of the analyst / supervisor who set the verdict.</summary>
    public Guid DecidedByUserId { get; set; }

    /// <summary>If this verdict revised an earlier one, the prior verdict's id. Null = first verdict on the case.</summary>
    public Guid? RevisedVerdictId { get; set; }

    public long TenantId { get; set; }
}

/// <summary>
/// The composite-decision label set. Stable enum values — they show up in
/// audit events, integrations, statistics; renaming requires a migration.
/// </summary>
public enum VerdictDecision
{
    /// <summary>No issue — release the consignment.</summary>
    Clear = 0,
    /// <summary>Hold for physical inspection / examination.</summary>
    HoldForInspection = 10,
    /// <summary>Seize the consignment.</summary>
    Seize = 20,
    /// <summary>Image / documents don't support a confident call. Often re-routes to a senior analyst.</summary>
    Inconclusive = 30
}
