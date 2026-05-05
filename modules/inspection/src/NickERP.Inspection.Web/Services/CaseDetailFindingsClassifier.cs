using NickERP.Inspection.Core.Entities;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 32 FU-C — split a list of <see cref="Finding"/> rows into the
/// two distinct UI buckets the case-detail page needs to render after
/// Sprint 28 (validation engine) shipped.
///
/// <para>
/// Sprint 28's <c>ValidationEngine</c> writes a Finding per rule outcome
/// with <see cref="Finding.FindingType"/> = <c>validation.{ruleId}</c>.
/// Sprint 31 ships completeness Findings with <c>completeness.*</c>.
/// Both are SYSTEM-AUTHORED — the analyst didn't draw an ROI; the engine
/// drew a conclusion. They render in their own pane on
/// <c>CaseDetail.razor</c>'s "Rules + findings" tab.
/// </para>
///
/// <para>
/// Everything else (notably <c>analyst.annotation</c> from
/// <see cref="AnalystAnnotationService"/>) is an analyst observation —
/// the column-set the analyst expects is different (ROI summary,
/// optional note). They keep their existing pane.
/// </para>
///
/// <para>
/// The split is by FindingType <b>prefix</b> rather than an enum so
/// future modules can introduce their own system-Finding namespaces
/// (e.g. <c>completeness.*</c> for Sprint 31) without recompiling this
/// file. The prefix list is the source of truth.
/// </para>
/// </summary>
public static class CaseDetailFindingsClassifier
{
    /// <summary>
    /// FindingType prefixes that are system-authored (engine-driven) and
    /// belong in the "Validation rules" pane on CaseDetail. Match is
    /// case-insensitive on the prefix only — anything after the dot is
    /// caller-defined.
    /// </summary>
    public static readonly IReadOnlyList<string> SystemFindingTypePrefixes = new[]
    {
        "validation.",
        "completeness.",
    };

    /// <summary>
    /// True when <paramref name="findingType"/> belongs to the
    /// system-authored pane. Null / blank values default to false (the
    /// analyst pane), matching the existing default for legacy rows.
    /// </summary>
    public static bool IsSystemAuthored(string? findingType)
    {
        if (string.IsNullOrWhiteSpace(findingType)) return false;
        foreach (var prefix in SystemFindingTypePrefixes)
        {
            if (findingType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Split <paramref name="findings"/> into (validation, analyst)
    /// preserving the input order within each bucket. The caller is
    /// expected to pass an already-sorted list (CaseDetail sorts by
    /// <c>CreatedAt</c> descending before classification).
    /// </summary>
    public static (IReadOnlyList<Finding> Validation, IReadOnlyList<Finding> Analyst)
        Partition(IEnumerable<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var validation = new List<Finding>();
        var analyst = new List<Finding>();
        foreach (var f in findings)
        {
            if (IsSystemAuthored(f.FindingType)) validation.Add(f);
            else analyst.Add(f);
        }
        return (validation, analyst);
    }

    /// <summary>
    /// Extract the rule id from a system-authored Finding's FindingType
    /// so the UI can hyperlink to <c>/admin/rules/{ruleId}</c>. Returns
    /// the post-prefix portion, or null when the FindingType doesn't
    /// match a known prefix or has nothing after the dot. Case is
    /// preserved on the returned segment — callers (the rules-admin
    /// page) lower-case for lookup.
    /// </summary>
    public static string? ExtractRuleId(string? findingType)
    {
        if (string.IsNullOrWhiteSpace(findingType)) return null;
        foreach (var prefix in SystemFindingTypePrefixes)
        {
            if (findingType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var tail = findingType[prefix.Length..];
                return string.IsNullOrWhiteSpace(tail) ? null : tail;
            }
        }
        return null;
    }
}
