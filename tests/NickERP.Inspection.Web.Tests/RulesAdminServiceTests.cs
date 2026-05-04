using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Validation;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Core.Validation;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 28 / B4 Phase D — coverage for
/// <see cref="RulesAdminService"/>. Asserts the list view shape, the
/// upsert + audit-emit on toggle, the recent-failures filter, and the
/// "unknown rule id" guard that refuses to write a setting row for a
/// non-existent rule.
/// </summary>
public sealed class RulesAdminServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly List<DomainEvent> _events = new();
    private const long TenantId = 1L;

    public RulesAdminServiceTests()
    {
        var services = new ServiceCollection();
        var inspName = "admin-insp-" + Guid.NewGuid();
        var tenancyName = "admin-tenancy-" + Guid.NewGuid();
        var auditName = "admin-audit-" + Guid.NewGuid();
        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(inspName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddDbContext<TenancyDbContext>(o =>
            o.UseInMemoryDatabase(tenancyName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        // Test-only AuditDbContext subclass with a JsonDocument<->string
        // value converter so the EF in-memory provider can materialise
        // the Payload column. Production runs use Postgres jsonb directly.
        // Mirrors the AuditNotificationProjectorTests pattern.
        services.AddDbContext<AuditDbContext>(o =>
            o.UseInMemoryDatabase(auditName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<AuditDbContext>(sp =>
            new TestAuditDbContext(sp.GetRequiredService<DbContextOptions<AuditDbContext>>()));

        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(TenantId);
            return t;
        });

        services.AddSingleton<InMemoryRuleEnablementProvider>();
        services.AddSingleton<IRuleEnablementProvider>(sp => sp.GetRequiredService<InMemoryRuleEnablementProvider>());
        services.AddSingleton<IEventPublisher>(new CapturingEventPublisher(_events));
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddScoped<IValidationRule, FakeRuleA>();
        services.AddScoped<IValidationRule, FakeRuleB>();
        services.AddScoped<ValidationEngine>();
        services.AddScoped<RulesAdminService>();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    public async Task ListRulesAsync_returns_one_row_per_registered_rule_with_default_enabled()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RulesAdminService>();
        var rows = await svc.ListRulesAsync(TenantId);

        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(r => r.Enabled.Should().BeTrue());
        rows.Should().AllSatisfy(r => r.HasOverride.Should().BeFalse());
        rows.Select(r => r.RuleId).Should().BeEquivalentTo(new[] { "test.fake_a", "test.fake_b" });
    }

    [Fact]
    public async Task SetRuleEnabledAsync_creates_row_and_audit_event_on_first_disable()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RulesAdminService>();
        var actor = Guid.NewGuid();

        await svc.SetRuleEnabledAsync(TenantId, "test.fake_a", enabled: false, actor);

        var tenancyDb = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var rows = await tenancyDb.TenantValidationRuleSettings.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].RuleId.Should().Be("test.fake_a");
        rows[0].Enabled.Should().BeFalse();
        rows[0].UpdatedByUserId.Should().Be(actor);

        _events.Should().ContainSingle(e => e.EventType == "inspection.validation_rule.toggled");
    }

    [Fact]
    public async Task SetRuleEnabledAsync_upserts_existing_row_and_emits_event()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RulesAdminService>();
        var actor = Guid.NewGuid();

        await svc.SetRuleEnabledAsync(TenantId, "test.fake_a", enabled: false, actor);
        _events.Clear();
        await svc.SetRuleEnabledAsync(TenantId, "test.fake_a", enabled: true, actor);

        var tenancyDb = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var rows = await tenancyDb.TenantValidationRuleSettings.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle(r => r.Enabled,
            because: "the upsert path flips Enabled in place; one row is preserved");
        _events.Should().ContainSingle(e => e.EventType == "inspection.validation_rule.toggled");
    }

    [Fact]
    public async Task SetRuleEnabledAsync_throws_for_unknown_rule_id()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RulesAdminService>();

        Func<Task> act = () => svc.SetRuleEnabledAsync(TenantId, "test.never_existed", false, null);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*test.never_existed*");
    }

    [Fact]
    public async Task ListRulesAsync_reflects_disabled_state_after_toggle()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RulesAdminService>();
        await svc.SetRuleEnabledAsync(TenantId, "test.fake_b", enabled: false, null);

        var rows = await svc.ListRulesAsync(TenantId);
        rows.Single(r => r.RuleId == "test.fake_b").Enabled.Should().BeFalse();
        rows.Single(r => r.RuleId == "test.fake_b").HasOverride.Should().BeTrue();
        rows.Single(r => r.RuleId == "test.fake_a").Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task RecentFailuresAsync_filters_by_ruleId_in_payload()
    {
        using var scope = _sp.CreateScope();
        var auditDb = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        var caseIdA = Guid.NewGuid().ToString();
        var caseIdB = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        // Three failure events: 2 for fake_a, 1 for fake_b.
        auditDb.Events.Add(BuildFailureEvent(caseIdA, "test.fake_a", "Error", "msg-a-1", now));
        auditDb.Events.Add(BuildFailureEvent(caseIdB, "test.fake_a", "Error", "msg-a-2", now.AddSeconds(1)));
        auditDb.Events.Add(BuildFailureEvent(caseIdA, "test.fake_b", "Warning", "msg-b-1", now.AddSeconds(2)));
        await auditDb.SaveChangesAsync();

        var svc = scope.ServiceProvider.GetRequiredService<RulesAdminService>();
        var fakeAFailures = await svc.RecentFailuresAsync(TenantId, "test.fake_a");
        fakeAFailures.Should().HaveCount(2);
        fakeAFailures.Select(f => f.Message).Should().BeEquivalentTo(new[] { "msg-a-1", "msg-a-2" });

        var fakeBFailures = await svc.RecentFailuresAsync(TenantId, "test.fake_b");
        fakeBFailures.Should().ContainSingle();
        fakeBFailures[0].Severity.Should().Be("Warning");
    }

    [Fact]
    public async Task ListRulesAsync_returns_recent_failure_count()
    {
        using var scope = _sp.CreateScope();
        var auditDb = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var caseId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            auditDb.Events.Add(BuildFailureEvent(caseId, "test.fake_a", "Error", $"msg-{i}", now.AddSeconds(i)));
        }
        await auditDb.SaveChangesAsync();

        var svc = scope.ServiceProvider.GetRequiredService<RulesAdminService>();
        var rows = await svc.ListRulesAsync(TenantId);
        rows.Single(r => r.RuleId == "test.fake_a").RecentFailureCount.Should().Be(3);
        rows.Single(r => r.RuleId == "test.fake_b").RecentFailureCount.Should().Be(0);
    }

    private static DomainEventRow BuildFailureEvent(string caseId, string ruleId, string severity, string message, DateTimeOffset when)
    {
        var payload = JsonSerializer.SerializeToDocument(new
        {
            ruleId, severity, message
        });
        return new DomainEventRow
        {
            EventId = Guid.NewGuid(),
            TenantId = TenantId,
            EventType = "inspection.validation.failed",
            EntityType = "InspectionCase",
            EntityId = caseId,
            Payload = payload,
            OccurredAt = when,
            IngestedAt = when,
            IdempotencyKey = Guid.NewGuid().ToString()
        };
    }

    // -------- test rules + helpers --------

    private sealed class FakeRuleA : IValidationRule
    {
        public string RuleId => "test.fake_a";
        public string Description => "fake a";
        public ValidationOutcome Evaluate(ValidationContext context) => ValidationOutcome.Pass(RuleId);
    }
    private sealed class FakeRuleB : IValidationRule
    {
        public string RuleId => "test.fake_b";
        public string Description => "fake b";
        public ValidationOutcome Evaluate(ValidationContext context) => ValidationOutcome.Pass(RuleId);
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
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
        {
            _sink.AddRange(events);
            return Task.FromResult(events);
        }
    }

    /// <summary>
    /// Test-only AuditDbContext subclass that adds a JsonDocument<->string
    /// value converter so the EF in-memory provider can materialise
    /// DomainEventRow.Payload (Postgres jsonb in production).
    /// </summary>
    private sealed class TestAuditDbContext : AuditDbContext
    {
        public TestAuditDbContext(DbContextOptions<AuditDbContext> options) : base(options) { }

        protected override void OnAuditModelCreating(ModelBuilder modelBuilder)
        {
            base.OnAuditModelCreating(modelBuilder);
            var jsonConverter = new ValueConverter<JsonDocument, string>(
                v => v.RootElement.GetRawText(),
                v => JsonDocument.Parse(v, default));
            modelBuilder.Entity<DomainEventRow>()
                .Property(e => e.Payload)
                .HasConversion(jsonConverter);
        }
    }
}
