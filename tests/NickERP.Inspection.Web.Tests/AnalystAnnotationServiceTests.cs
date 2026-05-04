using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 20 / B1.2 unit coverage for <see cref="AnalystAnnotationService"/>.
///
/// Asserts the four pieces of behaviour the viewer page depends on:
///   1. <c>AddAnnotationAsync</c> creates a Finding row + a backing
///      AnalystReview + ReviewSession when none exist.
///   2. A second annotation under the same analyst session re-uses the
///      existing AnalystReview rather than spinning up a fresh one
///      (per-annotation review-row sprawl is the exact thing we don't
///      want — the verdict path's review row stays separate).
///   3. <c>ListForArtifactAsync</c> returns annotations only for the
///      artifact id encoded in the location-payload jsonb (false-
///      positive jsonb text matches must be filtered out).
///   4. <c>DeleteAnnotationAsync</c> succeeds for the owning analyst
///      and refuses for everyone else.
///
/// Docker is unavailable in this environment so the test uses the EF
/// in-memory provider — same precedent as <c>RuleEvaluationPersistenceTests</c>.
/// RLS isolation is asserted at runtime via the live Postgres DB.
/// </summary>
public sealed class AnalystAnnotationServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly Guid _caseId = Guid.NewGuid();
    private readonly Guid _scanId = Guid.NewGuid();
    private readonly Guid _artifactId = Guid.NewGuid();
    private readonly Guid _otherArtifactId = Guid.NewGuid();
    private readonly Guid _analystUserId = Guid.NewGuid();

    public AnalystAnnotationServiceTests()
    {
        var services = new ServiceCollection();
        var dbName = "annotation-service-" + Guid.NewGuid();
        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });
        services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider(_analystUserId));
        services.AddSingleton<IEventPublisher, NoopEventPublisher>();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddScoped<AnalystAnnotationService>();

        _sp = services.BuildServiceProvider();

        Seed();
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AddAnnotation_CreatesReviewSession_AnalystReview_AndFinding_OnFirstCall()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AnalystAnnotationService>();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var f = await svc.AddAnnotationAsync(
            _caseId, _artifactId, x: 10, y: 20, w: 30, h: 40,
            severity: "warning", note: "looks like organic clutter");

        f.FindingType.Should().Be(AnalystAnnotationService.AnnotationFindingType);
        f.Severity.Should().Be("warning");
        f.Note.Should().Be("looks like organic clutter");
        f.LocationInImageJson.Should().Contain(_artifactId.ToString());

        // The location payload must round-trip through the jsonb shape
        // the viewer overlay expects (x/y/w/h/artifactId).
        using (var doc = JsonDocument.Parse(f.LocationInImageJson))
        {
            var root = doc.RootElement;
            root.GetProperty("x").GetInt32().Should().Be(10);
            root.GetProperty("y").GetInt32().Should().Be(20);
            root.GetProperty("w").GetInt32().Should().Be(30);
            root.GetProperty("h").GetInt32().Should().Be(40);
            root.GetProperty("artifactId").GetString().Should().Be(_artifactId.ToString());
        }

        // Backing review session + analyst review created.
        var session = await db.ReviewSessions.SingleAsync(s => s.CaseId == _caseId);
        session.AnalystUserId.Should().Be(_analystUserId);
        session.Outcome.Should().Be("in-progress");

        var review = await db.AnalystReviews.SingleAsync(r => r.ReviewSessionId == session.Id);
        review.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AddAnnotation_ReusesExistingReview_OnSubsequentCalls()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AnalystAnnotationService>();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        await svc.AddAnnotationAsync(_caseId, _artifactId, 1, 2, 3, 4, "info", "first");
        await svc.AddAnnotationAsync(_caseId, _artifactId, 5, 6, 7, 8, "info", "second");
        await svc.AddAnnotationAsync(_caseId, _artifactId, 9, 10, 11, 12, "critical", "third");

        // Three findings, but only ONE AnalystReview row — the verdict
        // path creates its own row at decision time; we don't want a
        // review-per-annotation sprawl polluting the case timeline.
        (await db.Findings.CountAsync()).Should().Be(3);
        (await db.AnalystReviews.CountAsync()).Should().Be(1);
        (await db.ReviewSessions.CountAsync()).Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListForArtifact_FiltersOutAnnotations_FromOtherArtifacts()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AnalystAnnotationService>();

        await svc.AddAnnotationAsync(_caseId, _artifactId,        x: 1, y: 1, w: 10, h: 10, "info", "primary-1");
        await svc.AddAnnotationAsync(_caseId, _otherArtifactId,   x: 2, y: 2, w: 20, h: 20, "warning", "side-view");
        await svc.AddAnnotationAsync(_caseId, _artifactId,        x: 3, y: 3, w: 30, h: 30, "critical", "primary-2");

        var primary = await svc.ListForArtifactAsync(_artifactId);
        primary.Should().HaveCount(2);
        primary.Should().OnlyContain(a => a.ArtifactId == _artifactId);
        primary.Select(a => a.Note).Should().BeEquivalentTo(new[] { "primary-1", "primary-2" });

        var side = await svc.ListForArtifactAsync(_otherArtifactId);
        side.Should().HaveCount(1);
        side.Single().ArtifactId.Should().Be(_otherArtifactId);
        side.Single().Note.Should().Be("side-view");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DeleteAnnotation_RemovesOwnFinding_AndRefusesNonOwner()
    {
        // Owner creates an annotation and removes it — should succeed.
        using var ownerScope = _sp.CreateScope();
        var ownerSvc = ownerScope.ServiceProvider.GetRequiredService<AnalystAnnotationService>();
        var ownerDb = ownerScope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var f = await ownerSvc.AddAnnotationAsync(_caseId, _artifactId, 1, 2, 3, 4, "info", "to-delete");
        var ok = await ownerSvc.DeleteAnnotationAsync(f.Id);
        ok.Should().BeTrue();
        (await ownerDb.Findings.CountAsync(x => x.Id == f.Id)).Should().Be(0);

        // Different analyst (different user id) tries to delete an
        // annotation they didn't create — should refuse.
        var f2 = await ownerSvc.AddAnnotationAsync(_caseId, _artifactId, 5, 6, 7, 8, "info", "owned-by-A");

        // Build a fresh scope rooted in a different user-id provider.
        var foreignProvider = new ServiceCollection()
            .AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider(Guid.NewGuid()))
            .BuildServiceProvider();
        // Use the same DB but a foreign principal — easiest is to
        // construct the service directly so the existing test ITenantContext
        // and DbContext stay usable.
        var foreignSvc = new AnalystAnnotationService(
            ownerDb,
            new NoopEventPublisher(),
            ownerScope.ServiceProvider.GetRequiredService<ITenantContext>(),
            foreignProvider.GetRequiredService<AuthenticationStateProvider>(),
            NullLogger<AnalystAnnotationService>.Instance);

        var refused = await foreignSvc.DeleteAnnotationAsync(f2.Id);
        refused.Should().BeFalse();
        (await ownerDb.Findings.CountAsync(x => x.Id == f2.Id)).Should().Be(1,
            because: "non-owner deletes must not remove the row");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AddAnnotation_RejectsZeroSizedRectangle()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AnalystAnnotationService>();

        await FluentActions.Invoking(() =>
            svc.AddAnnotationAsync(_caseId, _artifactId, 1, 1, 0, 5, "info", null))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() =>
            svc.AddAnnotationAsync(_caseId, _artifactId, 1, 1, 5, -1, "info", null))
            .Should().ThrowAsync<ArgumentException>();
    }

    private void Seed()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var locationId = Guid.NewGuid();
        db.Locations.Add(new Location
        {
            Id = locationId, Code = "TEMA", Name = "Tema port",
            TimeZone = "Africa/Accra", IsActive = true, TenantId = 1,
        });
        db.Cases.Add(new InspectionCase
        {
            Id = _caseId, LocationId = locationId,
            SubjectType = CaseSubjectType.Container,
            SubjectIdentifier = "CONT-S20-B1-0001",
            State = InspectionWorkflowState.Open,
            OpenedAt = DateTimeOffset.UtcNow, StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = 1,
        });
        db.Scans.Add(new Scan
        {
            Id = _scanId, CaseId = _caseId,
            ScannerDeviceInstanceId = Guid.NewGuid(),
            Mode = "high-energy", CapturedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = "test-" + _scanId, TenantId = 1,
        });
        db.ScanArtifacts.Add(new ScanArtifact
        {
            Id = _artifactId, ScanId = _scanId, ArtifactKind = "Primary",
            StorageUri = "noop://" + _artifactId, MimeType = "image/png",
            ContentHash = "deadbeef", WidthPx = 1024, HeightPx = 768,
            CreatedAt = DateTimeOffset.UtcNow, TenantId = 1,
        });
        db.ScanArtifacts.Add(new ScanArtifact
        {
            Id = _otherArtifactId, ScanId = _scanId, ArtifactKind = "SideView",
            StorageUri = "noop://" + _otherArtifactId, MimeType = "image/png",
            ContentHash = "cafebabe", WidthPx = 1024, HeightPx = 768,
            CreatedAt = DateTimeOffset.UtcNow, TenantId = 1,
        });
        db.SaveChanges();
    }

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        private readonly Guid _userId;
        public FakeAuthStateProvider(Guid userId) { _userId = userId; }
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "test-analyst"),
                new Claim("nickerp:id", _userId.ToString()),
                new Claim("nickerp:tenant_id", "1"),
            }, "Test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }

    private sealed class NoopEventPublisher : IEventPublisher
    {
        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default) =>
            Task.FromResult(evt);
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default) =>
            Task.FromResult(events);
    }
}
