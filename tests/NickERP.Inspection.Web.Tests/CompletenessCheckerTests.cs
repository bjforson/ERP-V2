using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Completeness;
using NickERP.Inspection.Core.Completeness;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 31 / B5.1 Phase D — engine-level coverage for
/// <see cref="CompletenessChecker"/>. Asserts the bulk-evaluation path
/// (multiple requirements per case), per-tenant disable, the rollup
/// rule (Incomplete &gt; Partial &gt; Pass), and finding persistence.
/// </summary>
public sealed class CompletenessCheckerTests : IDisposable
{
    private readonly InspectionDbContext _db;
    private readonly TenantContext _tenant;
    private readonly InMemoryCompletenessRequirementProvider _settings;
    private readonly RecordingEventPublisher _events;

    public CompletenessCheckerTests()
    {
        var options = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("completeness-checker-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new InspectionDbContext(options);
        _tenant = new TenantContext();
        _tenant.SetTenant(1);
        _settings = new InMemoryCompletenessRequirementProvider();
        _events = new RecordingEventPublisher();
    }

    public void Dispose() => _db.Dispose();

    private CompletenessChecker NewChecker(IEnumerable<ICompletenessRequirement>? requirements = null)
    {
        var reqs = requirements?.ToList() ?? new List<ICompletenessRequirement>
        {
            new RequiredScanArtifactRequirement(),
            new RequiredCustomsDeclarationRequirement(),
            new RequiredAnalystDecisionRequirement()
        };
        return new CompletenessChecker(
            _db, reqs, _settings, _events, _tenant, NullLogger<CompletenessChecker>.Instance);
    }

    private async Task<InspectionCase> SeedCaseAsync()
    {
        var locId = Guid.NewGuid();
        var c = new InspectionCase
        {
            Id = Guid.NewGuid(),
            LocationId = locId,
            SubjectIdentifier = "C1",
            State = InspectionWorkflowState.Validated,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        _db.Cases.Add(c);
        await _db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task Empty_case_skips_or_misses_per_requirement_definitions()
    {
        var c = await SeedCaseAsync();
        var checker = NewChecker();
        var result = await checker.EvaluateAsync(c.Id);

        // Three built-ins on an empty case:
        //   - RequiredScanArtifact → Skip (no scans)
        //   - RequiredCustomsDeclaration → Incomplete (no docs)
        //   - RequiredAnalystDecision → Skip (pre-intake)
        result.Outcomes.Should().HaveCount(3);
        result.HasIncomplete.Should().BeTrue();
        result.RollupSeverity.Should().Be(CompletenessSeverity.Incomplete);
    }

    [Fact]
    public async Task Disabled_requirement_is_skipped_silently()
    {
        var c = await SeedCaseAsync();
        _settings.Disable(1, BuiltInCompletenessRequirementIds.RequiredCustomsDeclaration);
        var checker = NewChecker();
        var result = await checker.EvaluateAsync(c.Id);

        // Disabled requirements DO NOT appear in the outcome list at
        // all — they're filtered before the eval call. Two outcomes
        // remain (the two enabled built-ins).
        result.Outcomes.Should().HaveCount(2);
        result.Outcomes.Select(o => o.RequirementId)
            .Should().NotContain(BuiltInCompletenessRequirementIds.RequiredCustomsDeclaration);
    }

    [Fact]
    public async Task Duplicate_requirement_id_throws()
    {
        var dup = new ICompletenessRequirement[]
        {
            new RequiredScanArtifactRequirement(),
            new RequiredScanArtifactRequirement()
        };
        var act = () => new CompletenessChecker(
            _db, dup, _settings, _events, _tenant, NullLogger<CompletenessChecker>.Instance);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate*");
    }

    [Fact]
    public async Task Throwing_requirement_is_quarantined()
    {
        var c = await SeedCaseAsync();
        var checker = NewChecker(new ICompletenessRequirement[] { new ThrowingRequirement() });
        var result = await checker.EvaluateAsync(c.Id);
        var outcome = result.Outcomes.Single();
        outcome.Severity.Should().Be(CompletenessSeverity.Skip);
        outcome.Message.Should().Contain("threw");
    }

    [Fact]
    public async Task Incomplete_outcome_persists_a_finding_with_completeness_prefix()
    {
        var c = await SeedCaseAsync();
        var checker = NewChecker(new ICompletenessRequirement[] { new RequiredCustomsDeclarationRequirement() });
        await checker.EvaluateAsync(c.Id);

        var finding = await _db.Findings.AsNoTracking().FirstOrDefaultAsync();
        finding.Should().NotBeNull();
        finding!.FindingType.Should().StartWith("completeness.");
        finding.Severity.Should().Be("incomplete");
    }

    [Fact]
    public async Task Pass_outcome_does_not_persist_a_finding()
    {
        var c = await SeedCaseAsync();
        // Add a doc to make RequiredCustomsDeclaration pass.
        _db.AuthorityDocuments.Add(new AuthorityDocument
        {
            Id = Guid.NewGuid(), CaseId = c.Id, ExternalSystemInstanceId = Guid.NewGuid(),
            DocumentType = "BOE", ReferenceNumber = "ref", PayloadJson = "{}",
            ReceivedAt = DateTimeOffset.UtcNow, TenantId = 1
        });
        await _db.SaveChangesAsync();

        var checker = NewChecker(new ICompletenessRequirement[] { new RequiredCustomsDeclarationRequirement() });
        await checker.EvaluateAsync(c.Id);
        (await _db.Findings.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Audit_event_emits_per_outcome_with_severity_specific_type()
    {
        var c = await SeedCaseAsync();
        var checker = NewChecker(new ICompletenessRequirement[] { new RequiredCustomsDeclarationRequirement() });
        await checker.EvaluateAsync(c.Id);

        _events.Events.Should().HaveCount(1);
        _events.Events[0].EventType.Should().Be("inspection.completeness.incomplete");
    }

    [Fact]
    public void RollupSeverity_picks_worst_case()
    {
        // Pass + Pass + Pass → Pass
        var allPass = new CompletenessEvaluationResult(Guid.NewGuid(), new[]
        {
            CompletenessOutcome.Pass("x"),
            CompletenessOutcome.Pass("y")
        });
        allPass.RollupSeverity.Should().Be(CompletenessSeverity.Pass);

        // Pass + Partial → Partial
        var withPartial = new CompletenessEvaluationResult(Guid.NewGuid(), new[]
        {
            CompletenessOutcome.Pass("x"),
            CompletenessOutcome.Partial("y", "missing")
        });
        withPartial.RollupSeverity.Should().Be(CompletenessSeverity.PartiallyComplete);

        // Partial + Incomplete → Incomplete
        var withIncomplete = new CompletenessEvaluationResult(Guid.NewGuid(), new[]
        {
            CompletenessOutcome.Partial("x", "p"),
            CompletenessOutcome.Incomplete("y", "miss")
        });
        withIncomplete.RollupSeverity.Should().Be(CompletenessSeverity.Incomplete);

        // Skip alone → Pass (rollup ignores Skips)
        var allSkip = new CompletenessEvaluationResult(Guid.NewGuid(), new[]
        {
            CompletenessOutcome.Skip("x", "no data")
        });
        allSkip.RollupSeverity.Should().Be(CompletenessSeverity.Pass);
    }

    private sealed class ThrowingRequirement : ICompletenessRequirement
    {
        public string RequirementId => "test.throwing";
        public string Description => "always throws";
        public CompletenessOutcome Evaluate(CompletenessContext context)
            => throw new InvalidOperationException("boom");
    }
}

/// <summary>
/// Test-only IEventPublisher that records what the engine emitted so
/// tests can assert event types + payload shape without a real
/// Postgres-backed audit DB.
/// </summary>
internal sealed class RecordingEventPublisher : IEventPublisher
{
    public List<DomainEvent> Events { get; } = new();
    public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default)
    {
        Events.Add(evt);
        return Task.FromResult(evt);
    }
    public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
    {
        Events.AddRange(events);
        return Task.FromResult<IReadOnlyList<DomainEvent>>(events);
    }
}
