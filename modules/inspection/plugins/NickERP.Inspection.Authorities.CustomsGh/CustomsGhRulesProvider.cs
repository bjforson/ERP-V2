using System.Text.Json;
using NickERP.Inspection.Authorities.Abstractions;
using NickERP.Platform.Plugins;

namespace NickERP.Inspection.Authorities.CustomsGh;

/// <summary>
/// Ghana Customs rule pack. Wires <see cref="IAuthorityRulesProvider"/>
/// against four rules ported point-in-time from v1 NSCIM
/// (<c>NickScanCentralImagingPortal.Services.ContainerValidation.ContainerValidationService</c>):
///
///   - <b>Port-match</b> — scan location must match BOE's declared port of
///     clearance. v1 keyed off scanner type (FS6000→TKD, ASE→TMA); v2 keys
///     off the location code via <see cref="GhCustoms.LocationToPort"/>,
///     which is the right level of indirection now that locations are
///     first-class objects (federation by location was the original vision).
///
///   - <b>Fyco import/export</b> — when a scanner reports a Fyco flag in its
///     metadata (<c>scanner.fyco_present</c>), it must agree with the BOE's
///     ClearanceType (IM vs EX). CMR is pre-BOE so the rule skips. v1's
///     FS6000 adapter exposed FycoPresent natively; the v2 FS6000 adapter
///     doesn't surface it yet — when it does, this rule starts firing.
///
///   - <b>Regime sanity</b> — RegimeCode is recognized. Unknown regimes get
///     a Warning (not an Error) — v1 learned the hard way that customs
///     adds new codes without telling anyone, and an Error here blocks
///     legitimate cases.
///
/// Inference (separate from validation):
///
///   - <b>CMR→IM upgrade</b> — when a case has both a CMR document and a BOE
///     for the same container, emit a <c>promote_cmr_to_im</c> mutation
///     suggesting the host upgrade the case's primary document type. v1
///     wrote this directly into the BOEDocument table
///     (OriginalClearanceType + CmrUpgradedAt); v2 surfaces the suggestion
///     and leaves the writeback to the host so audit trail goes through
///     DomainEvents.
///
/// Rules are independently togglable via the plugin instance config so a
/// deployment can disable any of them without redeploying.
/// </summary>
[Plugin("gh-customs", Module = "inspection")]
public sealed class CustomsGhRulesProvider : IAuthorityRulesProvider
{
    public string AuthorityCode => "GH-CUSTOMS";

    public Task<ValidationResult> ValidateAsync(InspectionCaseData @case, CancellationToken ct = default)
    {
        // Rules provider is wired by the host directly; per-instance config
        // doesn't pass through the contract (yet), so the toggles default
        // to "all on". Once the host carries config for rules-provider
        // plugins (mirroring how scanner adapters get ScannerDeviceConfig),
        // we'll honor EnablePortMatch/EnableFycoCheck/etc. from the manifest.
        var violations = new List<RuleViolation>();
        var boes = ReadBoeDocuments(@case);

        ApplyPortMatch(@case, boes, violations);
        ApplyFycoCheck(@case, boes, violations);
        ApplyRegimeCheck(boes, violations);

        return Task.FromResult(new ValidationResult(violations));
    }

    public Task<InferenceResult> InferAsync(InspectionCaseData @case, CancellationToken ct = default)
    {
        var mutations = new List<InferredMutation>();

        // CMR → IM upgrade: if we have at least one CMR doc and at least one
        // BOE doc on the same case, suggest promotion. The host decides
        // whether to apply (and emits the DomainEvent if so).
        var byType = @case.Documents
            .GroupBy(d => d.DocumentType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        if (byType.TryGetValue("CMR", out var cmrs) &&
            byType.TryGetValue("BOE", out var boes) &&
            cmrs.Count > 0 && boes.Count > 0)
        {
            var primaryBoe = boes[0];
            var primaryCmr = cmrs[0];
            var data = JsonSerializer.Serialize(new
            {
                cmrReference = primaryCmr.ReferenceNumber,
                boeReference = primaryBoe.ReferenceNumber,
                @case.SubjectIdentifier
            });
            mutations.Add(new InferredMutation(
                MutationKind: GhCustoms.MutationKinds.PromoteCmrToIm,
                DataJson: data,
                Reason: $"BOE {primaryBoe.ReferenceNumber} now exists for transit-loaded container {@case.SubjectIdentifier}; promote case primary doc type from CMR to IM."));
        }

        return Task.FromResult(mutations.Count == 0
            ? InferenceResult.NoOp
            : new InferenceResult(mutations));
    }

    // --- rule implementations ------------------------------------------

    private static void ApplyPortMatch(
        InspectionCaseData @case,
        IReadOnlyList<BoePayloadReader> boes,
        List<RuleViolation> violations)
    {
        if (@case.Scans.Count == 0 || boes.Count == 0) return;

        // Only check against the most recent scan — v1 used "first scan",
        // but the most recent is what the analyst is reviewing. If any
        // scan in the case lands at a port that disagrees with the BOE,
        // we still want to surface it; for now keep it simple.
        var scan = @case.Scans.OrderByDescending(s => s.CapturedAt).First();
        if (string.IsNullOrEmpty(scan.LocationCode)) return;
        if (!GhCustoms.LocationToPort.TryGetValue(scan.LocationCode, out var scanPort))
            return; // unknown location — skip silently, don't false-positive

        foreach (var boe in boes)
        {
            var dp = boe.DeliveryPlace;
            var boePort = GhCustoms.ExtractPortCode(dp);
            if (boePort is null) continue;

            if (!string.Equals(boePort, scanPort, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(new RuleViolation(
                    RuleCode: GhCustoms.RuleCodes.PortMatch,
                    Severity: GhCustoms.Severity.Error,
                    Message: $"Port mismatch: scanned at {scan.LocationCode} ({scanPort}) but BOE DeliveryPlace is {dp} ({boePort}).",
                    FieldPath: "ManifestDetails.DeliveryPlace"));
            }
        }
    }

    private static void ApplyFycoCheck(
        InspectionCaseData @case,
        IReadOnlyList<BoePayloadReader> boes,
        List<RuleViolation> violations)
    {
        // Pull the most recent Fyco-bearing scan. Adapters surface the
        // flag under "scanner.fyco_present" with values 1/0/Y/N/true/false.
        // Adapters that don't produce Fyco data simply don't populate the
        // key — rule degrades to no-op.
        var fycoScan = @case.Scans
            .OrderByDescending(s => s.CapturedAt)
            .FirstOrDefault(s => s.Metadata.ContainsKey("scanner.fyco_present"));

        if (fycoScan is null) return;

        var raw = fycoScan.Metadata["scanner.fyco_present"]?.Trim() ?? "";
        var isExportFlag = raw.Equals("1") ||
                           raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                           raw.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                           raw.Equals("yes", StringComparison.OrdinalIgnoreCase);

        foreach (var boe in boes)
        {
            var clearance = boe.ClearanceType;
            if (string.IsNullOrWhiteSpace(clearance)) continue;
            if (clearance.Equals(GhCustoms.ClearanceTypes.Transit, StringComparison.OrdinalIgnoreCase))
                continue; // CMR — direction not yet defined

            var isBoeExport = clearance.Equals(GhCustoms.ClearanceTypes.Export, StringComparison.OrdinalIgnoreCase);
            if (isExportFlag != isBoeExport)
            {
                violations.Add(new RuleViolation(
                    RuleCode: GhCustoms.RuleCodes.FycoCheck,
                    Severity: GhCustoms.Severity.Error,
                    Message: $"Fyco import/export mismatch: scanner reported FycoPresent='{raw}' but BOE ClearanceType is '{clearance}'.",
                    FieldPath: "Header.ClearanceType"));
            }
        }
    }

    private static void ApplyRegimeCheck(
        IReadOnlyList<BoePayloadReader> boes,
        List<RuleViolation> violations)
    {
        foreach (var boe in boes)
        {
            var regime = boe.RegimeCode;
            if (string.IsNullOrWhiteSpace(regime)) continue;

            if (!GhCustoms.RegimeCodes.ContainsKey(regime))
            {
                violations.Add(new RuleViolation(
                    RuleCode: GhCustoms.RuleCodes.RegimeCheck,
                    Severity: GhCustoms.Severity.Warning,
                    Message: $"Unrecognized Ghana customs regime code '{regime}'. Verify upstream classification before clearing.",
                    FieldPath: "Header.RegimeCode"));
            }
        }
    }

    private static IReadOnlyList<BoePayloadReader> ReadBoeDocuments(InspectionCaseData @case)
    {
        // Read every BOE-shaped document — both DocumentType="BOE" and
        // DocumentType="IM" carry the same JSON shape (Header /
        // ManifestDetails / ContainerDetails). CMR also carries the same
        // shape but with Header.ClearanceType="CMR" — include those too;
        // individual rules skip CMRs where appropriate (Fyco) but the
        // regime check still runs.
        var readers = new List<BoePayloadReader>(@case.Documents.Count);
        foreach (var doc in @case.Documents)
        {
            if (string.IsNullOrEmpty(doc.PayloadJson)) continue;
            var reader = BoePayloadReader.TryParse(doc.PayloadJson);
            if (reader is not null) readers.Add(reader);
        }
        return readers;
    }
}
