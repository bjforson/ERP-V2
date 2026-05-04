using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.AnalysisServices;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Inspection.Imaging;
using NickERP.Inspection.Web.Components.Pages;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Database.Entities;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Plugins;
using NickERP.Platform.Tenancy;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 20 / B1.1 — bunit coverage for the new tabbed
/// <see cref="CaseDetail"/> layout. Asserts:
///
///   1. The default landing tab is "Overview" — when no <c>?tab=</c>
///      query string is supplied, the workflow surface (Run authority
///      checks, Set verdict, etc.) renders. Audit / ICUMS / gallery
///      panes do NOT render until activated.
///   2. The Image gallery tab lazy-loads its data — passing
///      <c>?tab=gallery</c> triggers the artifact fetch and surfaces
///      the gallery grid.
///   3. The History tab queries the AuditDbContext — events for the
///      case render as table rows; events for unrelated cases stay
///      filtered out (defense-in-depth: the page filter, not just RLS).
///
/// Docker is unavailable, so EF in-memory + a custom audit DbContext
/// subclass with a JsonDocument↔string converter for the audit jsonb
/// column. Same precedent as <see cref="ScanViewerTests"/>.
/// </summary>
public sealed class CaseDetailTabsTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();
    private readonly Guid _caseId = Guid.NewGuid();
    private readonly Guid _otherCaseId = Guid.NewGuid();
    private readonly Guid _locationId = Guid.NewGuid();
    private readonly Guid _scannerId = Guid.NewGuid();
    private readonly Guid _scanId = Guid.NewGuid();
    private readonly Guid _artifactId = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 5, 4, 10, 0, 0, TimeSpan.Zero);

    public CaseDetailTabsTests()
    {
        var dbName = "case-detail-tabs-" + Guid.NewGuid();
        _ctx.Services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        // The audit DbContext requires a model-build override to map the
        // jsonb-typed Payload column onto the EF in-memory provider, which
        // can't natively serialize JsonDocument. The subclass below
        // converts to/from a string round-trip so events seed cleanly.
        // We register InMemoryAuditDbContext as the concrete type AND
        // resolve AuditDbContext to it so the page's `@inject AuditDbContext`
        // sees the test subclass.
        var auditDbName = "audit-tabs-" + Guid.NewGuid();
        _ctx.Services.AddDbContext<InMemoryAuditDbContext>(o =>
            o.UseInMemoryDatabase(auditDbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        _ctx.Services.AddScoped<AuditDbContext>(sp => sp.GetRequiredService<InMemoryAuditDbContext>());

        _ctx.Services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });

        _ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));

        // CaseWorkflowService is @inject'd; its constructor pulls quite a
        // few dependencies even when the page never invokes any of its
        // methods. Lightweight stubs match the pattern from RazorFormBindingTests.
        _ctx.Services.AddSingleton<IEventPublisher, NoopEventPublisher>();
        _ctx.Services.AddSingleton<IPluginRegistry>(new PluginRegistry(Array.Empty<RegisteredPlugin>()));
        _ctx.Services.AddSingleton<IImageStore, NoopImageStore>();
        _ctx.Services.AddScoped<CaseWorkflowService>();

        // Sprint 14 / VP6 Phase C — claim service the header banner uses.
        _ctx.Services.AddCaseClaimAndVisibility();

        Seed();
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void CaseDetail_DefaultsToOverviewTab_RendersWorkflowActions()
    {
        var cut = _ctx.RenderComponent<CaseDetail>(p => p
            .Add(x => x.CaseId, _caseId));

        cut.WaitForAssertion(
            () => cut.Markup.Should().NotContain("Loading"),
            TimeSpan.FromSeconds(5));

        // Header rendered + the tab strip is present.
        cut.Markup.Should().Contain("CONT-S20-B1-CASE",
            because: "the header carries the case SubjectIdentifier");
        cut.Markup.Should().Contain("inspection-tabs",
            because: "the tab strip should be present even on the default tab");
        cut.Markup.Should().Contain("Authority documents",
            because: "the ICUMS tab label appears in the strip even when not active");

        // Overview-tab workflow surface rendered.
        cut.Markup.Should().Contain("Workflow actions",
            because: "the overview tab carries the workflow card");
        cut.Markup.Should().Contain("Run authority checks",
            because: "the overview tab carries the rules-evaluation button");

        // Other tab panes did NOT render — gallery grid + history table
        // are lazy-loaded only on tab activation.
        cut.Markup.Should().NotContain("inspection-gallery-grid",
            because: "gallery pane only renders when ?tab=gallery is active");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CaseDetail_GalleryTab_LazyLoadsArtifactGrid()
    {
        // bunit's [SupplyParameterFromQuery] support requires the URL
        // to carry the value; we navigate before render.
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("tab", "gallery"));

        var cut = _ctx.RenderComponent<CaseDetail>(p => p
            .Add(x => x.CaseId, _caseId));

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("Image gallery"),
            TimeSpan.FromSeconds(5));

        // Gallery grid container rendered.
        cut.Markup.Should().Contain("inspection-gallery-grid",
            because: "the gallery tab paints the thumbnail grid");

        // Tile points back at the W/L viewer for the seeded scan.
        cut.Markup.Should().Contain($"/cases/{_caseId}/scans/{_scanId}",
            because: "each tile links to the per-scan viewer");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CaseDetail_HistoryTab_FiltersEventsToCurrentCase()
    {
        SeedAuditEvents();

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("tab", "history"));

        var cut = _ctx.RenderComponent<CaseDetail>(p => p
            .Add(x => x.CaseId, _caseId));

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("nickerp.inspection.case_opened"),
            TimeSpan.FromSeconds(5));

        // Events for THIS case render — three events seeded for _caseId.
        cut.Markup.Should().Contain("nickerp.inspection.case_opened");
        cut.Markup.Should().Contain("nickerp.inspection.scan_recorded");
        cut.Markup.Should().Contain("nickerp.inspection.verdict_set");

        // The unrelated-case event must NOT leak into this view — the
        // page-level EntityType+EntityId filter should hide it.
        cut.Markup.Should().NotContain("nickerp.inspection.unrelated",
            because: "the page filters audit rows to (EntityType=InspectionCase, EntityId=this caseId)");
    }

    private void Seed()
    {
        using var scope = _ctx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        db.Locations.Add(new Location
        {
            Id = _locationId, Code = "TEMA", Name = "Tema port",
            TimeZone = "Africa/Accra", IsActive = true, TenantId = 1,
        });
        db.ScannerDeviceInstances.Add(new ScannerDeviceInstance
        {
            Id = _scannerId, LocationId = _locationId, TypeCode = "fs6000",
            DisplayName = "FS6000 #1", IsActive = true, TenantId = 1,
        });
        db.Cases.Add(new InspectionCase
        {
            Id = _caseId, LocationId = _locationId,
            SubjectType = CaseSubjectType.Container,
            SubjectIdentifier = "CONT-S20-B1-CASE",
            State = InspectionWorkflowState.Open,
            OpenedAt = _now, StateEnteredAt = _now,
            TenantId = 1,
        });
        db.Scans.Add(new Scan
        {
            Id = _scanId, CaseId = _caseId,
            ScannerDeviceInstanceId = _scannerId,
            Mode = "high-energy", CapturedAt = _now,
            IdempotencyKey = "tabs-test-" + _scanId, TenantId = 1,
        });
        db.ScanArtifacts.Add(new ScanArtifact
        {
            Id = _artifactId, ScanId = _scanId, ArtifactKind = "Primary",
            StorageUri = "noop://" + _artifactId, MimeType = "image/png",
            ContentHash = "tabsdead", WidthPx = 1024, HeightPx = 768,
            CreatedAt = _now, TenantId = 1,
        });
        db.SaveChanges();
    }

    private void SeedAuditEvents()
    {
        using var scope = _ctx.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        audit.Events.Add(NewEvent("nickerp.inspection.case_opened", "InspectionCase", _caseId.ToString()));
        audit.Events.Add(NewEvent("nickerp.inspection.scan_recorded", "InspectionCase", _caseId.ToString()));
        audit.Events.Add(NewEvent("nickerp.inspection.verdict_set", "InspectionCase", _caseId.ToString()));
        // Foreign-case event — should NOT appear in the History pane.
        audit.Events.Add(NewEvent("nickerp.inspection.unrelated", "InspectionCase", _otherCaseId.ToString()));
        audit.SaveChanges();
    }

    private DomainEventRow NewEvent(string eventType, string entityType, string entityId) =>
        new()
        {
            EventId = Guid.NewGuid(),
            TenantId = 1,
            ActorUserId = Guid.NewGuid(),
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            Payload = System.Text.Json.JsonDocument.Parse("{}"),
            OccurredAt = _now,
            IngestedAt = _now,
            IdempotencyKey = "audit-" + Guid.NewGuid(),
        };

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "test-analyst"),
                new Claim("nickerp:id", Guid.NewGuid().ToString()),
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

    private sealed class NoopImageStore : IImageStore
    {
        public Task<string> SaveSourceAsync(string contentHash, string fileExtension, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) =>
            Task.FromResult("noop://" + contentHash);
        public Task<byte[]> ReadSourceAsync(string contentHash, string fileExtension, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());
        public Task<string> SaveRenderAsync(Guid scanArtifactId, string kind, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) =>
            Task.FromResult("noop://" + scanArtifactId);
        public Stream? OpenRenderRead(Guid scanArtifactId, string kind) => null;
    }

    /// <summary>
    /// Test-only AuditDbContext subclass that converts the jsonb-typed
    /// Payload column to a string round-trip so the EF in-memory
    /// provider can persist + materialise events. Production code uses
    /// the real AuditDbContext + Postgres jsonb mapping.
    /// </summary>
    private sealed class InMemoryAuditDbContext : AuditDbContext
    {
        // AddDbContext<InMemoryAuditDbContext> registers
        // DbContextOptions<InMemoryAuditDbContext>; we project that into
        // the base DbContextOptions<AuditDbContext> so EF sees the right
        // metadata for the base context.
        public InMemoryAuditDbContext(DbContextOptions<InMemoryAuditDbContext> options)
            : base(BuildBaseOptions(options))
        {
        }

        private static DbContextOptions<AuditDbContext> BuildBaseOptions(
            DbContextOptions<InMemoryAuditDbContext> source)
        {
            // Use the same in-memory database name as the registered options.
            var builder = new DbContextOptionsBuilder<AuditDbContext>();
            foreach (var ext in source.Extensions)
            {
                ((Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsBuilderInfrastructure)builder)
                    .AddOrUpdateExtension(ext);
            }
            return builder.Options;
        }

        protected override void OnAuditModelCreating(ModelBuilder modelBuilder)
        {
            base.OnAuditModelCreating(modelBuilder);
            modelBuilder.Entity<DomainEventRow>()
                .Property(x => x.Payload)
                .HasConversion(
                    v => v.RootElement.GetRawText(),
                    v => System.Text.Json.JsonDocument.Parse(v, default));
        }
    }
}
