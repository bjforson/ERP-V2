using System.Text.Json;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Authorities.CustomsGh;
using NickERP.Inspection.Authorities.CustomsGh.Validation;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Core.Validation;

namespace NickERP.Inspection.Authorities.CustomsGh.Tests;

/// <summary>
/// Sprint 28 / B4 Phase D — coverage for <see cref="CmrPortRule"/>.
/// CMR is half-state pre-BOE; the rule emits Skip in that posture and
/// Info for transit cargo (regime 80/88/89) so the analyst doesn't
/// conflate silence with passing.
/// </summary>
public sealed class CmrPortRuleTests
{
    private static CmrPortRule BuildRule()
        => new(Options.Create(new CustomsGhValidationOptions()));

    private static AuthorityDocument BuildDoc(string clearanceType, string regimeCode, string docType = "BOE")
        => new()
        {
            Id = Guid.NewGuid(),
            CaseId = Guid.NewGuid(),
            ExternalSystemInstanceId = Guid.NewGuid(),
            DocumentType = docType,
            ReferenceNumber = "ref-1",
            PayloadJson = JsonSerializer.Serialize(new
            {
                Header = new { ClearanceType = clearanceType, RegimeCode = regimeCode },
                ManifestDetails = new { DeliveryPlace = "WTTMA1MPS3" }
            }),
            ReceivedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };

    private static ValidationContext BuildContext(IReadOnlyList<AuthorityDocument> docs)
    {
        return new ValidationContext(
            Case: new InspectionCase { Id = Guid.NewGuid(), TenantId = 1 },
            Scans: Array.Empty<Scan>(),
            Documents: docs,
            ScannerDevices: Array.Empty<ScannerDeviceInstance>(),
            ScanArtifacts: Array.Empty<ScanArtifact>(),
            LocationCode: "tema",
            TenantId: 1);
    }

    [Fact]
    public void Half_state_cmr_skips()
    {
        var rule = BuildRule();
        var ctx = BuildContext(new[] { BuildDoc(clearanceType: "CMR", regimeCode: "", docType: "CMR") });
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(ValidationSeverity.Skip,
            because: "CMR with blank regime is half-state — waiting for BOE");
    }

    [Fact]
    public void Empty_regime_and_clearance_is_half_state()
    {
        var rule = BuildRule();
        var ctx = BuildContext(new[] { BuildDoc(clearanceType: "", regimeCode: "") });
        rule.Evaluate(ctx).Severity.Should().Be(ValidationSeverity.Skip);
    }

    [Theory]
    [InlineData("80")]
    [InlineData("88")]
    [InlineData("89")]
    public void Transit_regime_emits_info_posture(string regime)
    {
        var rule = BuildRule();
        var ctx = BuildContext(new[] { BuildDoc(clearanceType: "IM", regimeCode: regime) });
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(ValidationSeverity.Info,
            because: "regime 80/88/89 is true transit; analyst sees the posture so they don't conflate silence with passing");
        outcome.Properties!["transitDocCount"].Should().Be("1");
        outcome.Properties["exemplarRegime"].Should().Be(regime);
    }

    [Fact]
    public void Classified_non_transit_passes()
    {
        var rule = BuildRule();
        var ctx = BuildContext(new[] { BuildDoc(clearanceType: "IM", regimeCode: "40") });
        rule.Evaluate(ctx).Severity.Should().Be(ValidationSeverity.Info,
            because: "regime 40 is direct-import — Pass posture lets the broader rule pack handle it");
    }

    [Fact]
    public void No_documents_skips()
    {
        var rule = BuildRule();
        var ctx = BuildContext(Array.Empty<AuthorityDocument>());
        rule.Evaluate(ctx).Severity.Should().Be(ValidationSeverity.Skip);
    }

    [Fact]
    public void Mixed_transit_and_classified_emits_info()
    {
        // Some real cases carry both a transit BOE and a follow-on
        // import doc. Rule prioritises the transit posture so the
        // analyst sees it.
        var rule = BuildRule();
        var ctx = BuildContext(new[]
        {
            BuildDoc(clearanceType: "IM", regimeCode: "40"),
            BuildDoc(clearanceType: "IM", regimeCode: "80"),
        });
        rule.Evaluate(ctx).Severity.Should().Be(ValidationSeverity.Info);
    }

    [Fact]
    public void RuleId_is_dotted_lowercase()
    {
        BuildRule().RuleId.Should().Be("customsgh.cmr_port_state");
    }
}
