using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Pilot;
using NickERP.Portal.Components.Pages;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 43 Phase D — bunit page-render coverage for
/// <see cref="PilotReadiness"/>. Asserts the dashboard renders gate
/// cards, surfaces "what's needed" notes for NotYetObserved, breaks
/// out the multi-tenant invariant gate's three sub-checks, and
/// renders proof-event links when present.
/// </summary>
public sealed class PilotReadinessPageTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();
    private readonly StubReadinessService _stub = new();

    public PilotReadinessPageTests()
    {
        _ctx.Services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });
        _ctx.Services.AddSingleton<IPilotReadinessService>(_stub);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void Renders_AllFiveGateCards()
    {
        _stub.Report = NewReport(
            ScannerState: PilotReadinessState.Pass,
            EdgeState: PilotReadinessState.NotYetObserved,
            AnalystState: PilotReadinessState.NotYetObserved,
            ExternalState: PilotReadinessState.NotYetObserved,
            InvariantsNote: "rls_read_isolation:pass; system_context_register:pass; cross_tenant_export_gate:pass");

        var cut = _ctx.RenderComponent<PilotReadiness>();

        cut.Markup.Should().Contain("Scanner adapter wired");
        cut.Markup.Should().Contain("Edge round-trip");
        cut.Markup.Should().Contain("Analyst decisioned a real case");
        cut.Markup.Should().Contain("External system round-trip");
        cut.Markup.Should().Contain("Multi-tenant invariants");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RendersStateBadges_ForEachGate()
    {
        _stub.Report = NewReport(
            ScannerState: PilotReadinessState.Pass,
            EdgeState: PilotReadinessState.NotYetObserved,
            AnalystState: PilotReadinessState.Fail,
            ExternalState: PilotReadinessState.NotYetObserved);

        var cut = _ctx.RenderComponent<PilotReadiness>();

        cut.Markup.Should().Contain("PASS");
        cut.Markup.Should().Contain("NOT YET OBSERVED");
        cut.Markup.Should().Contain("FAIL");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RendersWhatsNeededNote_ForNotYetObservedGates()
    {
        _stub.Report = NewReport(
            ScannerState: PilotReadinessState.NotYetObserved,
            EdgeState: PilotReadinessState.NotYetObserved,
            AnalystState: PilotReadinessState.NotYetObserved,
            ExternalState: PilotReadinessState.NotYetObserved,
            ScannerNote: "register a scanner adapter and run at least one scan");

        var cut = _ctx.RenderComponent<PilotReadiness>();

        cut.Markup.Should().Contain("register a scanner adapter and run at least one scan");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RendersProofEventLink_WhenPassWithProofId()
    {
        var proofId = Guid.NewGuid();
        _stub.Report = NewReport(
            ScannerState: PilotReadinessState.Pass,
            EdgeState: PilotReadinessState.NotYetObserved,
            AnalystState: PilotReadinessState.NotYetObserved,
            ExternalState: PilotReadinessState.NotYetObserved,
            ScannerProof: proofId);

        var cut = _ctx.RenderComponent<PilotReadiness>();

        cut.Markup.Should().Contain("Proof event:");
        cut.Markup.Should().Contain(proofId.ToString());
        cut.Markup.Should().Contain("/audit-log?eventId=");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void MultiTenantGate_RendersSubCheckBreakdown_OnPass()
    {
        _stub.Report = NewReport(
            ScannerState: PilotReadinessState.Pass,
            EdgeState: PilotReadinessState.Pass,
            AnalystState: PilotReadinessState.Pass,
            ExternalState: PilotReadinessState.Pass,
            InvariantsState: PilotReadinessState.Pass,
            InvariantsNote: "rls_read_isolation:pass; system_context_register:pass; cross_tenant_export_gate:pass");

        var cut = _ctx.RenderComponent<PilotReadiness>();

        cut.Markup.Should().Contain("rls_read_isolation");
        cut.Markup.Should().Contain("system_context_register");
        cut.Markup.Should().Contain("cross_tenant_export_gate");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void MultiTenantGate_RendersSubCheckBreakdown_OnFail()
    {
        _stub.Report = NewReport(
            ScannerState: PilotReadinessState.Pass,
            EdgeState: PilotReadinessState.Pass,
            AnalystState: PilotReadinessState.Pass,
            ExternalState: PilotReadinessState.Pass,
            InvariantsState: PilotReadinessState.Fail,
            InvariantsNote: "rls_read_isolation:pass; system_context_register:fail(register drift — unregistered callers: foo.cs); cross_tenant_export_gate:pass");

        var cut = _ctx.RenderComponent<PilotReadiness>();

        cut.Markup.Should().Contain("rls_read_isolation");
        cut.Markup.Should().Contain("system_context_register");
        cut.Markup.Should().Contain("register drift");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RefreshButton_TriggersFreshProbeRun()
    {
        _stub.Report = NewReport(
            ScannerState: PilotReadinessState.NotYetObserved,
            EdgeState: PilotReadinessState.NotYetObserved,
            AnalystState: PilotReadinessState.NotYetObserved,
            ExternalState: PilotReadinessState.NotYetObserved);

        var cut = _ctx.RenderComponent<PilotReadiness>();
        var initialCalls = _stub.GetReadinessCallCount;

        // Click "Refresh now". The page disables the button while
        // refreshing so we wait for it to come back.
        var btn = cut.Find("button.portal-button");
        btn.Click();

        _stub.GetReadinessCallCount.Should().BeGreaterThan(initialCalls);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RendersGenericNote_WhenAnalystGateNotYetObserved()
    {
        _stub.Report = NewReport(
            ScannerState: PilotReadinessState.NotYetObserved,
            EdgeState: PilotReadinessState.NotYetObserved,
            AnalystState: PilotReadinessState.NotYetObserved,
            ExternalState: PilotReadinessState.NotYetObserved,
            AnalystNote: "No analyst has decisioned a non-synthetic case yet");

        var cut = _ctx.RenderComponent<PilotReadiness>();

        cut.Markup.Should().Contain("No analyst has decisioned a non-synthetic case yet");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Renders_GateIdsForOperatorTriage()
    {
        _stub.Report = NewReport(
            ScannerState: PilotReadinessState.Pass,
            EdgeState: PilotReadinessState.NotYetObserved,
            AnalystState: PilotReadinessState.NotYetObserved,
            ExternalState: PilotReadinessState.NotYetObserved);

        var cut = _ctx.RenderComponent<PilotReadiness>();

        // Stable gate IDs are surfaced for operator deep-link / triage.
        cut.Markup.Should().Contain(PilotReadinessGate.ScannerAdapter);
        cut.Markup.Should().Contain(PilotReadinessGate.EdgeRoundtrip);
        cut.Markup.Should().Contain(PilotReadinessGate.AnalystDecisionedRealCase);
        cut.Markup.Should().Contain(PilotReadinessGate.ExternalSystemRoundtrip);
        cut.Markup.Should().Contain(PilotReadinessGate.MultiTenantInvariants);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Renders_LastRefreshedTimestamp()
    {
        _stub.Report = NewReport(
            ScannerState: PilotReadinessState.Pass,
            EdgeState: PilotReadinessState.NotYetObserved,
            AnalystState: PilotReadinessState.NotYetObserved,
            ExternalState: PilotReadinessState.NotYetObserved);

        var cut = _ctx.RenderComponent<PilotReadiness>();

        cut.Markup.Should().Contain("Last refreshed");
    }

    // ---- helpers ----

    private static PilotReadinessReport NewReport(
        PilotReadinessState ScannerState,
        PilotReadinessState EdgeState,
        PilotReadinessState AnalystState,
        PilotReadinessState ExternalState,
        PilotReadinessState InvariantsState = PilotReadinessState.Pass,
        string? ScannerNote = null,
        string? EdgeNote = null,
        string? AnalystNote = null,
        string? ExternalNote = null,
        string? InvariantsNote = null,
        Guid? ScannerProof = null)
    {
        var observedAt = DateTimeOffset.UtcNow;
        return new PilotReadinessReport(
            TenantId: 1,
            ObservedAt: observedAt,
            Gates: new[]
            {
                new PilotReadinessGateResult(PilotReadinessGate.ScannerAdapter, ScannerState, observedAt, ScannerProof, ScannerNote),
                new PilotReadinessGateResult(PilotReadinessGate.EdgeRoundtrip, EdgeState, observedAt, null, EdgeNote),
                new PilotReadinessGateResult(PilotReadinessGate.AnalystDecisionedRealCase, AnalystState, observedAt, null, AnalystNote),
                new PilotReadinessGateResult(PilotReadinessGate.ExternalSystemRoundtrip, ExternalState, observedAt, null, ExternalNote),
                new PilotReadinessGateResult(PilotReadinessGate.MultiTenantInvariants, InvariantsState, observedAt, null, InvariantsNote ?? "rls_read_isolation:pass; system_context_register:pass; cross_tenant_export_gate:pass"),
            });
    }

    private sealed class StubReadinessService : IPilotReadinessService
    {
        public PilotReadinessReport? Report { get; set; }
        public int GetReadinessCallCount { get; private set; }

        public Task<PilotReadinessReport> GetReadinessAsync(long tenantId, CancellationToken ct = default)
        {
            GetReadinessCallCount++;
            if (Report is null)
            {
                Report = new PilotReadinessReport(tenantId, DateTimeOffset.UtcNow, Array.Empty<PilotReadinessGateResult>());
            }
            return Task.FromResult(Report);
        }
    }

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "admin"),
                new Claim("nickerp:id", Guid.NewGuid().ToString()),
                new Claim("nickerp:tenant_id", "1"),
            }, "Test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }
}
