using Microsoft.Extensions.Options;
using NickERP.Inspection.Core.Validation;

namespace NickERP.Inspection.Application.Validation;

/// <summary>
/// Sprint 28 — Vendor-neutral, built-in validation rules. Live in
/// <c>Application/Validation/</c> so they ship with every deployment
/// regardless of which authority plugins are installed; they encode
/// invariants that hold for inspection cases anywhere.
///
/// <para>
/// <b>Strict no-Ghana-data rule.</b> These rules MUST stay vendor-neutral
/// — port codes, regime codes, Fyco values, and any other Ghana-specific
/// data live in <c>NickERP.Inspection.Authorities.CustomsGh.Validation</c>.
/// </para>
/// </summary>
public static class BuiltInRuleIds
{
    /// <summary>
    /// "case must have at least one scan artifact attached before
    /// validation completes". Encodes the v1 invariant that a BOE
    /// without a scan is a bookkeeping case, not an inspection case.
    /// </summary>
    public const string RequiredScanArtifact = "required.scan_artifact";

    /// <summary>
    /// "case must have at least one customs declaration document
    /// attached before validation completes". Mirrors the v1 NSCIM
    /// "must have BOE / IM / CMR" check, expressed in vendor-neutral
    /// terms — the rule looks at the count of <see cref="Core.Entities.AuthorityDocument"/>
    /// rows, not their type.
    /// </summary>
    public const string RequiredCustomsDeclaration = "required.customs_declaration";

    /// <summary>
    /// "scan must arrive within a configurable wall-clock window of the
    /// case being opened". Guards against analyst leftover work — a
    /// case opened today against a scan from last month is operational
    /// noise, not an inspection.
    /// </summary>
    public const string ScanWithinWindow = "required.scan_within_window";
}

/// <summary>
/// "case has at least one scan artifact". Skip on no scans (case is
/// genuinely pre-scan); Error when scans exist but no
/// <see cref="Core.Entities.ScanArtifact"/> rows back them.
/// </summary>
public sealed class RequiredScanArtifactRule : IValidationRule
{
    public string RuleId => BuiltInRuleIds.RequiredScanArtifact;
    public string Description => "Case must carry at least one scan artifact.";

    public ValidationOutcome Evaluate(ValidationContext context)
    {
        if (context.Scans.Count == 0)
            return ValidationOutcome.Skip(RuleId, "case has no scans yet");

        var anyArtifact = context.ScanArtifacts.Count > 0;
        if (anyArtifact) return ValidationOutcome.Pass(RuleId);

        return ValidationOutcome.Error(
            RuleId,
            "Case has scans but no artifact rows — ingestion likely failed.",
            new Dictionary<string, string>
            {
                ["scanCount"] = context.Scans.Count.ToString(),
                ["artifactCount"] = "0"
            });
    }
}

/// <summary>
/// "case has at least one authority document attached". Used to gate the
/// case from advancing past Validated without proof of customs paperwork.
/// </summary>
public sealed class RequiredCustomsDeclarationRule : IValidationRule
{
    public string RuleId => BuiltInRuleIds.RequiredCustomsDeclaration;
    public string Description => "Case must have at least one authority document attached.";

    public ValidationOutcome Evaluate(ValidationContext context)
    {
        if (context.Documents.Count > 0) return ValidationOutcome.Pass(RuleId);

        return ValidationOutcome.Warn(
            RuleId,
            "Case has no customs declaration documents attached.",
            new Dictionary<string, string> { ["documentCount"] = "0" });
    }
}

/// <summary>
/// Configuration for <see cref="ScanWithinWindowRule"/> — how long after
/// case open is a scan allowed to land. Default 6 hours mirrors the v1
/// operational expectation; extend per deployment via
/// <c>builder.Services.Configure&lt;ScanWithinWindowOptions&gt;(...)</c>.
/// </summary>
public sealed class ScanWithinWindowOptions
{
    /// <summary>Wall-clock window in hours. Negative or zero disables the rule.</summary>
    public double WindowHours { get; set; } = 6.0;
}

/// <summary>
/// "every scan was captured within <see cref="ScanWithinWindowOptions.WindowHours"/>
/// of the case being opened". Stale scans get an Error; missing scans Skip.
/// </summary>
public sealed class ScanWithinWindowRule : IValidationRule
{
    private readonly IOptions<ScanWithinWindowOptions> _options;

    public ScanWithinWindowRule(IOptions<ScanWithinWindowOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string RuleId => BuiltInRuleIds.ScanWithinWindow;
    public string Description => "Every scan must land within the configured window of case open.";

    public ValidationOutcome Evaluate(ValidationContext context)
    {
        var windowHours = _options.Value.WindowHours;
        if (windowHours <= 0)
            return ValidationOutcome.Skip(RuleId, "rule disabled (WindowHours <= 0)");
        if (context.Scans.Count == 0)
            return ValidationOutcome.Skip(RuleId, "case has no scans yet");

        var window = TimeSpan.FromHours(windowHours);
        var stale = context.Scans
            .Where(s => s.CapturedAt - context.Case.OpenedAt > window
                     || context.Case.OpenedAt - s.CapturedAt > window)
            .ToList();
        if (stale.Count == 0) return ValidationOutcome.Pass(RuleId);

        return ValidationOutcome.Error(
            RuleId,
            $"Found {stale.Count} scan(s) outside the {windowHours}h window of case open.",
            new Dictionary<string, string>
            {
                ["windowHours"] = windowHours.ToString("F1"),
                ["staleScanCount"] = stale.Count.ToString(),
                ["caseOpenedAt"] = context.Case.OpenedAt.ToString("O")
            });
    }
}
