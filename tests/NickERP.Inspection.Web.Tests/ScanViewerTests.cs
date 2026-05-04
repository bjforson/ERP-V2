using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Components.Pages;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint V4 — bunit coverage for the analyst W/L viewer page
/// (<see cref="ScanViewer"/>). The W/L math itself lives in JS
/// (<c>wwwroot/js/scan-viewer.js</c>) and is exercised at runtime in
/// the browser; here we assert the Razor component:
///
///   1. Hydrates from <see cref="InspectionDbContext"/> and renders the
///      page header with subject identifier + scan timestamp + canvas
///      element + the <c>/api/images/{id}/preview</c> URL surfaced in
///      the source panel and recorded in the JS interop call.
///
///   2. Falls back to a "no preview yet — try again" message rather than
///      throwing when there's no <see cref="ScanArtifact"/> row attached
///      to the scan (e.g. the ingestion pipeline hasn't completed yet).
///
/// Docker is not available in this environment so we use the EF Core
/// in-memory provider — same precedent as
/// <see cref="RazorFormBindingTests"/> and
/// <see cref="RuleEvaluationPersistenceTests"/>. The page's interop
/// calls run only inside <c>OnAfterRenderAsync(firstRender: true)</c>;
/// bunit's built-in <see cref="BunitJSInterop"/> records each invocation
/// without needing a real browser.
/// </summary>
public sealed class ScanViewerTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();
    private readonly Guid _caseId = Guid.NewGuid();
    private readonly Guid _scanId = Guid.NewGuid();
    private readonly Guid _artifactId = Guid.NewGuid();
    private readonly Guid _locationId = Guid.NewGuid();
    private readonly Guid _scannerId = Guid.NewGuid();
    private readonly DateTimeOffset _capturedAt = new(2026, 4, 26, 10, 30, 0, TimeSpan.Zero);

    public ScanViewerTests()
    {
        // Stable in-memory DB name per test fixture — without this, each
        // DbContext scope would build its own database and the seed rows
        // would never be visible to the page's query scope.
        var dbName = "scan-viewer-bunit-" + Guid.NewGuid();
        _ctx.Services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

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

        // Sprint 20 / B1.2 — annotation service the page now @injects.
        // Tests don't exercise annotation persistence; the service still
        // needs to resolve so component initialisation completes.
        _ctx.Services.AddSingleton<IEventPublisher, NoopEventPublisher>();
        _ctx.Services.AddScoped<AnalystAnnotationService>();

        // Pre-register the interop calls the page makes on first render.
        // The page also `import`s the JS module; bunit's JSInterop returns
        // a default IJSObjectReference for that, against which we set up
        // `init` (returns {width, height}) and the void calls. Mouse-driven
        // calls (getPixelAt / setRoi) aren't exercised here because the
        // bunit DOM doesn't dispatch synthetic mouse events through the
        // canvas; that's deliberately covered in-browser via manual QA.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    /// <summary>
    /// AC: rendering the page for a fully-seeded case + scan + primary
    /// artifact produces the page header (subject identifier + timestamp),
    /// a &lt;canvas&gt; element, and surfaces the
    /// <c>/api/images/{artifactId}/preview</c> URL — both in the markup
    /// (Source panel) and in the JS-interop <c>init</c> call args.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void ScanViewer_RendersPageHeader_AndCanvasElement_ForKnownScan()
    {
        SeedFullScan();

        // Sanity check that the seed is visible to a fresh scope —
        // without this, a DI mistake (e.g. each scope creating its own
        // in-memory DB) shows up as a misleading "page never rendered
        // anything past 'Loading…'" timeout in WaitForAssertion below.
        using (var checkScope = _ctx.Services.CreateScope())
        {
            var checkDb = checkScope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            checkDb.Cases.Count().Should().Be(1, because: "seed wrote one case");
        }

        var cut = _ctx.RenderComponent<ScanViewer>(p => p
            .Add(x => x.CaseId, _caseId)
            .Add(x => x.ScanId, _scanId));

        // OnInitializedAsync awaits the DbContext; the synchronous render
        // returns "<p>Loading…</p>" first. WaitForAssertion drives bunit's
        // dispatcher until the component re-renders with the hydrated data.
        cut.WaitForAssertion(
            () => cut.Markup.Should().NotContain("Loading"),
            TimeSpan.FromSeconds(5));

        // Subject identifier shows up in the page header.
        cut.Markup.Should().Contain("CONT-V4-0001",
            because: "ScanViewer's header includes the case SubjectIdentifier");

        // Scan timestamp is present (yyyy-MM-dd HH:mm:ss format used
        // throughout the inspection module).
        cut.Markup.Should().Contain(_capturedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            because: "the scan capture time anchors the analyst's mental model");

        // Canvas element rendered for the JS interop module to bind to.
        cut.Markup.Should().Contain("<canvas",
            because: "the W/L viewer paints its raster output into a <canvas>");

        // Preview URL surfaced in the Source panel.
        var expectedUrl = $"/api/images/{_artifactId}/preview";
        cut.Markup.Should().Contain(expectedUrl,
            because: "the Source panel surfaces the preview URL the canvas is bound to");

        // The interop init call recorded the same URL — proves the wiring
        // matches what we put in the markup. We pull the most-recent init
        // invocation from bunit's recorder; in loose mode it returns a
        // default value but still records args.
        var initInvocations = _ctx.JSInterop.Invocations
            .Where(i => i.Identifier == "init")
            .ToList();
        initInvocations.Should().NotBeEmpty(
            because: "OnAfterRenderAsync(firstRender:true) calls scan-viewer.init");
        initInvocations[^1].Arguments.Should().Contain(expectedUrl,
            because: "init must be handed the same /api/images/.../preview URL the markup advertises");
    }

    /// <summary>
    /// AC: when no <see cref="ScanArtifact"/> exists yet for the scan,
    /// the page renders a friendly "no primary artifact" message instead
    /// of throwing — this is the path we expect for a fresh scan whose
    /// ingestion step lost a race against the analyst clicking through.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void ScanViewer_FallsBack_OnMissingArtifact()
    {
        SeedScanWithoutArtifact();

        var cut = _ctx.RenderComponent<ScanViewer>(p => p
            .Add(x => x.CaseId, _caseId)
            .Add(x => x.ScanId, _scanId));

        // Wait for OnInitializedAsync to finish hydrating — same async
        // pattern as the seeded path above.
        cut.WaitForAssertion(
            () => cut.Markup.Should().NotContain("Loading"),
            TimeSpan.FromSeconds(5));

        // Page header still renders — we want the analyst to see *which*
        // case they're looking at while the artifact catches up.
        cut.Markup.Should().Contain("CONT-V4-NOART",
            because: "the header should still render so the user knows the context");

        // Friendly fallback rather than a NullReferenceException.
        cut.Markup.Should().Contain("No primary artifact",
            because: "the page must surface the missing-artifact state, not throw");

        // Critically: NO canvas element should have been emitted, because
        // there's nothing to paint. This prevents the JS module from
        // running against a meaningless URL.
        cut.Markup.Should().NotContain("<canvas",
            because: "the canvas only renders when there's an artifact to paint");
    }

    // ---------------- seeding helpers ----------------

    private void SeedFullScan()
    {
        using var scope = _ctx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        db.Locations.Add(new Location
        {
            Id = _locationId,
            Code = "TEMA",
            Name = "Tema port",
            TimeZone = "Africa/Accra",
            IsActive = true,
            TenantId = 1
        });
        db.ScannerDeviceInstances.Add(new ScannerDeviceInstance
        {
            Id = _scannerId,
            LocationId = _locationId,
            TypeCode = "fs6000",
            DisplayName = "FS6000 #1",
            IsActive = true,
            TenantId = 1
        });
        db.Cases.Add(new InspectionCase
        {
            Id = _caseId,
            LocationId = _locationId,
            SubjectType = CaseSubjectType.Container,
            SubjectIdentifier = "CONT-V4-0001",
            State = InspectionWorkflowState.Open,
            OpenedAt = _capturedAt,
            StateEnteredAt = _capturedAt,
            TenantId = 1
        });
        db.Scans.Add(new Scan
        {
            Id = _scanId,
            CaseId = _caseId,
            ScannerDeviceInstanceId = _scannerId,
            Mode = "high-energy",
            CapturedAt = _capturedAt,
            IdempotencyKey = "v4-test-" + _scanId,
            TenantId = 1
        });
        db.ScanArtifacts.Add(new ScanArtifact
        {
            Id = _artifactId,
            ScanId = _scanId,
            ArtifactKind = "Primary",
            StorageUri = "noop://" + _artifactId,
            MimeType = "image/png",
            ContentHash = "deadbeef",
            WidthPx = 1024,
            HeightPx = 768,
            CreatedAt = _capturedAt,
            TenantId = 1
        });
        db.SaveChanges();
    }

    private void SeedScanWithoutArtifact()
    {
        using var scope = _ctx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        db.Locations.Add(new Location
        {
            Id = _locationId,
            Code = "TEMA",
            Name = "Tema port",
            TimeZone = "Africa/Accra",
            IsActive = true,
            TenantId = 1
        });
        db.ScannerDeviceInstances.Add(new ScannerDeviceInstance
        {
            Id = _scannerId,
            LocationId = _locationId,
            TypeCode = "fs6000",
            DisplayName = "FS6000 #1",
            IsActive = true,
            TenantId = 1
        });
        db.Cases.Add(new InspectionCase
        {
            Id = _caseId,
            LocationId = _locationId,
            SubjectType = CaseSubjectType.Container,
            SubjectIdentifier = "CONT-V4-NOART",
            State = InspectionWorkflowState.Open,
            OpenedAt = _capturedAt,
            StateEnteredAt = _capturedAt,
            TenantId = 1
        });
        db.Scans.Add(new Scan
        {
            Id = _scanId,
            CaseId = _caseId,
            ScannerDeviceInstanceId = _scannerId,
            Mode = "high-energy",
            CapturedAt = _capturedAt,
            IdempotencyKey = "v4-test-noart-" + _scanId,
            TenantId = 1
        });
        // No ScanArtifact — this is the fallback path.
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

    /// <summary>No-op event publisher for the AnalystAnnotationService DI graph.</summary>
    private sealed class NoopEventPublisher : IEventPublisher
    {
        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default) =>
            Task.FromResult(evt);
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default) =>
            Task.FromResult(events);
    }
}
