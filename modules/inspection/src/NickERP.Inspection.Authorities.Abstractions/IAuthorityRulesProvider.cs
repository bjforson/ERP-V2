namespace NickERP.Inspection.Authorities.Abstractions;

/// <summary>
/// Plugin contract for authority-specific rule packs. Each
/// implementation (e.g. <c>CustomsGh</c>) holds the validation +
/// inference logic specific to one customs authority — the v1 rules
/// (port-match, Fyco import/export, regime validation, CMR→IM upgrade)
/// land in <c>Authorities.CustomsGh</c> when ported.
/// </summary>
public interface IAuthorityRulesProvider
{
    /// <summary>Stable code (e.g. "GH-CUSTOMS").</summary>
    string AuthorityCode { get; }

    /// <summary>
    /// Validate a case against this authority's rules. Returns a list
    /// of violations (empty list = pass). Pure / read-only — does not
    /// mutate the case.
    /// </summary>
    Task<ValidationResult> ValidateAsync(InspectionCaseData @case, CancellationToken ct = default);

    /// <summary>
    /// Apply authority-specific inference to a case (e.g. CMR→IM
    /// upgrade when a CMR-typed message carries a regime code).
    /// Returns the inferred mutations the host should apply, or
    /// <see cref="InferenceResult.NoOp"/>.
    /// </summary>
    Task<InferenceResult> InferAsync(InspectionCaseData @case, CancellationToken ct = default);
}

/// <summary>
/// Wire shape of the case passed to the rules provider. Vendor-neutral;
/// concrete adapter packages can map their domain types to/from this.
/// </summary>
public sealed record InspectionCaseData(
    Guid CaseId,
    long TenantId,
    string SubjectType,
    string SubjectIdentifier,
    IReadOnlyList<AuthorityDocumentSnapshot> Documents);

/// <summary>Snapshot of one authority document attached to the case.</summary>
public sealed record AuthorityDocumentSnapshot(
    string DocumentType,
    string ReferenceNumber,
    string PayloadJson);

/// <summary>Validation outcome. Empty <see cref="Violations"/> = pass.</summary>
public sealed record ValidationResult(IReadOnlyList<RuleViolation> Violations)
{
    public bool IsValid => Violations.Count == 0;
    public static ValidationResult Pass { get; } = new(Array.Empty<RuleViolation>());
}

/// <summary>One rule violation.</summary>
public sealed record RuleViolation(
    string RuleCode,
    string Severity,
    string Message,
    string? FieldPath = null);

/// <summary>Inferred mutations to apply to a case before review.</summary>
public sealed record InferenceResult(IReadOnlyList<InferredMutation> Mutations)
{
    public static InferenceResult NoOp { get; } = new(Array.Empty<InferredMutation>());
    public bool HasMutations => Mutations.Count > 0;
}

/// <summary>One inferred mutation. Adapter-specific shape via <see cref="MutationKind"/> + <see cref="DataJson"/>.</summary>
public sealed record InferredMutation(
    string MutationKind,
    string DataJson,
    string Reason);
