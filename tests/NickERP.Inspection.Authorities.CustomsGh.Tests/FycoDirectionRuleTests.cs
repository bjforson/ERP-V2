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
    public void Broader_export_pattern_handles_free_text_v1_couldnt(string fycoValue)
    {
        // v1's narrow 1/Y/YES parser missed all of these; v2 must catch
        // them so the rule fires on the misclassification rather than
        // silently passing.
        var rule = BuildRule();
        var ctx = BuildContext(fycoValue: fycoValue, clearanceType: "IM", regimeCode: "40");
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(ValidationSeverity.Error);
    }

    [Theory]
    [InlineData("EPORT")]            // missing X
    [InlineData("EXORT")]            // missing P
    [InlineData("EXPRT")]            // missing O
    [InlineData("WAYBILL/EPORT")]    // typo embedded in free-text
    [InlineData("waybill/exort")]    // typo + lowercase
    public void Sprint37_export_typo_patterns_now_match(string fycoValue)
    {
        // Sprint 37 / FU-fyco-export-pattern-eport — three observed
        // single-letter typos of EXPORT (EPORT/EXORT/EXPRT) are now
        // matched by the default export regex. Confirm they're
        // treated as export-direction (so the rule fires on
        // misclassification against an import regime) rather than
        // silently Skipping. Embedded-in-free-text variants confirm
        // the word-boundary still matches.
        var rule = BuildRule();
        var ctx = BuildContext(fycoValue: fycoValue, clearanceType: "IM", regimeCode: "40");
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(ValidationSeverity.Error,
            because: $"'{fycoValue}' is one of the Sprint 37 known-typo set and should resolve to export-direction.");
        outcome.Properties!["fycoDirection"].Should().Be("export");
    }

    [Theory]
    [InlineData("EXPROT")]   // anagram — intentionally NOT matched
    [InlineData("X-PORT")]   // hyphenation — intentionally NOT matched
    [InlineData("EXPRORT")]  // extra letter — intentionally NOT matched
    public void Sprint37_truly_exotic_typos_still_skip(string fycoValue)
    {
        // Sprint 37 / FU-fyco-export-pattern-eport — the tightening
        // intentionally STOPS at the three observed single-letter
        // omissions of EXPORT. Anagrams, hyphenations, and
        // extra-letter typos are not auto-matched; they need an
        // operator-call to confirm before being added to the regex.
        // This test pins that boundary so a future loosening is a
        // visible test failure rather than a silent change.
        var rule = BuildRule();
        var ctx = BuildContext(fycoValue: fycoValue, clearanceType: "IM", regimeCode: "40");
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(ValidationSeverity.Skip,
            because: $"'{fycoValue}' is intentionally outside the Sprint 37 known-typo set.");
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
