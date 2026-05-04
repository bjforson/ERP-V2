using System.Text.Json;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Authorities.CustomsGh;
using NickERP.Inspection.Authorities.CustomsGh.Validation;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Core.Validation;

namespace NickERP.Inspection.Authorities.CustomsGh.Tests;

/// <summary>
/// Sprint 28 / B4 Phase D — coverage for <see cref="PortMatchRule"/>.
/// The rule ports v1's port-match (FS6000=TKD, ASE=TMA) into the v2
/// vendor-neutral engine; tests assert the happy-path Pass, the Error
/// when ports disagree, the configured-override path that lets operators
/// onboard a new scanner type, and the Skip postures for missing data.
/// </summary>
public sealed class PortMatchRuleTests
{
    private static PortMatchRule BuildRule(CustomsGhValidationOptions? opts = null)
        => new(Options.Create(opts ?? new CustomsGhValidationOptions()));

    private static ValidationContext BuildContext(
        string scannerType,
        string deliveryPlace,
        string locationCode = "tema",
        string clearanceType = "IM",
        string regimeCode = "40")
    {
        var caseId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var scanId = Guid.NewGuid();

        var @case = new InspectionCase
        {
            Id = caseId,
            LocationId = Guid.NewGuid(),
            SubjectIdentifier = "MSCU1234567",
            State = InspectionWorkflowState.Validated,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        var scan = new Scan
        {
            Id = scanId,
            CaseId = caseId,
            ScannerDeviceInstanceId = deviceId,
            CapturedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        var device = new ScannerDeviceInstance
        {
            Id = deviceId,
            TypeCode = scannerType,
            LocationId = @case.LocationId,
            DisplayName = "test",
            ConfigJson = "{}",
            TenantId = 1
        };
        var doc = new AuthorityDocument
        {
            Id = Guid.NewGuid(),
            CaseId = caseId,
            ExternalSystemInstanceId = Guid.NewGuid(),
            DocumentType = "BOE",
            ReferenceNumber = "C 999999 99",
            PayloadJson = JsonSerializer.Serialize(new
            {
                Header = new { ClearanceType = clearanceType, RegimeCode = regimeCode },
                ManifestDetails = new { DeliveryPlace = deliveryPlace }
            }),
            ReceivedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };

        return new ValidationContext(
            Case: @case,
            Scans: new[] { scan },
            Documents: new[] { doc },
            ScannerDevices: new[] { device },
            ScanArtifacts: Array.Empty<ScanArtifact>(),
            LocationCode: locationCode,
            TenantId: 1);
    }

    [Fact]
    public void FS6000_at_TKD_passes()
    {
        var rule = BuildRule();
        var ctx = BuildContext(scannerType: "FS6000", deliveryPlace: "WTTKD1MPS3");
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(ValidationSeverity.Info,
            because: "FS6000 sits at Takoradi per the operator-validated port map");
        outcome.RuleId.Should().Be(PortMatchRule.Id);
    }

    [Fact]
    public void ASE_at_TMA_passes()
    {
        var rule = BuildRule();
        var ctx = BuildContext(scannerType: "ASE", deliveryPlace: "WTTMA1MPS3");
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(ValidationSeverity.Info);
    }

    [Fact]
    public void FS6000_at_TMA_errors()
    {
        var rule = BuildRule();
        var ctx = BuildContext(scannerType: "FS6000", deliveryPlace: "WTTMA1MPS3");
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(ValidationSeverity.Error);
        outcome.Properties!["expectedPort"].Should().Be("TKD");
        outcome.Properties["observedPort"].Should().Be("TMA");
        outcome.Properties["scannerType"].Should().Be("FS6000");
    }

    [Fact]
    public void Operator_can_override_port_map_via_options()
    {
        // New scanner type "DUALVIEW" onboarded at Aflao via configuration.
        var opts = new CustomsGhValidationOptions
        {
            PortMatchMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DUALVIEW"] = "AFL"
            }
        };
        var rule = BuildRule(opts);
        var ctx = BuildContext(scannerType: "DUALVIEW", deliveryPlace: "WTAFL1MPS3");
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(ValidationSeverity.Info,
            because: "the override map mapped DUALVIEW->AFL and the BOE matches");
    }

    [Fact]
    public void Unknown_scanner_type_skips()
    {
        // Scanner type isn't in the default map — no false-positive.
        var rule = BuildRule();
        var ctx = BuildContext(scannerType: "MOCK", deliveryPlace: "WTTMA1MPS3");
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(ValidationSeverity.Skip);
        outcome.Message.Should().Contain("MOCK");
    }

    [Fact]
    public void No_scans_skips()
    {
        var rule = BuildRule();
        var noScansCtx = new ValidationContext(
            Case: new InspectionCase { Id = Guid.NewGuid(), TenantId = 1 },
            Scans: Array.Empty<Scan>(),
            Documents: Array.Empty<AuthorityDocument>(),
            ScannerDevices: Array.Empty<ScannerDeviceInstance>(),
            ScanArtifacts: Array.Empty<ScanArtifact>(),
            LocationCode: "",
            TenantId: 1);
        rule.Evaluate(noScansCtx).Severity.Should().Be(ValidationSeverity.Skip);
    }

    [Fact]
    public void Unparseable_DeliveryPlace_skips()
    {
        // ~0.35% v1 rate of malformed DeliveryPlace; rule must skip
        // rather than false-positive.
        var rule = BuildRule();
        var ctx = BuildContext(scannerType: "FS6000", deliveryPlace: "X");
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(ValidationSeverity.Skip);
    }

    [Fact]
    public void RuleId_is_dotted_lowercase()
    {
        BuildRule().RuleId.Should().Be("customsgh.port_match");
    }
}
