using NickERP.Inspection.Authorities.Abstractions;
using NickERP.Inspection.Authorities.CustomsGh;

namespace NickERP.Inspection.Authorities.CustomsGh.Tests;

/// <summary>
/// Two parallel-but-independent rules tested here:
///   1. Port-match — Tema scan + WTTKD (Takoradi) BOE → exactly one
///      <c>GH-PORT-MATCH</c> Error.
///   2. CMR→IM upgrade — same container has a CMR doc and a BOE doc →
///      exactly one <c>promote_cmr_to_im</c> mutation, with both reference
///      numbers in <c>DataJson</c>.
/// Both rules ported from v1 NSCIM ContainerValidationService.
/// </summary>
public sealed class CustomsGhRulesProviderTests
{
    [Fact]
    public async Task ValidateAsync_TemaScanWithTakoradiBoe_RaisesPortMatchError()
    {
        // Regression guarded: GH port-match rule must fire when a scan's
        // location code disagrees with the BOE's DeliveryPlace port code.
        var provider = new CustomsGhRulesProvider();

        var scan = new ScanSnapshot(
            ScannerTypeCode: "fs6000",
            LocationCode: "tema",
            Mode: "container",
            CapturedAt: DateTimeOffset.UtcNow,
            Metadata: new Dictionary<string, string>());

        var boeJson = """
        {
          "Header": { "DeclarationNumber": "C 123456 22", "ClearanceType": "IM", "RegimeCode": "40" },
          "ManifestDetails": { "DeliveryPlace": "WTTKD2MPS3" },
          "ContainerDetails": { "ContainerNumber": "MSCU1234567" }
        }
        """;
        var doc = new AuthorityDocumentSnapshot(
            DocumentType: "BOE",
            ReferenceNumber: "C 123456 22",
            PayloadJson: boeJson);

        var @case = new InspectionCaseData(
            CaseId: Guid.NewGuid(),
            TenantId: 1,
            SubjectType: "Container",
            SubjectIdentifier: "MSCU1234567",
            Documents: new[] { doc },
            Scans: new[] { scan });

        var result = await provider.ValidateAsync(@case);

        var portMatch = result.Violations
            .Where(v => v.RuleCode == "GH-PORT-MATCH")
            .ToArray();
        portMatch.Should().HaveCount(1);
        portMatch[0].Severity.Should().Be("Error");
    }

    [Fact]
    public async Task InferAsync_CmrPlusBoeForSameContainer_PromotesCmrToIm()
    {
        // Regression guarded: GH CMR→IM upgrade emits exactly one
        // promote_cmr_to_im mutation, with both reference numbers in DataJson,
        // when a case has both CMR and BOE documents for the same container.
        var provider = new CustomsGhRulesProvider();

        const string cmrRef = "CMR-2026-AAA";
        const string boeRef = "C 123456 22";

        var cmrJson = """
        {
          "Header": { "DeclarationNumber": "CMR-2026-AAA", "ClearanceType": "CMR", "RegimeCode": "80" },
          "ManifestDetails": { "DeliveryPlace": "WTTMA1MPS3" },
          "ContainerDetails": { "ContainerNumber": "MSCU1234567" }
        }
        """;
        var boeJson = """
        {
          "Header": { "DeclarationNumber": "C 123456 22", "ClearanceType": "IM", "RegimeCode": "40" },
          "ManifestDetails": { "DeliveryPlace": "WTTMA1MPS3" },
          "ContainerDetails": { "ContainerNumber": "MSCU1234567" }
        }
        """;

        var docs = new[]
        {
            new AuthorityDocumentSnapshot("CMR", cmrRef, cmrJson),
            new AuthorityDocumentSnapshot("BOE", boeRef, boeJson),
        };

        var @case = new InspectionCaseData(
            CaseId: Guid.NewGuid(),
            TenantId: 1,
            SubjectType: "Container",
            SubjectIdentifier: "MSCU1234567",
            Documents: docs,
            Scans: Array.Empty<ScanSnapshot>());

        var result = await provider.InferAsync(@case);

        var promotions = result.Mutations
            .Where(m => m.MutationKind == "promote_cmr_to_im")
            .ToArray();
        promotions.Should().HaveCount(1);
        promotions[0].DataJson.Should().Contain(cmrRef).And.Contain(boeRef);
    }
}
