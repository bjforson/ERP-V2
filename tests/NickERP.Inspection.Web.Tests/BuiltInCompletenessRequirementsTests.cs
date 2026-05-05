using NickERP.Inspection.Application.Completeness;
using NickERP.Inspection.Core.Completeness;
using NickERP.Inspection.Core.Entities;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 31 / B5.1 Phase D — coverage for the three built-in
/// vendor-neutral completeness requirements. Each requirement must
/// hold the vendor-neutrality invariant (no Ghana strings appear in
/// the outcome message / properties bag / missing fields).
/// </summary>
public sealed class BuiltInCompletenessRequirementsTests
{
    private static CompletenessContext BuildContext(
        InspectionCase @case,
        IEnumerable<Scan>? scans = null,
        IEnumerable<ScanArtifact>? artifacts = null,
        IEnumerable<AuthorityDocument>? docs = null,
        IEnumerable<AnalystReview>? reviews = null,
        IEnumerable<Verdict>? verdicts = null)
    {
        return new CompletenessContext(
            Case: @case,
            Scans: scans?.ToList() ?? new List<Scan>(),
            ScanArtifacts: artifacts?.ToList() ?? new List<ScanArtifact>(),
            Documents: docs?.ToList() ?? new List<AuthorityDocument>(),
            AnalystReviews: reviews?.ToList() ?? new List<AnalystReview>(),
            Verdicts: verdicts?.ToList() ?? new List<Verdict>(),
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
    public void RequiredScanArtifact_skips_when_no_scans()
    {
        var rule = new RequiredScanArtifactRequirement();
        var ctx = BuildContext(NewCase());
        rule.Evaluate(ctx).Severity.Should().Be(CompletenessSeverity.Skip);
    }

    [Fact]
    public void RequiredScanArtifact_passes_when_scan_has_artifact()
    {
        var rule = new RequiredScanArtifactRequirement();
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
        rule.Evaluate(ctx).Severity.Should().Be(CompletenessSeverity.Pass);
    }

    [Fact]
    public void RequiredScanArtifact_incomplete_when_scan_lacks_artifact()
    {
        var rule = new RequiredScanArtifactRequirement();
        var c = NewCase();
        var scan = new Scan { Id = Guid.NewGuid(), CaseId = c.Id, CapturedAt = DateTimeOffset.UtcNow, TenantId = 1 };
        var ctx = BuildContext(c, scans: new[] { scan });
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(CompletenessSeverity.Incomplete);
        outcome.MissingFields.Should().NotBeNull().And.Contain("scan-artifact");
        outcome.Properties!["scanCount"].Should().Be("1");
        outcome.Properties["artifactCount"].Should().Be("0");
    }

    [Fact]
    public void RequiredCustomsDeclaration_incomplete_when_no_documents()
    {
        var rule = new RequiredCustomsDeclarationRequirement();
        var ctx = BuildContext(NewCase());
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(CompletenessSeverity.Incomplete);
        outcome.MissingFields.Should().NotBeNull().And.Contain("customs-declaration");
    }

    [Fact]
    public void RequiredCustomsDeclaration_passes_when_any_document()
    {
        var rule = new RequiredCustomsDeclarationRequirement();
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
        rule.Evaluate(ctx).Severity.Should().Be(CompletenessSeverity.Pass);
    }

    [Fact]
    public void RequiredAnalystDecision_skips_pre_intake()
    {
        var rule = new RequiredAnalystDecisionRequirement();
        var ctx = BuildContext(NewCase()); // no scans, no docs
        rule.Evaluate(ctx).Severity.Should().Be(CompletenessSeverity.Skip);
    }

    [Fact]
    public void RequiredAnalystDecision_partial_when_missing_decision()
    {
        var rule = new RequiredAnalystDecisionRequirement();
        var c = NewCase();
        var scan = new Scan { Id = Guid.NewGuid(), CaseId = c.Id, CapturedAt = DateTimeOffset.UtcNow, TenantId = 1 };
        var doc = new AuthorityDocument
        {
            Id = Guid.NewGuid(), CaseId = c.Id, ExternalSystemInstanceId = Guid.NewGuid(),
            DocumentType = "BOE", ReferenceNumber = "ref", PayloadJson = "{}",
            ReceivedAt = DateTimeOffset.UtcNow, TenantId = 1
        };
        var ctx = BuildContext(c, scans: new[] { scan }, docs: new[] { doc });
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(CompletenessSeverity.PartiallyComplete);
        outcome.MissingFields.Should().Contain("analyst-decision");
    }

    [Fact]
    public void RequiredAnalystDecision_passes_with_verdict()
    {
        var rule = new RequiredAnalystDecisionRequirement();
        var c = NewCase();
        var scan = new Scan { Id = Guid.NewGuid(), CaseId = c.Id, CapturedAt = DateTimeOffset.UtcNow, TenantId = 1 };
        var doc = new AuthorityDocument
        {
            Id = Guid.NewGuid(), CaseId = c.Id, ExternalSystemInstanceId = Guid.NewGuid(),
            DocumentType = "BOE", ReferenceNumber = "ref", PayloadJson = "{}",
            ReceivedAt = DateTimeOffset.UtcNow, TenantId = 1
        };
        var verdict = new Verdict
        {
            Id = Guid.NewGuid(), CaseId = c.Id, Decision = VerdictDecision.Clear,
            Basis = "ok", DecidedAt = DateTimeOffset.UtcNow, DecidedByUserId = Guid.NewGuid(),
            TenantId = 1
        };
        var ctx = BuildContext(c, scans: new[] { scan }, docs: new[] { doc }, verdicts: new[] { verdict });
        rule.Evaluate(ctx).Severity.Should().Be(CompletenessSeverity.Pass);
    }

    [Fact]
    public void Outcomes_carry_no_ghana_strings()
    {
        // Vendor-neutrality invariant — no Ghana / customs / FS6000 /
        // ICUMS / port / regime strings in any built-in outcome.
        var requirements = new ICompletenessRequirement[]
        {
            new RequiredScanArtifactRequirement(),
            new RequiredCustomsDeclarationRequirement(),
            new RequiredAnalystDecisionRequirement()
        };
        var c = NewCase();
        var scan = new Scan { Id = Guid.NewGuid(), CaseId = c.Id, CapturedAt = DateTimeOffset.UtcNow, TenantId = 1 };
        var ctx = BuildContext(c, scans: new[] { scan });
        var combined = string.Concat(requirements.Select(r =>
            r.Evaluate(ctx) is { } outcome
                ? outcome.Message + string.Join(" ", outcome.Properties?.Values ?? Array.Empty<string>())
                  + string.Join(" ", outcome.MissingFields ?? Array.Empty<string>())
                : ""));
        var forbidden = new[] { "fs6000", "icums", "ghana", "ghc", "boe", "regime", "fyco" };
        foreach (var f in forbidden)
            combined.ToLowerInvariant().Should().NotContain(f, because: $"built-in outcomes must stay vendor-neutral; '{f}' is Ghana-domain.");
    }
}
