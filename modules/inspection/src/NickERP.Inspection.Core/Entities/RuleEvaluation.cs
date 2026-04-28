using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Snapshot of one authority's rules pack run against an
/// <see cref="InspectionCase"/>. Persisted by
/// <c>CaseWorkflowService.EvaluateAuthorityRulesAsync</c> so the rules
/// pane survives a page reload — without this, the analyst has to click
/// "Run authority checks" again every time they navigate back to the
/// case.
///
/// <para>
/// One row per <em>(<see cref="CaseId"/>, <see cref="AuthorityCode"/>)</em>
/// snapshot — re-evaluation overwrites the existing row rather than
/// appending history. The most-recent-per-(case, authority) view is
/// the natural query the UI needs; full history lives on the audit
/// stream via the <c>nickerp.inspection.rules_evaluated</c> event.
/// </para>
///
/// <para>
/// Violations / mutations / provider-errors are persisted as JSON blobs
/// rather than separate tables — Sprint A1 explicitly punts on a
/// dedicated <c>RuleViolation</c> table; Postgres jsonb operators are
/// good enough for the analyst's "what did the rules say last time?"
/// query.
/// </para>
/// </summary>
public sealed class RuleEvaluation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CaseId { get; set; }

    /// <summary>Stable authority code (e.g. <c>"GH-CUSTOMS"</c>).</summary>
    public string AuthorityCode { get; set; } = string.Empty;

    /// <summary>When this evaluation was produced.</summary>
    public DateTimeOffset EvaluatedAt { get; set; }

    /// <summary>JSON array of <c>EvaluatedViolation</c>s for this authority.</summary>
    public string ViolationsJson { get; set; } = "[]";

    /// <summary>JSON array of <c>EvaluatedMutation</c>s for this authority.</summary>
    public string MutationsJson { get; set; } = "[]";

    /// <summary>JSON array of provider error strings (typically 0 or 1 entries — populated when this authority's pack threw).</summary>
    public string ProviderErrorsJson { get; set; } = "[]";

    public long TenantId { get; set; }
}
