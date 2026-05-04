using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Validation;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Core.Validation;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 28 / B4 Phase D — coverage for the three built-in vendor-neutral
/// rules in <see cref="BuiltInRules"/>. Each rule must hold the
/// vendor-neutrality invariant (no Ghana strings appear in the
/// outcome message / properties bag).
/// </summary>
public sealed class BuiltInValidationRulesTests
{
    private static ValidationContext BuildContext(
        InspectionCase @case,
        IEnumerable<Scan>? scans = null,
        IEnumerable<AuthorityDocument>? docs = null,
        IEnumerable<ScanArtifact>? artifacts = null)
    {
        return new ValidationContext(
            Case: @case,
            Scans: scans?.ToList() ?? new List<Scan>(),
            Documents: docs?.ToList() ?? new List<AuthorityDocument>(),
            ScannerDevices: new List<ScannerDeviceInstance>(),
            ScanArtifacts: artifacts?.ToList() ?? new List<ScanArtifact>(),
            LocationCode: "any",
            TenantId: 1);
    }

    private static InspectionCase NewCase()
        => new()
        {
            Id = Guid.NewGuid(),
            LocationId = Guid.NewGuid(),
            SubjectIdentifier = "X",
            State = InspectionWorkflowState.Validated,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };

    [Fact]
    public void RequiredScanArtifactRule_skips_when_no_scans()
    {
        var rule = new RequiredScanArtifactRule();
        var ctx = BuildContext(NewCase());
        rule.Evaluate(ctx).Severity.Should().Be(ValidationSeverity.Skip);
    }

    [Fact]
    public void RequiredScanArtifactRule_passes_when_scan_has_artifact()
    {
        var rule = new RequiredScanArtifactRule();
        var c = NewCase();
        var scan = new Scan { Id = Guid.NewGuid(), CaseId = c.Id, CapturedAt = DateTimeOffset.UtcNow, TenantId = 1 };
        var artifact = new ScanArtifact
        {
            Id = Guid.NewGuid(),
            ScanId = scan.Id,
            ArtifactKind = "Primary",
            StorageUri = "noop://x",
            MimeType = "image/png",
            ContentHash = "abc",
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        var ctx = BuildContext(c, scans: new[] { scan }, artifacts: new[] { artifact });
        rule.Evaluate(ctx).Severity.Should().Be(ValidationSeverity.Info);
    }

    [Fact]
    public void RequiredScanArtifactRule_errors_when_scan_lacks_artifact()
    {
        var rule = new RequiredScanArtifactRule();
        var c = NewCase();
        var scan = new Scan { Id = Guid.NewGuid(), CaseId = c.Id, CapturedAt = DateTimeOffset.UtcNow, TenantId = 1 };
        var ctx = BuildContext(c, scans: new[] { scan });
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(ValidationSeverity.Error);
        outcome.Properties!["scanCount"].Should().Be("1");
        outcome.Properties["artifactCount"].Should().Be("0");
    }

    [Fact]
    public void RequiredCustomsDeclarationRule_warns_when_no_documents()
    {
        var rule = new RequiredCustomsDeclarationRule();
        var ctx = BuildContext(NewCase());
        rule.Evaluate(ctx).Severity.Should().Be(ValidationSeverity.Warning);
    }

    [Fact]
    public void RequiredCustomsDeclarationRule_passes_when_any_document()
    {
        var rule = new RequiredCustomsDeclarationRule();
        var c = NewCase();
        var doc = new AuthorityDocument
        {
            Id = Guid.NewGuid(),
            CaseId = c.Id,
            ExternalSystemInstanceId = Guid.NewGuid(),
            DocumentType = "BOE",
            ReferenceNumber = "ref-1",
            PayloadJson = "{}",
            ReceivedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        var ctx = BuildContext(c, docs: new[] { doc });
        rule.Evaluate(ctx).Severity.Should().Be(ValidationSeverity.Info);
    }

    [Fact]
    public void ScanWithinWindowRule_disabled_via_options_skips()
    {
        var rule = new ScanWithinWindowRule(Options.Create(new ScanWithinWindowOptions { WindowHours = 0 }));
        var c = NewCase();
        var scan = new Scan { Id = Guid.NewGuid(), CaseId = c.Id, CapturedAt = DateTimeOffset.UtcNow, TenantId = 1 };
        rule.Evaluate(BuildContext(c, scans: new[] { scan })).Severity.Should().Be(ValidationSeverity.Skip);
    }

    [Fact]
    public void ScanWithinWindowRule_passes_when_scan_within_window()
    {
        var rule = new ScanWithinWindowRule(Options.Create(new ScanWithinWindowOptions { WindowHours = 6 }));
        var c = NewCase();
        // Case opened now; scan 1h after = within 6h window.
        var scan = new Scan
        {
            Id = Guid.NewGuid(),
            CaseId = c.Id,
            CapturedAt = c.OpenedAt.AddHours(1),
            TenantId = 1
        };
        rule.Evaluate(BuildContext(c, scans: new[] { scan })).Severity.Should().Be(ValidationSeverity.Info);
    }

    [Fact]
    public void ScanWithinWindowRule_errors_when_scan_outside_window()
    {
        var rule = new ScanWithinWindowRule(Options.Create(new ScanWithinWindowOptions { WindowHours = 6 }));
        var c = NewCase();
        var scan = new Scan
        {
            Id = Guid.NewGuid(),
            CaseId = c.Id,
            CapturedAt = c.OpenedAt.AddHours(48),
            TenantId = 1
        };
        var outcome = rule.Evaluate(BuildContext(c, scans: new[] { scan }));
        outcome.Severity.Should().Be(ValidationSeverity.Error);
        outcome.Properties!["staleScanCount"].Should().Be("1");
        outcome.Properties["windowHours"].Should().Be("6.0");
    }

    [Fact]
    public void Built_in_rule_ids_are_dotted_lowercase_no_vendor_strings()
    {
        var ids = new[]
        {
            new RequiredScanArtifactRule().RuleId,
            new RequiredCustomsDeclarationRule().RuleId,
            new ScanWithinWindowRule(Options.Create(new ScanWithinWindowOptions())).RuleId
        };
        ids.Should().AllSatisfy(id =>
        {
            id.Should().MatchRegex(@"^[a-z][a-z0-9_.]+$",
                because: "vendor-neutral built-in rule ids must be dotted-lowercase ASCII");
            id.Should().NotContain("customsgh", because: "core engine rules must stay vendor-neutral — Ghana strings live in plugins");
            id.Should().NotContain("ghana");
        });
    }
}
