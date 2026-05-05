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
/// Sprint 32 FU-C — bunit coverage for the new split Rules tab on
/// <see cref="CaseDetail"/>. Asserts:
///
///   1. Mixed Findings (validation + analyst) render in their own
///      sub-panes when the Rules tab is active. Validation findings
///      surface in the "Validation rules" card; analyst findings stay
///      in "Analyst findings".
///   2. The validation pane formats the rule id rather than the full
///      <c>validation.</c> prefix and links to <c>/admin/rules/{ruleId}</c>
///      for drill-down.
///   3. Sprint 31 completeness Findings (forward-compat: prefix
///      <c>completeness.</c>) are routed to the validation pane too.
///
/// Reuses the in-memory + JsonDocument-converter pattern from
/// <see cref="CaseDetailTabsTests"/>.
/// </summary>
public sealed class CaseDetailValidationPaneTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();
    private readonly Guid _caseId = Guid.NewGuid();
    private readonly Guid _locationId = Guid.NewGuid();
    private readonly Guid _scannerId = Guid.NewGuid();
    private readonly Guid _scanId = Guid.NewGuid();
    private readonly Guid _artifactId = Guid.NewGuid();
    private readonly Guid _reviewSessionId = Guid.NewGuid();
    private readonly Guid _analystReviewId = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 5, 4, 10, 0, 0, TimeSpan.Zero);

    public CaseDetailValidationPaneTests()
    {
        var dbName = "case-detail-validation-" + Guid.NewGuid();
        _ctx.Services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        var auditDbName = "audit-validation-" + Guid.NewGuid();
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

        _ctx.Services.AddSingleton<IEventPublisher, NoopEventPublisher>();
        _ctx.Services.AddSingleton<IPluginRegistry>(new PluginRegistry(Array.Empty<RegisteredPlugin>()));
        _ctx.Services.AddSingleton<IImageStore, NoopImageStore>();
        _ctx.Services.AddScoped<CaseWorkflowService>();
        _ctx.Services.AddCaseClaimAndVisibility();

        Seed();
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void RulesTab_MixedFindings_RoutesValidationToValidationPaneAndAnalystToAnalystPane()
    {
        SeedMixedFindings();

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("tab", "rules"));

        var cut = _ctx.RenderComponent<CaseDetail>(p => p
            .Add(x => x.CaseId, _caseId));

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("Validation rules"),
            TimeSpan.FromSeconds(5));

        // Both panes rendered.
        cut.Markup.Should().Contain("data-testid=\"validation-findings-pane\"",
            because: "Sprint 32 FU-C splits the tab into two panes");
        cut.Markup.Should().Contain("data-testid=\"analyst-findings-pane\"",
            because: "the analyst-observation list keeps its own pane");

        // The validation pane carries the rule id (post-prefix) — NOT the
        // full validation.* finding-type string. Sprint 28's RuleId for
        // the test seed is "customsgh.port_match".
        cut.Markup.Should().Contain("customsgh.port_match",
            because: "the rule-id column extracts the post-prefix segment of validation.{ruleId}");

        // The drill-down link points at the Sprint 28 rules-admin page.
        cut.Markup.Should().Contain("/admin/rules/customsgh.port_match",
            because: "FU-C asks for a hyperlink to /admin/rules/{ruleId} for drill-down");

        // The analyst-pane row shows the full finding-type label
        // (analyst.annotation) — analyst entries don't get reformatted.
        cut.Markup.Should().Contain("analyst.annotation",
            because: "the analyst pane keeps the verbatim FindingType label");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RulesTab_OnlyValidationFindings_AnalystPaneShowsEmptyStateAndValidationPaneRendersRows()
    {
        SeedValidationOnlyFindings();

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("tab", "rules"));

        var cut = _ctx.RenderComponent<CaseDetail>(p => p
            .Add(x => x.CaseId, _caseId));

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("Validation rules (1)"),
            TimeSpan.FromSeconds(5));

        // Validation pane has the row.
        cut.Markup.Should().Contain("Validation rules (1)");
        // Analyst pane is empty — same default as before.
        cut.Markup.Should().Contain("Analyst findings (0)");
        cut.Markup.Should().Contain("No findings recorded yet",
            because: "the analyst pane's empty-state copy stays unchanged when there are no analyst-authored Findings");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RulesTab_CompletenessFinding_RoutedToValidationPane()
    {
        // Sprint 31 forward-compat — completeness.* prefix is also
        // system-authored. Verifies the prefix list is the source of truth
        // (not just validation.*).
        SeedCompletenessFinding();

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("tab", "rules"));

        var cut = _ctx.RenderComponent<CaseDetail>(p => p
            .Add(x => x.CaseId, _caseId));

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("Validation rules (1)"),
            TimeSpan.FromSeconds(5));

        // The rule id strip-out should leave just the post-prefix tail.
        cut.Markup.Should().Contain("manifest_required",
            because: "completeness.manifest_required → manifest_required after the classifier strips the prefix");
        cut.Markup.Should().Contain("/admin/rules/manifest_required",
            because: "the drill-down link uses the same admin-rules URL shape");
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
            SubjectIdentifier = "CONT-S32-FUC-CASE",
            State = InspectionWorkflowState.Open,
            OpenedAt = _now, StateEnteredAt = _now,
            TenantId = 1,
        });
        db.Scans.Add(new Scan
        {
            Id = _scanId, CaseId = _caseId,
            ScannerDeviceInstanceId = _scannerId,
            Mode = "high-energy", CapturedAt = _now,
            IdempotencyKey = "fuc-test-" + _scanId, TenantId = 1,
        });
        db.ScanArtifacts.Add(new ScanArtifact
        {
            Id = _artifactId, ScanId = _scanId, ArtifactKind = "Primary",
            StorageUri = "noop://" + _artifactId, MimeType = "image/png",
            ContentHash = "fucdead", WidthPx = 1024, HeightPx = 768,
            CreatedAt = _now, TenantId = 1,
        });
        // ReviewSession + AnalystReview are required for the Findings join
        // in CaseDetail — Findings are scoped via Review→Session→CaseId.
        db.ReviewSessions.Add(new ReviewSession
        {
            Id = _reviewSessionId, CaseId = _caseId,
            AnalystUserId = Guid.NewGuid(),
            StartedAt = _now,
            TenantId = 1,
        });
        db.AnalystReviews.Add(new AnalystReview
        {
            Id = _analystReviewId, ReviewSessionId = _reviewSessionId,
            TimeToDecisionMs = 0, ConfidenceScore = 1.0,
            CreatedAt = _now, TenantId = 1,
        });
        db.SaveChanges();
    }

    /// <summary>
    /// Two Findings — one validation-engine output (validation.{ruleId}),
    /// one analyst annotation. The classifier should split them.
    /// </summary>
    private void SeedMixedFindings()
    {
        using var scope = _ctx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        db.Findings.AddRange(
            new Finding
            {
                Id = Guid.NewGuid(),
                AnalystReviewId = _analystReviewId,
                FindingType = "validation.customsgh.port_match",
                Severity = "warning",
                Note = "Scanner location mismatches the declared port (TKD vs TMA).",
                LocationInImageJson = "{}",
                CreatedAt = _now,
                TenantId = 1,
            },
            new Finding
            {
                Id = Guid.NewGuid(),
                AnalystReviewId = _analystReviewId,
                FindingType = "analyst.annotation",
                Severity = "info",
                Note = "Possible organic mass in lower-left quadrant.",
                LocationInImageJson = "{\"x\":10,\"y\":20,\"w\":100,\"h\":80}",
                CreatedAt = _now.AddSeconds(1),
                TenantId = 1,
            });
        db.SaveChanges();
    }

    private void SeedValidationOnlyFindings()
    {
        using var scope = _ctx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        db.Findings.Add(new Finding
        {
            Id = Guid.NewGuid(),
            AnalystReviewId = _analystReviewId,
            FindingType = "validation.customsgh.port_match",
            Severity = "warning",
            Note = "Mismatch.",
            LocationInImageJson = "{}",
            CreatedAt = _now,
            TenantId = 1,
        });
        db.SaveChanges();
    }

    private void SeedCompletenessFinding()
    {
        using var scope = _ctx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        db.Findings.Add(new Finding
        {
            Id = Guid.NewGuid(),
            AnalystReviewId = _analystReviewId,
            FindingType = "completeness.manifest_required",
            Severity = "warning",
            Note = "Manifest absent at decision time.",
            LocationInImageJson = "{}",
            CreatedAt = _now,
            TenantId = 1,
        });
        db.SaveChanges();
    }

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
    /// Same audit-dbcontext shim used in <see cref="CaseDetailTabsTests"/> —
    /// the EF in-memory provider can't natively serialize JsonDocument so
    /// we add a value converter on top of the production model.
    /// </summary>
    private sealed class InMemoryAuditDbContext : AuditDbContext
    {
        public InMemoryAuditDbContext(DbContextOptions<InMemoryAuditDbContext> options)
            : base(BuildBaseOptions(options))
        {
        }

        private static DbContextOptions<AuditDbContext> BuildBaseOptions(
            DbContextOptions<InMemoryAuditDbContext> source)
        {
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
