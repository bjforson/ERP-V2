using Microsoft.Extensions.Options;
using NickERP.Inspection.Core.Validation;

namespace NickERP.Inspection.Authorities.CustomsGh.Validation;

/// <summary>
/// Sprint 28 / B4 — Customs Ghana scanner→port match rule.
///
/// <para>
/// Ports v1's <c>ContainerValidationService.ValidatePortMatchAsync</c>
/// (lines 773-833 in the 2026-05-02 commit) into the v2 vendor-neutral
/// engine. v1 keyed off the scanner type code (FS6000=TKD, ASE=TMA)
/// against <c>boedocuments.deliveryplace</c> positions 3-5; v2 keeps the
/// same scanner-type-driven posture but reads the mapping from
/// <see cref="CustomsGhValidationOptions.PortMatchMap"/> so operators
/// override per deployment via configuration.
/// </para>
///
/// <para>
/// Per the 2026-05-02 memory note: <c>ips1</c>/<c>ips2</c> stations are
/// TKD-side viewing stations (NOT Tema operators) — the rule maps by
/// scanner TYPE, not by operator id, so this stays correct.
/// </para>
///
/// <para>
/// Skip postures (no Error, no Warning):
/// </para>
/// <list type="bullet">
///   <item>case has no scans yet</item>
///   <item>case has no documents yet</item>
///   <item>scanner type isn't in the configured map (unknown vendor)</item>
///   <item>document doesn't expose a recognisable DeliveryPlace</item>
/// </list>
/// </summary>
public sealed class PortMatchRule : IValidationRule
{
    /// <summary>Stable rule identifier — matches v1's <c>GH-PORT-MATCH</c> code.</summary>
    public const string Id = "customsgh.port_match";

    private readonly IOptions<CustomsGhValidationOptions> _options;

    public PortMatchRule(IOptions<CustomsGhValidationOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string RuleId => Id;
    public string Description =>
        "Scanner port (FS6000=TKD, ASE=TMA by default) must match BOE DeliveryPlace.";

    public ValidationOutcome Evaluate(ValidationContext context)
    {
        if (context.Scans.Count == 0)
            return ValidationOutcome.Skip(Id, "case has no scans yet");
        if (context.Documents.Count == 0)
            return ValidationOutcome.Skip(Id, "case has no documents yet");

        var device = context.LatestScanDevice;
        if (device is null)
            return ValidationOutcome.Skip(Id, "latest scan's device row not loaded");

        var portMap = _options.Value.PortMatchMap
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!portMap.TryGetValue(device.TypeCode ?? "", out var expectedPort)
            || string.IsNullOrEmpty(expectedPort))
        {
            return ValidationOutcome.Skip(Id,
                $"scanner type '{device.TypeCode}' has no configured port mapping");
        }

        // The set of authority documents on a case can include CMR (no
        // BOE-style payload yet), BOE/IM, or whatever IcumsGh ingestion
        // attached. Walk every document; the first one with a parseable
        // DeliveryPlace decides the outcome (matches the v1 single-BOE
        // assumption — most cases have exactly one BOE).
        foreach (var doc in context.Documents)
        {
            if (string.IsNullOrEmpty(doc.PayloadJson)) continue;
            var reader = BoePayloadReader.TryParse(doc.PayloadJson);
            var dp = reader?.DeliveryPlace;
            if (string.IsNullOrEmpty(dp)) continue;

            var observedPort = GhCustoms.ExtractPortCode(dp);
            if (observedPort is null) continue;

            if (string.Equals(observedPort, expectedPort, StringComparison.OrdinalIgnoreCase))
                return ValidationOutcome.Pass(Id);

            return ValidationOutcome.Error(
                Id,
                $"Port mismatch: scanner type '{device.TypeCode}' expected {expectedPort}, "
                + $"BOE DeliveryPlace '{dp}' resolves to {observedPort}.",
                new Dictionary<string, string>
                {
                    ["scannerType"] = device.TypeCode ?? "",
                    ["expectedPort"] = expectedPort,
                    ["observedPort"] = observedPort,
                    ["deliveryPlace"] = dp,
                    ["documentType"] = doc.DocumentType ?? "",
                    ["documentReference"] = doc.ReferenceNumber ?? ""
                });
        }

        // No document had a parseable DeliveryPlace — Skip rather than
        // false-positive. v1 saw a ~0.35% rate of malformed values.
        return ValidationOutcome.Skip(Id,
            "no document on the case exposes a recognisable DeliveryPlace");
    }
}
