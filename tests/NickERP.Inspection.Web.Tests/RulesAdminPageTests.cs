using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Validation;
using NickERP.Inspection.Core.Validation;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Components.Pages.Rules;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 28 / B4 Phase D — bunit render coverage for the
/// <c>RulesAdmin</c> Razor page. The regression we guard is the same as
/// for the rest of the admin pages: SSR-time rendering must not throw,
/// and the page must list at least one rule when the engine has rules
/// registered.
/// </summary>
public sealed class RulesAdminPageTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();

    public RulesAdminPageTests()
    {
        var inspName = "rulesadm-insp-" + Guid.NewGuid();
        var tenancyName = "rulesadm-tenancy-" + Guid.NewGuid();
        var auditName = "rulesadm-audit-" + Guid.NewGuid();
        _ctx.Services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(inspName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        _ctx.Services.AddDbContext<TenancyDbContext>(o =>
            o.UseInMemoryDatabase(tenancyName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        _ctx.Services.AddDbContext<AuditDbContext>(o =>
            o.UseInMemoryDatabase(auditName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        _ctx.Services.AddScoped<AuditDbContext>(sp =>
            new TestAuditDbContext(sp.GetRequiredService<DbContextOptions<AuditDbContext>>()));

        _ctx.Services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });
        _ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());

        _ctx.Services.AddSingleton<InMemoryRuleEnablementProvider>();
        _ctx.Services.AddSingleton<IRuleEnablementProvider>(sp => sp.GetRequiredService<InMemoryRuleEnablementProvider>());
        _ctx.Services.AddSingleton<IEventPublisher, NoopEventPublisher>();
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        _ctx.Services.AddScoped<IValidationRule, FakePageRuleA>();
        _ctx.Services.AddScoped<IValidationRule, FakePageRuleB>();
        _ctx.Services.AddScoped<ValidationEngine>();
        _ctx.Services.AddScoped<RulesAdminService>();
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void RulesAdmin_renders_one_row_per_registered_rule()
    {
        var cut = _ctx.RenderComponent<RulesAdmin>();
        cut.Markup.Should().Contain("Validation rules");
        cut.Markup.Should().Contain("test.fake_a");
        cut.Markup.Should().Contain("test.fake_b");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RuleDetail_renders_with_rule_id_and_summary()
    {
        var cut = _ctx.RenderComponent<RuleDetail>(p => p.Add(d => d.RuleId, "test.fake_a"));
        cut.Markup.Should().Contain("test.fake_a");
    }

    private sealed class FakePageRuleA : IValidationRule
    {
        public string RuleId => "test.fake_a";
        public string Description => "page A";
        public ValidationOutcome Evaluate(ValidationContext context) => ValidationOutcome.Pass(RuleId);
    }
    private sealed class FakePageRuleB : IValidationRule
    {
        public string RuleId => "test.fake_b";
        public string Description => "page B";
        public ValidationOutcome Evaluate(ValidationContext context) => ValidationOutcome.Pass(RuleId);
    }

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "test"),
                new Claim("nickerp:id", Guid.NewGuid().ToString()),
                new Claim("nickerp:tenant_id", "1"),
                new Claim(ClaimTypes.Role, "Inspection.Admin"),
            }, "Test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }

    private sealed class NoopEventPublisher : IEventPublisher
    {
        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default) => Task.FromResult(evt);
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
            => Task.FromResult(events);
    }

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
