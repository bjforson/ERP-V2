using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.PostHocOutcomes;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 13 / §6.11.9 — round-trip tests for the manual-entry path.
/// Operator submits a payload via <see cref="PostHocOutcomeManualEntryService"/>;
/// the row lands in <c>authority_documents</c> with
/// <c>payload.entry_method = "manual"</c> and the per-tenant pseudo-
/// instance is auto-seeded.
/// </summary>
public sealed class PostHocOutcomeManualEntryServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly Guid _caseId = Guid.NewGuid();
    private readonly Guid _reviewSessionId = Guid.NewGuid();
    private readonly long _tenantId = 1;

    public PostHocOutcomeManualEntryServiceTests()
    {
        var dbName = "manual-entry-" + Guid.NewGuid();
        var services = new ServiceCollection();

        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });
        services.AddSingleton<AuthenticationStateProvider>(new FakeAuthState());
        services.AddSingleton<IEventPublisher, NoopPublisher>();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddScoped<IPostHocOutcomeWriter, PostHocOutcomeWriter>();
        services.AddScoped<PostHocOutcomeManualEntryService>();

        _sp = services.BuildServiceProvider();

        SeedAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RecordAsync_ValidForm_PersistsAuthorityDocumentAndSeedsPseudoInstance()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PostHocOutcomeManualEntryService>();

        var form = new ManualEntryForm
        {
            DeclarationNumber = "DECL-MANUAL-1",
            ContainerNumber = "MSCU0000001",
            Outcome = "Seized",
            SeizedCount = 3,
            DecidedAt = new DateTimeOffset(2026, 4, 28, 12, 0, 0, TimeSpan.Zero),
            DecidedByOfficerId = "GRA-CO-0001",
            DecisionReference = "GRA-MANUAL-0001",
            OperatorNotes = "phoned by GRA officer"
        };

        var outcome = await svc.RecordAsync(_caseId, form);

        Assert.Equal(OutcomeWriteOutcome.Inserted, outcome);

        using var verifyScope = _sp.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        // Document persisted
        var doc = await db.AuthorityDocuments.AsNoTracking().FirstAsync();
        Assert.Equal("PostHocOutcome", doc.DocumentType);
        Assert.Equal("GRA-MANUAL-0001", doc.ReferenceNumber);
        Assert.Equal(_caseId, doc.CaseId);

        // Payload stamped with manual entry_method
        var payload = JsonNode.Parse(doc.PayloadJson) as JsonObject;
        Assert.Equal("manual", payload!["entry_method"]?.GetValue<string>());

        // Pseudo-instance auto-seeded with type_code "manual-entry"
        var pseudo = await db.ExternalSystemInstances.AsNoTracking()
            .FirstAsync(e => e.TypeCode == PostHocOutcomeManualEntryService.ManualEntryTypeCode);
        Assert.True(pseudo.IsActive);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RecordAsync_DoubleSubmit_DedupsToSecondCallReturningDeduplicated()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PostHocOutcomeManualEntryService>();

        var form = new ManualEntryForm
        {
            // Match the seeded case's SubjectIdentifier so the writer's
            // case-lookup path 2 (SubjectIdentifier match) succeeds.
            DeclarationNumber = "DECL-MANUAL-1",
            Outcome = "Cleared",
            DecidedAt = new DateTimeOffset(2026, 4, 28, 14, 0, 0, TimeSpan.Zero),
            DecisionReference = "GRA-MANUAL-0002"
        };

        var first = await svc.RecordAsync(_caseId, form);
        var second = await svc.RecordAsync(_caseId, form);

        Assert.Equal(OutcomeWriteOutcome.Inserted, first);
        Assert.Equal(OutcomeWriteOutcome.Deduplicated, second);

        using var verifyScope = _sp.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var docs = await db.AuthorityDocuments.AsNoTracking().ToListAsync();
        Assert.Single(docs);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordAsync_UnresolvedTenant_ThrowsInvalidOperation()
    {
        // Spin up a parallel SP where tenant context is intentionally unresolved.
        var dbName = "manual-entry-noten-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddSingleton<AuthenticationStateProvider>(new FakeAuthState());
        services.AddSingleton<IEventPublisher, NoopPublisher>();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IPostHocOutcomeWriter, PostHocOutcomeWriter>();
        services.AddScoped<PostHocOutcomeManualEntryService>();

        await using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PostHocOutcomeManualEntryService>();

        var form = new ManualEntryForm
        {
            DecisionReference = "GRA-X",
            Outcome = "Cleared"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RecordAsync(Guid.NewGuid(), form));
    }

    private async Task SeedAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var locationId = Guid.NewGuid();
        db.Locations.Add(new Location
        {
            Id = locationId, Code = "loc1", Name = "L1", TimeZone = "UTC", IsActive = true, TenantId = _tenantId
        });
        db.Cases.Add(new InspectionCase
        {
            Id = _caseId,
            LocationId = locationId,
            SubjectType = CaseSubjectType.Container,
            SubjectIdentifier = "DECL-MANUAL-1",
            SubjectPayloadJson = "{}",
            State = InspectionWorkflowState.Open,
            OpenedAt = DateTimeOffset.UtcNow.AddDays(-1),
            StateEnteredAt = DateTimeOffset.UtcNow.AddDays(-1),
            TenantId = _tenantId
        });
        db.ReviewSessions.Add(new ReviewSession
        {
            Id = _reviewSessionId,
            CaseId = _caseId,
            AnalystUserId = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Outcome = "in-progress",
            TenantId = _tenantId
        });
        db.AnalystReviews.Add(new AnalystReview
        {
            Id = Guid.NewGuid(),
            ReviewSessionId = _reviewSessionId,
            TimeToDecisionMs = 2000,
            ConfidenceScore = 0.7,
            RoiInteractionsJson = "[]",
            VerdictChangesJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = _tenantId
        });
        await db.SaveChangesAsync();
    }

    // ---------------- stubs ----------------

    private sealed class FakeAuthState : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "test-operator"),
                new System.Security.Claims.Claim("nickerp:id", Guid.NewGuid().ToString()),
                new System.Security.Claims.Claim("nickerp:tenant_id", "1"),
            }, "Test");
            return Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal(identity)));
        }
    }

    private sealed class NoopPublisher : IEventPublisher
    {
        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default) =>
            Task.FromResult(evt);
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default) =>
            Task.FromResult(events);
    }
}
