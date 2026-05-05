using NickERP.Inspection.Core.Entities;

namespace NickERP.Inspection.Core.Completeness;

/// <summary>
/// Sprint 31 / B5.1 — read-only snapshot of an <see cref="InspectionCase"/>
/// + the scaffolding requirements need to evaluate it.
///
/// <para>
/// The engine builds one <see cref="CompletenessContext"/> per case-evaluation
/// pass and hands the same instance to every registered
/// <see cref="ICompletenessRequirement"/>. Requirements MUST treat the
/// context as immutable — mutations are not propagated to the database,
/// and concurrent requirements will see a torn snapshot if any rule
/// writes.
/// </para>
///
/// <para>
/// Lives in <c>Core/Completeness</c> so vendor-neutral requirements ship
/// with every deployment regardless of which authority plugins are loaded.
/// CustomsGh-specific requirements (e.g. CMR port-state) live in the
/// CustomsGh adapter project and reach the same context shape.
/// </para>
/// </summary>
public sealed record CompletenessContext(
    InspectionCase Case,
    IReadOnlyList<Scan> Scans,
    IReadOnlyList<ScanArtifact> ScanArtifacts,
    IReadOnlyList<AuthorityDocument> Documents,
    IReadOnlyList<AnalystReview> AnalystReviews,
    IReadOnlyList<Verdict> Verdicts,
    long TenantId,
    IReadOnlyDictionary<string, decimal>? Thresholds = null)
{
    /// <summary>
    /// Convenience accessor — true when the case has at least one
    /// recorded analyst decision (verdict or analyst review).
    /// </summary>
    public bool HasAnalystDecision => Verdicts.Count > 0 || AnalystReviews.Count > 0;

    /// <summary>
    /// Documents-by-type lookup; case-insensitive on the document type.
    /// Returns an empty list when the type is absent — requirements can
    /// iterate without null-checking.
    /// </summary>
    public IReadOnlyList<AuthorityDocument> DocumentsOfType(string documentType)
    {
        if (string.IsNullOrEmpty(documentType)) return Array.Empty<AuthorityDocument>();
        return Documents
            .Where(d => string.Equals(d.DocumentType, documentType, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Sprint 36 / FU-completeness-percent-requirements — the effective
    /// numeric threshold for a percent-based requirement, resolved by
    /// the engine from (tenant override → built-in default). Returns
    /// <c>null</c> when the requirement is not percent-based or no
    /// threshold has been configured. Requirements pull this via
    /// <see cref="ICompletenessRequirement.RequirementId"/>.
    /// </summary>
    public decimal? ThresholdFor(string requirementId)
    {
        if (Thresholds is null || string.IsNullOrEmpty(requirementId)) return null;
        return Thresholds.TryGetValue(requirementId, out var v) ? v : (decimal?)null;
    }
}
