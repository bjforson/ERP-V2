using NickERP.Inspection.Core.Entities;

namespace NickERP.Inspection.Application.Validation;

/// <summary>
/// Sprint 48 / Phase B — FU-validation-rule-evaluation-snapshot.
///
/// <para>
/// Read-side companion to the <see cref="ValidationEngine"/> snapshot
/// writer. Used by the case-detail page (and any admin drill-down) to
/// hydrate the validation pane on cold reload without re-running the
/// engine. Returns rows in <see cref="ValidationRuleSnapshot.EvaluatedAt"/>
/// descending order so the latest snapshot per rule is the first one
/// the caller encounters in a per-rule deduplication pass.
/// </para>
///
/// <para>
/// <b>Append-only semantics.</b> The snapshot table accumulates one row
/// per (case, rule) per evaluation; this reader exposes the full history
/// for a case, ordered newest-first. Callers that only want the latest
/// per rule should dedupe in memory by <see cref="ValidationRuleSnapshot.RuleId"/>
/// (the order guarantee makes the first-occurrence-wins idiom correct).
/// </para>
/// </summary>
public interface IValidationSnapshotReader
{
    /// <summary>
    /// Read every snapshot row for a case, ordered by
    /// <see cref="ValidationRuleSnapshot.EvaluatedAt"/> descending.
    /// Returns an empty list when the engine has never run against the
    /// case (or when every snapshot has been purged via retention —
    /// not a current path, but reserved).
    /// </summary>
    Task<IReadOnlyList<ValidationRuleSnapshot>> ListByCaseAsync(
        Guid caseId,
        CancellationToken ct = default);

    /// <summary>
    /// Read snapshot rows for a specific rule across the tenant, ordered
    /// by <see cref="ValidationRuleSnapshot.EvaluatedAt"/> descending.
    /// Used by the admin /admin/rules/{ruleId} drill-down so the page
    /// can show recent fires without going through the audit-events
    /// table. <paramref name="take"/> is bounded to 200 to keep the
    /// payload on the page reasonable.
    /// </summary>
    Task<IReadOnlyList<ValidationRuleSnapshot>> ListByRuleAsync(
        long tenantId,
        string ruleId,
        int take = 50,
        CancellationToken ct = default);
}
