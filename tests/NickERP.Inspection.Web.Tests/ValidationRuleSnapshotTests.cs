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
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 48 / Phase B — coverage for FU-validation-rule-evaluation-snapshot.
///
/// <list type="bullet">
///   <item>Engine evaluation writes one
///         <see cref="ValidationRuleSnapshot"/> per outcome (incl. Skip).</item>
///   <item>Snapshots survive case reload (the per-(case, rule)
///         hydrate path is non-destructive).</item>
///   <item><see cref="IValidationSnapshotReader.ListByCaseAsync"/>
///         returns rows ordered by <see cref="ValidationRuleSnapshot.EvaluatedAt"/>
///         descending so the latest run is first.</item>
///   <item>Re-running the engine appends rather than upserts (snapshot
///         is append-only).</item>
/// </list>
/// </summary>
public sealed class ValidationRuleSnapshotTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly Guid _caseId = Guid.NewGuid();
    private readonly Guid _locationId = Guid.NewGuid();
    private readonly long _tenantId = 1;

    public ValidationRuleSnapshotTests()
    {
        var services = new ServiceCollection();
        var dbName = "s48-snap-" + Guid.NewGuid();
        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(_tenantId);
            return t;
        });
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddSingleton<IEventPublisher, NoopEventPublisher>();
        services.AddSingleton<InMemoryRuleEnablementProvider>();
        services.AddSingleton<IRuleEnablementProvider>(sp => sp.GetRequiredService<InMemoryRuleEnablementProvider>());
        services.AddScoped<IValidationRule, ErrorRule>();
        services.AddScoped<IValidationRule, WarnRule>();
        services.AddScoped<IValidationRule, SkipRule>();
        services.AddScoped<ValidationEngine>();
        services.AddScoped<IValidationSnapshotReader, DbValidationSnapshotReader>();

        _sp = services.BuildServiceProvider();
        SeedAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    public async Task EvaluateAsync_writes_a_snapshot_per_outcome_including_skip()
    {
        using var scope = _sp.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ValidationEngine>();
        await engine.EvaluateAsync(_caseId);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var rows = await db.ValidationRuleSnapshots.AsNoTracking()
            .Where(s => s.CaseId == _caseId)
            .ToListAsync();

        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.RuleId == "test.error" && r.Outcome == "error");
        Assert.Contains(rows, r => r.RuleId == "test.warn" && r.Outcome == "warning");
        Assert.Contains(rows, r => r.RuleId == "test.skip" && r.Outcome == "skip");
        Assert.All(rows, r => Assert.Equal(_tenantId, r.TenantId));
        Assert.All(rows, r => Assert.Equal(_caseId, r.CaseId));
    }

    [Fact]
    public async Task EvaluateAsync_persists_properties_and_message()
    {
        using var scope = _sp.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ValidationEngine>();
        await engine.EvaluateAsync(_caseId);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var errorRow = await db.ValidationRuleSnapshots.AsNoTracking()
            .FirstAsync(s => s.RuleId == "test.error");

        Assert.Equal("intentional error", errorRow.Message);
        Assert.Contains("\"k\":\"v\"", errorRow.PropertiesJson);
    }

    [Fact]
    public async Task SnapshotReader_ListByCaseAsync_orders_by_EvaluatedAt_descending()
    {
        // First evaluation
        using (var scope = _sp.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<ValidationEngine>();
            await engine.EvaluateAsync(_caseId);
        }
        // Tiny delay so EvaluatedAt actually advances
        await Task.Delay(20);
        // Second evaluation appends a fresh set of rows
        using (var scope = _sp.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<ValidationEngine>();
            await engine.EvaluateAsync(_caseId);
        }

        using var verifyScope = _sp.CreateScope();
        var reader = verifyScope.ServiceProvider.GetRequiredService<IValidationSnapshotReader>();
        var rows = await reader.ListByCaseAsync(_caseId);

        // 3 rules x 2 evaluations = 6 rows.
        Assert.Equal(6, rows.Count);
        // Newest first.
        for (int i = 1; i < rows.Count; i++)
        {
            Assert.True(rows[i - 1].EvaluatedAt >= rows[i].EvaluatedAt,
                $"Row {i - 1}.EvaluatedAt ({rows[i - 1].EvaluatedAt:O}) should be >= Row {i}.EvaluatedAt ({rows[i].EvaluatedAt:O})");
        }
        // Dedupe-by-rule-id idiom: first occurrence per RuleId is the
        // latest snapshot — that's what /cases/{id} would render.
        var latestPerRule = rows
            .GroupBy(r => r.RuleId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EvaluatedAt).First());
        Assert.Equal(3, latestPerRule.Count);
    }

    [Fact]
    public async Task Snapshots_survive_case_reload()
    {
        // Write snapshots
        using (var scope = _sp.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<ValidationEngine>();
            await engine.EvaluateAsync(_caseId);
        }

        // "Reload" — fresh DI scope, new DbContext, no engine call.
        using var freshScope = _sp.CreateScope();
        var reader = freshScope.ServiceProvider.GetRequiredService<IValidationSnapshotReader>();
        var rows = await reader.ListByCaseAsync(_caseId);

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task SnapshotReader_ListByRuleAsync_filters_by_rule()
    {
        using (var scope = _sp.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<ValidationEngine>();
            await engine.EvaluateAsync(_caseId);
        }

        using var verifyScope = _sp.CreateScope();
        var reader = verifyScope.ServiceProvider.GetRequiredService<IValidationSnapshotReader>();
        var rows = await reader.ListByRuleAsync(_tenantId, "test.error");

        Assert.Single(rows);
        Assert.Equal("test.error", rows[0].RuleId);
        Assert.Equal("error", rows[0].Outcome);
    }

    private async Task SeedAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        db.Locations.Add(new Location
        {
            Id = _locationId,
            Code = "tema",
            Name = "Tema",
            TenantId = _tenantId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Cases.Add(new InspectionCase
        {
            Id = _caseId,
            LocationId = _locationId,
            SubjectIdentifier = "MSCU0001",
            State = InspectionWorkflowState.Validated,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = _tenantId
        });
        await db.SaveChangesAsync();
    }

    // -------- test rules --------

    private sealed class ErrorRule : IValidationRule
    {
        public string RuleId => "test.error";
        public string Description => "error";
        public ValidationOutcome Evaluate(ValidationContext context) =>
            ValidationOutcome.Error(RuleId, "intentional error",
                new Dictionary<string, string> { ["k"] = "v" });
    }

    private sealed class WarnRule : IValidationRule
    {
        public string RuleId => "test.warn";
        public string Description => "warn";
        public ValidationOutcome Evaluate(ValidationContext context) =>
            ValidationOutcome.Warn(RuleId, "intentional warn");
    }

    private sealed class SkipRule : IValidationRule
    {
        public string RuleId => "test.skip";
        public string Description => "skip";
        public ValidationOutcome Evaluate(ValidationContext context) =>
            ValidationOutcome.Skip(RuleId, "abstaining");
    }

    private sealed class NoopEventPublisher : IEventPublisher
    {
        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default) =>
            Task.FromResult(evt);
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default) =>
            Task.FromResult(events);
    }
}
