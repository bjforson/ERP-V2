using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Validation;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Core.Validation;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 28 / B4 Phase D — coverage for
/// <see cref="ValidationEngine"/>. Asserts deterministic eval order,
/// per-tenant rule disable, Finding persistence, audit emission, and the
/// rule-rebadge / throw-handling defenses.
///
/// <para>
/// Docker is unavailable so we use the EF in-memory provider. The
/// per-tenant disable + Finding persistence paths don't depend on
/// Postgres-side RLS for correctness; the DB-policy half of the tenancy
/// story is exercised by the platform-level RLS migration tests.
/// </para>
/// </summary>
public sealed class ValidationEngineTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly Guid _caseId = Guid.NewGuid();
    private readonly List<DomainEvent> _capturedEvents = new();

    public ValidationEngineTests()
    {
        var services = new ServiceCollection();
        var dbName = "eng-" + Guid.NewGuid();
        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });

        services.AddSingleton<IEventPublisher>(new CapturingEventPublisher(_capturedEvents));
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        // Engine + in-memory enablement provider so we don't need TenancyDbContext.
        services.AddSingleton<InMemoryRuleEnablementProvider>();
        services.AddSingleton<IRuleEnablementProvider>(sp => sp.GetRequiredService<InMemoryRuleEnablementProvider>());

        // Three deterministic test rules — alphabetical by RuleId so the
        // engine's ordering invariant is observable.
        services.AddScoped<IValidationRule, AlphaRule>();
        services.AddScoped<IValidationRule, BetaRule>();
        services.AddScoped<IValidationRule, GammaRule>();

        services.AddScoped<ValidationEngine>();
        _sp = services.BuildServiceProvider();

        SeedAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    public async Task EvaluateAsync_orders_outcomes_deterministically_by_RuleId()
    {
        using var scope = _sp.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ValidationEngine>();
        var result = await engine.EvaluateAsync(_caseId);

        result.Outcomes.Select(o => o.RuleId).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
        result.Outcomes.Should().HaveCount(3);
    }

    [Fact]
    public async Task EvaluateAsync_skips_disabled_rule()
    {
        var enablement = _sp.GetRequiredService<InMemoryRuleEnablementProvider>();
        enablement.Disable(tenantId: 1, ruleId: "test.beta");

        using var scope = _sp.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ValidationEngine>();
        var result = await engine.EvaluateAsync(_caseId);

        result.Outcomes.Should().HaveCount(2);
        result.Outcomes.Should().NotContain(o => string.Equals(o.RuleId, "test.beta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EvaluateAsync_persists_findings_for_warning_and_error_only()
    {
        // AlphaRule emits Error, BetaRule emits Warning, GammaRule emits Skip.
        // Engine should write 2 findings (Error + Warning) and 0 for Skip.
        using var scope = _sp.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ValidationEngine>();
        await engine.EvaluateAsync(_caseId);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var findings = await db.Findings.AsNoTracking().ToListAsync();
        findings.Should().HaveCount(2,
            because: "Error + Warning rules persist Findings; Skip outcomes only emit audit events");
        findings.Select(f => f.FindingType).Should().BeEquivalentTo(new[]
        {
            "validation.test.alpha", "validation.test.beta"
        });
        findings.Select(f => f.Severity).Should().BeEquivalentTo(new[] { "error", "warning" });
    }

    [Fact]
    public async Task EvaluateAsync_emits_audit_event_per_outcome_including_skip()
    {
        _capturedEvents.Clear();
        using var scope = _sp.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ValidationEngine>();
        await engine.EvaluateAsync(_caseId);

        _capturedEvents.Should().HaveCount(3,
            because: "every outcome — including Skip — emits an audit event so the trail is complete");
        _capturedEvents.Select(e => e.EventType).Should().BeEquivalentTo(new[]
        {
            "inspection.validation.failed",   // alpha = Error
            "inspection.validation.failed",   // beta = Warning
            "inspection.validation.skipped"   // gamma = Skip
        });
    }

    [Fact]
    public async Task EvaluateAsync_HasErrors_true_when_any_rule_errors()
    {
        using var scope = _sp.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ValidationEngine>();
        var result = await engine.EvaluateAsync(_caseId);
        result.HasErrors.Should().BeTrue();
        result.HasWarnings.Should().BeTrue();
    }

    [Fact]
    public void Constructor_throws_on_duplicate_rule_id()
    {
        var enablement = new InMemoryRuleEnablementProvider();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(1);

        var rules = new IValidationRule[] { new AlphaRule(), new AlphaRule() };
        Action act = () => new ValidationEngine(
            db: null!,
            rules: rules,
            enablement: enablement,
            events: new CapturingEventPublisher(new List<DomainEvent>()),
            tenant: tenantContext,
            logger: NullLogger<ValidationEngine>.Instance);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate*");
    }

    [Fact]
    public async Task EvaluateAsync_treats_throwing_rule_as_skip()
    {
        var enablement = new InMemoryRuleEnablementProvider();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(1);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var engine = new ValidationEngine(
            db: db,
            rules: new IValidationRule[] { new ThrowingRule() },
            enablement: enablement,
            events: new CapturingEventPublisher(new List<DomainEvent>()),
            tenant: tenantContext,
            logger: NullLogger<ValidationEngine>.Instance);

        var result = await engine.EvaluateAsync(_caseId);
        result.Outcomes.Should().HaveCount(1);
        result.Outcomes[0].Severity.Should().Be(ValidationSeverity.Skip);
        result.Outcomes[0].RuleId.Should().Be("test.throwing");
    }

    [Fact]
    public async Task EvaluateAsync_rebadges_outcome_id_to_match_rule_id()
    {
        var enablement = new InMemoryRuleEnablementProvider();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(1);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var engine = new ValidationEngine(
            db: db,
            rules: new IValidationRule[] { new MisbehavingIdRule() },
            enablement: enablement,
            events: new CapturingEventPublisher(new List<DomainEvent>()),
            tenant: tenantContext,
            logger: NullLogger<ValidationEngine>.Instance);

        var result = await engine.EvaluateAsync(_caseId);
        result.Outcomes[0].RuleId.Should().Be("test.correct_id",
            because: "engine rebadges the outcome to match the rule's declared id; analytics depend on this invariant");
    }

    private async Task SeedAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var locId = Guid.NewGuid();
        db.Locations.Add(new Location
        {
            Id = locId,
            Code = "tema",
            Name = "Tema Port",
            TenantId = 1,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Cases.Add(new InspectionCase
        {
            Id = _caseId,
            LocationId = locId,
            SubjectIdentifier = "MSCU1234567",
            State = InspectionWorkflowState.Validated,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = 1
        });
        await db.SaveChangesAsync();
    }

    // -------- test rules --------

    private sealed class AlphaRule : IValidationRule
    {
        public string RuleId => "test.alpha";
        public string Description => "alpha";
        public ValidationOutcome Evaluate(ValidationContext context)
            => ValidationOutcome.Error(RuleId, "alpha-error");
    }

    private sealed class BetaRule : IValidationRule
    {
        public string RuleId => "test.beta";
        public string Description => "beta";
        public ValidationOutcome Evaluate(ValidationContext context)
            => ValidationOutcome.Warn(RuleId, "beta-warn");
    }

    private sealed class GammaRule : IValidationRule
    {
        public string RuleId => "test.gamma";
        public string Description => "gamma";
        public ValidationOutcome Evaluate(ValidationContext context)
            => ValidationOutcome.Skip(RuleId, "gamma-skip");
    }

    private sealed class ThrowingRule : IValidationRule
    {
        public string RuleId => "test.throwing";
        public string Description => "throws";
        public ValidationOutcome Evaluate(ValidationContext context)
            => throw new InvalidOperationException("intentionally broken");
    }

    private sealed class MisbehavingIdRule : IValidationRule
    {
        public string RuleId => "test.correct_id";
        public string Description => "tags an outcome with the wrong id";
        public ValidationOutcome Evaluate(ValidationContext context)
            => ValidationOutcome.Warn("test.WRONG_ID", "ought to be rebadged");
    }

    private sealed class CapturingEventPublisher : IEventPublisher
    {
        private readonly List<DomainEvent> _sink;
        public CapturingEventPublisher(List<DomainEvent> sink) => _sink = sink;

        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default)
        {
            _sink.Add(evt);
            return Task.FromResult(evt);
        }
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(
            IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
        {
            _sink.AddRange(events);
            return Task.FromResult(events);
        }
    }
}
