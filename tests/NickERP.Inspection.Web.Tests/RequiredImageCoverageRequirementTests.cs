using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Completeness;
using NickERP.Inspection.Core.Completeness;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 36 / FU-completeness-percent-requirements — Phase D coverage
/// for the new <see cref="RequiredImageCoverageRequirement"/> built-in
/// (the first percent-based built-in completeness requirement).
///
/// <para>
/// Asserts (a) the requirement's pure-evaluation surface — Skip on no
/// scans, Incomplete on no artifacts, Pass at full coverage, Incomplete
/// below threshold; (b) the engine integration — tenant override on
/// <see cref="ICompletenessRequirementProvider.GetSettingsAsync"/>'s
/// <c>MinThreshold</c> wins over the built-in default; (c) the audit
/// emission — <c>inspection.completeness.threshold_used</c> fires when
/// the engine consults a threshold, with the right
/// <c>source</c> tag (tenant-override vs built-in-default).
/// </para>
/// </summary>
public sealed class RequiredImageCoverageRequirementTests : IDisposable
{
    private readonly InspectionDbContext _db;
    private readonly TenantContext _tenant;
    private readonly InMemoryCompletenessRequirementProvider _settings;
    private readonly RecordingEventPublisher _events;

    public RequiredImageCoverageRequirementTests()
    {
        var options = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("img-coverage-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new InspectionDbContext(options);
        _tenant = new TenantContext();
        _tenant.SetTenant(1);
        _settings = new InMemoryCompletenessRequirementProvider();
        _events = new RecordingEventPublisher();
    }

    public void Dispose() => _db.Dispose();

    private static InspectionCase NewCase()
        => new()
        {
            Id = Guid.NewGuid(),
            LocationId = Guid.NewGuid(),
            SubjectIdentifier = "C1",
            State = InspectionWorkflowState.Validated,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };

    private static CompletenessContext BuildCtx(
        InspectionCase @case,
        IEnumerable<Scan> scans,
        IEnumerable<ScanArtifact> artifacts,
        decimal? threshold = null)
    {
        var thresholds = threshold.HasValue
            ? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                [BuiltInCompletenessRequirementIds.RequiredImageCoverage] = threshold.Value
            }
            : null;
        return new CompletenessContext(
            Case: @case,
            Scans: scans.ToList(),
            ScanArtifacts: artifacts.ToList(),
            Documents: new List<AuthorityDocument>(),
            AnalystReviews: new List<AnalystReview>(),
            Verdicts: new List<Verdict>(),
            TenantId: 1,
            Thresholds: thresholds);
    }

    [Fact]
    public void Skips_when_no_scans()
    {
        var rule = new RequiredImageCoverageRequirement();
        var ctx = BuildCtx(NewCase(), Array.Empty<Scan>(), Array.Empty<ScanArtifact>());
        rule.Evaluate(ctx).Severity.Should().Be(CompletenessSeverity.Skip);
    }

    [Fact]
    public void Incomplete_when_scan_has_no_artifacts()
    {
        var rule = new RequiredImageCoverageRequirement();
        var c = NewCase();
        var scan = new Scan { Id = Guid.NewGuid(), CaseId = c.Id, CapturedAt = DateTimeOffset.UtcNow, TenantId = 1 };
        var ctx = BuildCtx(c, new[] { scan }, Array.Empty<ScanArtifact>());
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(CompletenessSeverity.Incomplete);
        outcome.Properties!["observedValue"].Should().Be("0");
    }

    [Fact]
    public void Pass_when_all_expected_kinds_present()
    {
        var rule = new RequiredImageCoverageRequirement();
        var c = NewCase();
        var scan = new Scan { Id = Guid.NewGuid(), CaseId = c.Id, CapturedAt = DateTimeOffset.UtcNow, TenantId = 1 };
        var kinds = new[] { "Primary", "SideView", "Material", "IR" };
        var artifacts = kinds.Select(k => new ScanArtifact
        {
            Id = Guid.NewGuid(),
            ScanId = scan.Id,
            ArtifactKind = k,
            StorageUri = "noop://",
            MimeType = "image/png",
            ContentHash = k.ToLowerInvariant(),
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        }).ToList();

        var ctx = BuildCtx(c, new[] { scan }, artifacts);
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(CompletenessSeverity.Pass);
        outcome.Properties!["observedValue"].Should().Be("1");
        outcome.Properties["distinctKinds"].Should().Be("4");
    }

    [Fact]
    public void Incomplete_below_default_threshold()
    {
        // 2 of 4 kinds = 50% < 85% default threshold.
        var rule = new RequiredImageCoverageRequirement();
        var c = NewCase();
        var scan = new Scan { Id = Guid.NewGuid(), CaseId = c.Id, CapturedAt = DateTimeOffset.UtcNow, TenantId = 1 };
        var artifacts = new[] { "Primary", "SideView" }.Select(k => new ScanArtifact
        {
            Id = Guid.NewGuid(), ScanId = scan.Id, ArtifactKind = k,
            StorageUri = "noop://", MimeType = "image/png", ContentHash = k,
            CreatedAt = DateTimeOffset.UtcNow, TenantId = 1
        }).ToList();

        var ctx = BuildCtx(c, new[] { scan }, artifacts);
        var outcome = rule.Evaluate(ctx);
        outcome.Severity.Should().Be(CompletenessSeverity.Incomplete);
        // 0.5 vs default 0.85 — observedValue captures the ratio for
        // the engine's threshold_used audit.
        outcome.Properties!["observedValue"].Should().Be("0.5");
    }

    [Fact]
    public void Threshold_override_on_context_changes_the_pass_bar()
    {
        var rule = new RequiredImageCoverageRequirement();
        var c = NewCase();
        var scan = new Scan { Id = Guid.NewGuid(), CaseId = c.Id, CapturedAt = DateTimeOffset.UtcNow, TenantId = 1 };
        var artifacts = new[] { "Primary", "SideView" }.Select(k => new ScanArtifact
        {
            Id = Guid.NewGuid(), ScanId = scan.Id, ArtifactKind = k,
            StorageUri = "noop://", MimeType = "image/png", ContentHash = k,
            CreatedAt = DateTimeOffset.UtcNow, TenantId = 1
        }).ToList();

        // 2/4 = 50% — passes when threshold lowered to 0.50; fails at default 0.85.
        var passCtx = BuildCtx(c, new[] { scan }, artifacts, threshold: 0.50m);
        rule.Evaluate(passCtx).Severity.Should().Be(CompletenessSeverity.Pass);

        var failCtx = BuildCtx(c, new[] { scan }, artifacts, threshold: 0.85m);
        rule.Evaluate(failCtx).Severity.Should().Be(CompletenessSeverity.Incomplete);
    }

    [Fact]
    public void Distinct_kinds_collapses_duplicates()
    {
        var rule = new RequiredImageCoverageRequirement();
        var c = NewCase();
        var scan = new Scan { Id = Guid.NewGuid(), CaseId = c.Id, CapturedAt = DateTimeOffset.UtcNow, TenantId = 1 };
        // Two scans, each with a "Primary" — should count as 1 distinct kind.
        var artifacts = new[] { "Primary", "primary" }.Select(k => new ScanArtifact
        {
            Id = Guid.NewGuid(), ScanId = scan.Id, ArtifactKind = k,
            StorageUri = "noop://", MimeType = "image/png", ContentHash = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow, TenantId = 1
        }).ToList();

        var ctx = BuildCtx(c, new[] { scan }, artifacts);
        var outcome = rule.Evaluate(ctx);
        outcome.Properties!["distinctKinds"].Should().Be("1");
    }

    [Fact]
    public void DefaultMinThreshold_is_0_85()
    {
        var rule = new RequiredImageCoverageRequirement();
        rule.DefaultMinThreshold.Should().Be(0.85m);
    }

    [Fact]
    public void Outcome_carries_no_ghana_strings()
    {
        var rule = new RequiredImageCoverageRequirement();
        var c = NewCase();
        var scan = new Scan { Id = Guid.NewGuid(), CaseId = c.Id, CapturedAt = DateTimeOffset.UtcNow, TenantId = 1 };
        var ctx = BuildCtx(c, new[] { scan }, Array.Empty<ScanArtifact>());
        var outcome = rule.Evaluate(ctx);
        var combined = (outcome.Message + string.Join(" ", outcome.Properties?.Values ?? Array.Empty<string>())
                        + string.Join(" ", outcome.MissingFields ?? Array.Empty<string>()))
            .ToLowerInvariant();
        var forbidden = new[] { "fs6000", "icums", "ghana", "ghc", "boe", "regime", "fyco" };
        foreach (var f in forbidden)
            combined.Should().NotContain(f);
    }

    // ---------------------------------------------------------------
    // Engine-level tests via CompletenessChecker
    // ---------------------------------------------------------------

    private CompletenessChecker NewChecker(IEnumerable<ICompletenessRequirement>? requirements = null)
    {
        var reqs = requirements?.ToList() ?? new List<ICompletenessRequirement>
        {
            new RequiredImageCoverageRequirement()
        };
        return new CompletenessChecker(
            _db, reqs, _settings, _events, _tenant, NullLogger<CompletenessChecker>.Instance);
    }

    private async Task<InspectionCase> SeedCaseWithArtifactsAsync(int distinctKinds)
    {
        var c = NewCase();
        _db.Cases.Add(c);
        var scan = new Scan { Id = Guid.NewGuid(), CaseId = c.Id, CapturedAt = DateTimeOffset.UtcNow, TenantId = 1 };
        _db.Scans.Add(scan);
        var allKinds = new[] { "Primary", "SideView", "Material", "IR" };
        for (int i = 0; i < distinctKinds; i++)
        {
            _db.ScanArtifacts.Add(new ScanArtifact
            {
                Id = Guid.NewGuid(), ScanId = scan.Id, ArtifactKind = allKinds[i],
                StorageUri = "noop://", MimeType = "image/png", ContentHash = allKinds[i],
                CreatedAt = DateTimeOffset.UtcNow, TenantId = 1
            });
        }
        await _db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task Engine_uses_built_in_default_when_no_tenant_override()
    {
        var c = await SeedCaseWithArtifactsAsync(distinctKinds: 4); // full coverage
        var checker = NewChecker();
        var result = await checker.EvaluateAsync(c.Id);
        result.Outcomes.Should().HaveCount(1);
        result.Outcomes[0].Severity.Should().Be(CompletenessSeverity.Pass);

        // Audit event fires; source is built-in-default since no override.
        var thresholdEvents = _events.Events
            .Where(e => e.EventType == "inspection.completeness.threshold_used")
            .ToList();
        thresholdEvents.Should().HaveCount(1);
        thresholdEvents[0].Payload.GetProperty("source").GetString().Should().Be("built-in-default");
        thresholdEvents[0].Payload.GetProperty("threshold").GetDecimal().Should().Be(0.85m);
    }

    [Fact]
    public async Task Engine_consults_tenant_override_when_set()
    {
        var c = await SeedCaseWithArtifactsAsync(distinctKinds: 2); // 50% coverage

        // Set per-tenant override 0.5; coverage 0.5 should pass.
        _settings.SetThreshold(1, BuiltInCompletenessRequirementIds.RequiredImageCoverage, 0.5m);

        var checker = NewChecker();
        var result = await checker.EvaluateAsync(c.Id);
        result.Outcomes[0].Severity.Should().Be(CompletenessSeverity.Pass);

        // Audit event source = tenant-override.
        var thresholdEvents = _events.Events
            .Where(e => e.EventType == "inspection.completeness.threshold_used")
            .ToList();
        thresholdEvents.Should().HaveCount(1);
        thresholdEvents[0].Payload.GetProperty("source").GetString().Should().Be("tenant-override");
        thresholdEvents[0].Payload.GetProperty("threshold").GetDecimal().Should().Be(0.5m);
    }

    [Fact]
    public async Task Engine_uses_default_when_override_below_coverage_makes_incomplete()
    {
        var c = await SeedCaseWithArtifactsAsync(distinctKinds: 2); // 50% — below default 0.85
        var checker = NewChecker();
        var result = await checker.EvaluateAsync(c.Id);

        result.Outcomes[0].Severity.Should().Be(CompletenessSeverity.Incomplete);
        var thresholdEvents = _events.Events
            .Where(e => e.EventType == "inspection.completeness.threshold_used")
            .ToList();
        thresholdEvents.Should().HaveCount(1);
        thresholdEvents[0].Payload.GetProperty("observedValue").GetDecimal().Should().Be(0.5m);
    }

    [Fact]
    public async Task Disabled_requirement_does_not_emit_threshold_used_audit()
    {
        var c = await SeedCaseWithArtifactsAsync(distinctKinds: 2);

        // Disable the requirement entirely.
        _settings.Disable(1, BuiltInCompletenessRequirementIds.RequiredImageCoverage);
        var checker = NewChecker();
        await checker.EvaluateAsync(c.Id);

        var thresholdEvents = _events.Events
            .Count(e => e.EventType == "inspection.completeness.threshold_used");
        Assert.Equal(0, thresholdEvents);
    }

    [Fact]
    public async Task Boolean_requirements_dont_emit_threshold_used_audit()
    {
        // RequiredCustomsDeclaration has no DefaultMinThreshold — the
        // engine must not emit a threshold_used event for it.
        var c = await SeedCaseWithArtifactsAsync(distinctKinds: 0);
        // Add a customs document so RequiredCustomsDeclaration passes.
        _db.AuthorityDocuments.Add(new AuthorityDocument
        {
            Id = Guid.NewGuid(), CaseId = c.Id, ExternalSystemInstanceId = Guid.NewGuid(),
            DocumentType = "BOE", ReferenceNumber = "ref-1", PayloadJson = "{}",
            ReceivedAt = DateTimeOffset.UtcNow, TenantId = 1
        });
        await _db.SaveChangesAsync();

        var checker = new CompletenessChecker(
            _db,
            new ICompletenessRequirement[] { new RequiredCustomsDeclarationRequirement() },
            _settings, _events, _tenant, NullLogger<CompletenessChecker>.Instance);
        await checker.EvaluateAsync(c.Id);

        _events.Events.Any(e => e.EventType == "inspection.completeness.threshold_used")
            .Should().BeFalse();
    }

    [Fact]
    public async Task ResolveEffectiveThresholdsAsync_returns_tenant_override_when_set()
    {
        _settings.SetThreshold(1, BuiltInCompletenessRequirementIds.RequiredImageCoverage, 0.7m);
        var checker = NewChecker();
        var resolved = await checker.ResolveEffectiveThresholdsAsync(1);
        resolved.Should().ContainKey(BuiltInCompletenessRequirementIds.RequiredImageCoverage);
        resolved[BuiltInCompletenessRequirementIds.RequiredImageCoverage].Should().Be(0.7m);
    }

    [Fact]
    public async Task ResolveEffectiveThresholdsAsync_returns_built_in_default_otherwise()
    {
        var checker = NewChecker();
        var resolved = await checker.ResolveEffectiveThresholdsAsync(1);
        resolved[BuiltInCompletenessRequirementIds.RequiredImageCoverage].Should().Be(0.85m);
    }
}
