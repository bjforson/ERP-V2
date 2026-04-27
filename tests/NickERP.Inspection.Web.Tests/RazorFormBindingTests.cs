using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Database;
using NickERP.Inspection.Imaging;
using NickERP.Inspection.Web.Components.Pages;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Identity.Database;
using NickERP.Platform.Plugins;
using NickERP.Platform.Tenancy;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Razor SSR form-binding regression test. Renders <c>NewCase</c> and
/// <c>LocationAssignments</c> via bunit; the regression we guard fired
/// when <c>[SupplyParameterFromForm]</c> properties were declared with
/// <c>null!</c> initializers + <c>OnParametersSet ??=</c> reassignment —
/// the page would NRE on first render. We only assert that rendering
/// completes and a <c>&lt;form&gt;</c> appears in the markup; that's the
/// minimum signal the regression has not returned.
///
/// Docker is not available in this environment, so we use the EF Core
/// in-memory provider in lieu of Postgres / WebApplicationFactory. The
/// regression we're guarding fires during component initialization,
/// well before the DbContext is first awaited, so the in-memory provider
/// is sufficient.
/// </summary>
public sealed class RazorFormBindingTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();

    public RazorFormBindingTests()
    {
        _ctx.Services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase("inspection-bunit-" + Guid.NewGuid())
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        _ctx.Services.AddDbContext<IdentityDbContext>(o =>
            o.UseInMemoryDatabase("identity-bunit-" + Guid.NewGuid())
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        _ctx.Services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });

        _ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());

        // CaseWorkflowService is @inject'd by NewCase. The constructor needs
        // every one of these — the rendering path doesn't call any of their
        // methods, so empty stubs are sufficient.
        _ctx.Services.AddSingleton<IEventPublisher, NoopEventPublisher>();
        _ctx.Services.AddSingleton<IPluginRegistry>(new PluginRegistry(Array.Empty<RegisteredPlugin>()));
        _ctx.Services.AddSingleton<IImageStore, NoopImageStore>();
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        _ctx.Services.AddScoped<CaseWorkflowService>();
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void NewCase_AndLocationAssignments_RenderWithoutFormBindingNre()
    {
        // Regression guarded: [SupplyParameterFromForm] NRE on first render of
        // NewCase.razor and LocationAssignments.razor (the two pages flagged
        // in PLAN.md F2 §178 #5).
        var newCase = _ctx.RenderComponent<NewCase>();
        newCase.Markup.Should().Contain("<form", because: "NewCase renders an EditForm element");

        var assignments = _ctx.RenderComponent<LocationAssignments>();
        assignments.Markup.Should().Contain("<form", because: "LocationAssignments renders an EditForm element");
    }

    // ---------------- stub services -----------------

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "test-user"),
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
}
