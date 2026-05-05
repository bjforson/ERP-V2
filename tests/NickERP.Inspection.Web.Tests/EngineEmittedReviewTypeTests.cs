using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Completeness;
using NickERP.Inspection.Application.Validation;
using NickERP.Inspection.Core.Completeness;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Core.Validation;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 46 / Phase C — coverage for Sprint 42 FU-engine-emitted-
/// reviewtype-tagging. Both engine paths
/// (<see cref="ValidationEngine"/> + <see cref="CompletenessChecker"/>)
/// must stamp the right <see cref="ReviewType"/> on the synthetic
/// <see cref="AnalystReview"/> rows they create so the
/// <c>/admin/reviews/throughput</c> dashboard can split engine output
/// from human-emitted reviews. Sprint 34 left this defaulting to
/// <see cref="ReviewType.Standard"/>; the followup wires the explicit
/// type through.
/// </summary>
public sealed class EngineEmittedReviewTypeTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly Guid _caseId = Guid.NewGuid();
    private readonly Guid _locationId = Guid.NewGuid();
    private const long TenantId = 1L;

    public EngineEmittedReviewTypeTests()
    {
        var services = new ServiceCollection();
        var dbName = "engine-reviewtype-" + Guid.NewGuid();
        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(TenantId);
            return t;
        });
        services.AddSingleton<IEventPublisher>(new RecordingEventPublisher());
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));

        // Validation pipeline.
        services.AddSingleton<InMemoryRuleEnablementProvider>();
        services.AddSingleton<IRuleEnablementProvider>(sp =>
            sp.GetRequiredService<InMemoryRuleEnablementProvider>());
        services.AddScoped<IValidationRule, AlwaysWarnRule>();
        services.AddScoped<ValidationEngine>();

        // Completeness pipeline.
        services.AddSingleton<InMemoryCompletenessRequirementProvider>();
        services.AddSingleton<ICompletenessRequirementProvider>(sp =>
            sp.GetRequiredService<InMemoryCompletenessRequirementProvider>());
        services.AddScoped<ICompletenessRequirement, AlwaysIncompleteRequirement>();
        services.AddScoped<CompletenessChecker>();

        _sp = services.BuildServiceProvider();

        SeedAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _sp.Dispose();

    private async Task SeedAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        db.Locations.Add(new Location
        {
            Id = _locationId, Code = "ER", Name = "engine-reviewtype",
            TimeZone = "UTC", IsActive = true, TenantId = TenantId,
        });
        db.Cases.Add(new InspectionCase
        {
            Id = _caseId,
            LocationId = _locationId,
            SubjectIdentifier = "ENG-RT-1",
            State = InspectionWorkflowState.Validated,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = TenantId
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ValidationEngine_emits_AnalystReview_with_EngineValidation_type()
    {
        using var scope = _sp.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ValidationEngine>();

        await engine.EvaluateAsync(_caseId);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var reviews = await db.AnalystReviews.AsNoTracking().ToListAsync();
        reviews.Should().HaveCount(1,
            because: "the warn-rule produces 1 finding which hangs off 1 synthetic review");
        reviews[0].ReviewType.Should().Be(ReviewType.EngineValidation);
    }

    [Fact]
    public async Task ValidationEngine_review_session_is_engine_validation_outcome()
    {
        using var scope = _sp.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ValidationEngine>();

        await engine.EvaluateAsync(_caseId);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var sessions = await db.ReviewSessions.AsNoTracking().ToListAsync();
        sessions.Should().ContainSingle();
        sessions[0].Outcome.Should().Be("engine-validation");
        // AnalystUserId is empty because the engine isn't a real user.
        sessions[0].AnalystUserId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task CompletenessChecker_emits_AnalystReview_with_EngineCompleteness_type()
    {
        using var scope = _sp.CreateScope();
        var checker = scope.ServiceProvider.GetRequiredService<CompletenessChecker>();

        await checker.EvaluateAsync(_caseId);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var reviews = await db.AnalystReviews.AsNoTracking().ToListAsync();
        reviews.Should().HaveCount(1,
            because: "the always-incomplete requirement produces 1 finding hanging off 1 synthetic review");
        reviews[0].ReviewType.Should().Be(ReviewType.EngineCompleteness);
    }

    [Fact]
    public async Task CompletenessChecker_review_session_is_completeness_engine_outcome()
    {
        using var scope = _sp.CreateScope();
        var checker = scope.ServiceProvider.GetRequiredService<CompletenessChecker>();

        await checker.EvaluateAsync(_caseId);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var sessions = await db.ReviewSessions.AsNoTracking().ToListAsync();
        sessions.Should().ContainSingle();
        sessions[0].Outcome.Should().Be("completeness-engine");
        sessions[0].AnalystUserId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task EngineValidation_review_carries_zero_TimeToDecisionMs()
    {
        using var scope = _sp.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ValidationEngine>();
        await engine.EvaluateAsync(_caseId);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var review = await db.AnalystReviews.AsNoTracking().FirstAsync();
        // Engine reviews have no time-to-decision; the throughput
        // dashboard's avg-TTD card depends on this so the engine bucket
        // doesn't drag the human average down.
        review.TimeToDecisionMs.Should().Be(0);
        review.ConfidenceScore.Should().Be(1.0);
    }

    [Fact]
    public async Task EngineCompleteness_review_carries_zero_TimeToDecisionMs()
    {
        using var scope = _sp.CreateScope();
        var checker = scope.ServiceProvider.GetRequiredService<CompletenessChecker>();
        await checker.EvaluateAsync(_caseId);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var review = await db.AnalystReviews.AsNoTracking().FirstAsync();
        review.TimeToDecisionMs.Should().Be(0);
        review.ConfidenceScore.Should().Be(1.0);
    }

    [Fact]
    public async Task ValidationEngine_finding_attaches_to_engine_validation_review()
    {
        using var scope = _sp.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ValidationEngine>();
        await engine.EvaluateAsync(_caseId);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var review = await db.AnalystReviews.AsNoTracking().FirstAsync();
        var finding = await db.Findings.AsNoTracking().FirstAsync();
        finding.AnalystReviewId.Should().Be(review.Id,
            because: "the synthetic review and the finding it explains must be linked");
        finding.FindingType.Should().StartWith("validation.");
    }

    [Fact]
    public async Task CompletenessChecker_finding_attaches_to_engine_completeness_review()
    {
        using var scope = _sp.CreateScope();
        var checker = scope.ServiceProvider.GetRequiredService<CompletenessChecker>();
        await checker.EvaluateAsync(_caseId);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var review = await db.AnalystReviews.AsNoTracking().FirstAsync();
        var finding = await db.Findings.AsNoTracking().FirstAsync();
        finding.AnalystReviewId.Should().Be(review.Id);
        finding.FindingType.Should().StartWith("completeness.");
    }

    // -------- test helpers --------

    private sealed class AlwaysWarnRule : IValidationRule
    {
        public string RuleId => "test.always_warn";
        public string Description => "always-warn rule for the engine-reviewtype tests";
        public ValidationOutcome Evaluate(ValidationContext context)
            => ValidationOutcome.Warn(RuleId, "this rule always warns");
    }

    private sealed class AlwaysIncompleteRequirement : ICompletenessRequirement
    {
        public string RequirementId => "test.always_incomplete";
        public string Description => "always-incomplete requirement";
        public CompletenessOutcome Evaluate(CompletenessContext context)
            => CompletenessOutcome.Incomplete(RequirementId, "always missing");
    }
}
