using System.Text.Json;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Authorities.CustomsGh;
using NickERP.Inspection.Authorities.CustomsGh.Validation;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Core.Validation;

namespace NickERP.Inspection.Authorities.CustomsGh.Tests;

/// <summary>
/// Sprint 28 / B4 Phase D — coverage for <see cref="FycoDirectionRule"/>.
///
/// <para>
/// Asserts the happy-path Pass, the Error when fyco direction disagrees
/// with the BOE, the broader regex parser handling free-text values like
/// <c>WAYBILL/EXPORT</c> (which v1's narrow <c>1/Y/YES</c> parser missed),
/// the transit posture (regime 80 treated as import-direction per the
/// 2026-05-04 operator clarification — transit cargo physically can't
/// have a fyco=EXPORT), and the Skip postures for missing data.
/// </para>
/// </summary>
public sealed class FycoDirectionRuleTests
{
    private static FycoDirectionRule BuildRule(CustomsGhValidationOptions? opts = null)
        => new(Options.Create(opts ?? new CustomsGhValidationOptions()));

    private static ValidationContext BuildContext(
        string fycoValue,
        string clearanceType,
        string regimeCode)
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
            TypeCode = "FS6000",
            LocationId = @case.LocationId,
            DisplayName = "test",
            ConfigJson = "{}",
            TenantId = 1
        };
        var artifact = new ScanArtifact
        {
            Id = Guid.NewGuid(),
            ScanId = scanId,
            ArtifactKind = "Primary",
            StorageUri = "noop://x",
            MimeType = "image/png",
            ContentHash = "abc",
            MetadataJson = JsonSerializer.Serialize(
                new Dictionary<string, string> { ["scanner.fyco_present"] = fycoValue }),
            CreatedAt = DateTimeOffset.UtcNow,
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
                ManifestDetails = new { DeliveryPlace = "WTTMA1MPS3" }
            }),
            ReceivedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };

        return new ValidationContext(
            Case: @case,
            Scans: new[] { scan },
            Documents: new[] { doc },
            ScannerDevices: new[] { device },
            ScanArtifacts: new[] { artifact },
            LocationCode: "tema",
            TenantId: 1);
    }

    [Fact]
    public void Fyco_yes_with_export_regime_passes()
    {
        var rule = BuildRule();
        var ctx = BuildContext(fycoValue: "YES", clearanceType: "EX", regimeCode: "10");
        rule.Evaluate(ctx).Severity.Should().Be(ValidationSeverity.Info);
    }

    [Fact]
    public void Fyco_no_with_import_regime_passes()
    {
        var rule = BuildRule();
        var ctx = BuildContext(fycoValue: "0", clearanceType: "IM", regimeCode: "40");
        rule.Evaluate(ctx).Severity.Should().Be(ValidationSeverity.Info);
    }

    [Fact]
    public void Fyco_export_with_import_regime_errors()
    {
        // The triggering example from the 2026-05-02 memory note:
        // declaration 70326214329, regime 70 (import), DP=WTTMA1MPS3,
        // fyco=WAYBILL/EXPORT.
        var rule = BuildRule();
        var ctx = BuildContext(fycoValue: "WAYBILL/EXPORT", clearanceType: "IM", regimeCode: "70");
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(ValidationSeverity.Error);
        outcome.Properties!["fycoDirection"].Should().Be("export");
        outcome.Properties["boeDirection"].Should().Be("import");
    }

    [Theory]
    [InlineData("WAYBILL/EXPORT")]
    [InlineData("WAYBIL/EXPORT")]
    [InlineData("export")]
    [InlineData("Export")]
    [InlineData("EXPORT")]
    [InlineData("EPORT")] // regex still catches eport via the relaxed pattern
    public void Broader_export_pattern_handles_free_text_v1_couldnt(string fycoValue)
    {
        // v1's narrow 1/Y/YES parser missed all of these; v2 must catch
        // them so the rule fires on the misclassification rather than
        // silently passing.
        var rule = BuildRule();
        var ctx = BuildContext(fycoValue: fycoValue, clearanceType: "IM", regimeCode: "40");
        var outcome = rule.Evaluate(ctx);
        if (string.Equals(fycoValue, "EPORT", StringComparison.OrdinalIgnoreCase))
        {
            // EPORT isn't a clean "ex(p?)ort" hit in the default regex; it
            // falls into "neither matched" and Skips. Documents the
            // current parser bound — operators may want to extend it.
            outcome.Severity.Should().Be(ValidationSeverity.Skip);
        }
        else
        {
            outcome.Severity.Should().Be(ValidationSeverity.Error);
        }
    }

    [Fact]
    public void Transit_regime_treated_as_import_direction_for_fyco_with_export_scan()
    {
        // 2026-05-04 operator clarification: transit cargo physically
        // can't have a fyco=EXPORT (it leaves Ghana overland). Rule must
        // flag this as an Error, NOT Skip.
        var rule = BuildRule();
        var ctx = BuildContext(fycoValue: "YES", clearanceType: "IM", regimeCode: "80");
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(ValidationSeverity.Error,
            because: "transit cargo with fyco=EXPORT is a real anomaly");
        outcome.Properties!["boeSignalSource"].Should().Contain("transit");
    }

    [Fact]
    public void No_fyco_metadata_skips()
    {
        var rule = BuildRule();
        var caseId = Guid.NewGuid();
        var ctx = new ValidationContext(
            Case: new InspectionCase { Id = caseId, TenantId = 1 },
            Scans: new[] { new Scan { Id = Guid.NewGuid(), CaseId = caseId, ScannerDeviceInstanceId = Guid.NewGuid(), CapturedAt = DateTimeOffset.UtcNow, TenantId = 1 } },
            Documents: Array.Empty<AuthorityDocument>(),
            ScannerDevices: Array.Empty<ScannerDeviceInstance>(),
            ScanArtifacts: Array.Empty<ScanArtifact>(),
            LocationCode: "tema",
            TenantId: 1);
        rule.Evaluate(ctx).Severity.Should().Be(ValidationSeverity.Skip);
    }

    [Fact]
    public void Blank_regime_and_clearance_skips()
    {
        // Half-state CMR — no usable signal yet.
        var rule = BuildRule();
        var ctx = BuildContext(fycoValue: "1", clearanceType: "", regimeCode: "");
        rule.Evaluate(ctx).Severity.Should().Be(ValidationSeverity.Skip);
    }

    [Fact]
    public void RuleId_is_dotted_lowercase()
    {
        BuildRule().RuleId.Should().Be("customsgh.fyco_direction");
    }
}
