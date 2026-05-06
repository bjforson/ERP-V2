using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Database.Pilot;
using NickERP.Platform.Tenancy.Database.Services;
using NickERP.Platform.Tenancy.Pilot;
using NickERP.Portal.Components.Pages;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 53 Phase C — bunit page-render coverage proving the
/// <see cref="PilotReadiness"/> dashboard renders an all-green state
/// when the production <see cref="PilotReadinessService"/> is driven
/// by an <see cref="IInspectionPilotProbeDataSource"/> shaped exactly
/// like what the Sprint 53 PilotAcceptance integration scenario
/// produces.
///
/// <para>
/// <b>Why a separate file from <see cref="PilotReadinessPageTests"/>.</b>
/// The existing page tests stub <see cref="IPilotReadinessService"/>
/// directly with a canned <see cref="PilotReadinessReport"/>. That's
/// the right shape for proving page-rendering of arbitrary states.
/// This file goes one layer deeper: it injects mock data through the
/// probe-data-source contract, runs the REAL
/// <see cref="PilotReadinessService"/> over EF in-memory contexts, and
/// asserts the page renders all-green. The closer the test couples to
/// production wiring, the harder it is to silently break the
/// dashboard with a service-level refactor.
/// </para>
///
/// <para>
/// <b>Trait.</b> Tagged <c>Integration</c> like
/// <see cref="PilotReadinessPageTests"/> so the unit-test default
/// filter (<c>Category!=Integration</c>) skips them, preserving the
/// fast unit-test runtime.
/// </para>
/// </summary>
public sealed class PilotReadinessPageBunitTests : IDisposable
{
    private const long TenantId = 1L;
    private static readonly DateTimeOffset Now = new(2026, 5, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly BunitTestContext _ctx = new();

    public PilotReadinessPageBunitTests()
    {
        // Tenant context — render page under tenant 1.
        _ctx.Services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(TenantId);
            return t;
        });
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void RealService_WithFullScenarioMockData_RendersAllGreen()
    {
        // ----- 1. Stand up EF in-memory contexts ------------------------
        // Same shape PilotReadinessServiceTests uses — the in-memory
        // provider is fine for the readiness service because the only
        // load-bearing query is `Events.Where(...).FirstOrDefaultAsync`
        // and the in-memory provider supports that.
        using var tenancyCtx = BuildTenancyCtx();
        using var auditCtx = BuildAuditCtx();

        // ----- 2. Seed the audit events the scenario would produce ----
        // The integration scenario emits:
        //  - nickerp.inspection.scan_recorded   (gate.scanner.adapter)
        //  - inspection.scan.captured           (gate.edge.roundtrip)
        //  - nickerp.inspection.verdict_set     (gate.analyst.decisioned_real_case)
        //  - nickerp.inspection.submission_dispatched (informational)
        // Plus an OutboundSubmission row in Status=accepted (gate.external_system.roundtrip)
        // and the multi-tenant invariant probe runs three sub-checks.
        var caseId = Guid.NewGuid();
        var scanProofId = Guid.NewGuid();
        var edgeProofId = Guid.NewGuid();
        var verdictProofId = Guid.NewGuid();
        auditCtx.Events.AddRange(
            NewAuditRow(scanProofId, TenantId, "nickerp.inspection.scan_recorded", "Scan", "scan-1", Now.AddMinutes(-3)),
            NewAuditRow(edgeProofId, TenantId, "inspection.scan.captured", "ScanArtifact", "/edge/edge-01/abc.zip", Now.AddMinutes(-2)),
            NewAuditRow(verdictProofId, TenantId, "nickerp.inspection.verdict_set", "InspectionCase", caseId.ToString(), Now.AddMinutes(-1)));
        auditCtx.SaveChanges();

        // ----- 3. Wire up the real PilotReadinessService --------------
        var inspection = new MockInspectionProbeDataSource
        {
            HasReal = true,
            LatestRealCaseId = caseId,
            HasAcceptedSubmission = true,
        };
        var probe = new StubInvariantProbe(allPass: true);
        var realSvc = new PilotReadinessService(
            tenancyCtx, auditCtx, inspection, probe,
            new FakeClock(Now),
            NullLogger<PilotReadinessService>.Instance);

        // ----- 4. Render the page using the real service -------------
        _ctx.Services.AddSingleton<IPilotReadinessService>(realSvc);

        var cut = _ctx.RenderComponent<PilotReadiness>();

        // ----- 5. Assert all-green markup -----------------------------
        // The page renders a state pill per gate. With the full scenario
        // satisfied, every gate should display PASS.
        var passOccurrences = CountOccurrences(cut.Markup, "PASS");
        passOccurrences.Should().BeGreaterThanOrEqualTo(5,
            because: "all 5 gate cards plus the multi-tenant invariant sub-checks should render PASS pills; "
                     + $"actual markup contains {passOccurrences} PASS occurrence(s)");

        cut.Markup.Should().NotContain("NOT YET OBSERVED",
            because: "the all-green scenario fixture leaves no gate in NotYetObserved");
        cut.Markup.Should().NotContain("FAIL",
            because: "no gate should report Fail under the all-green scenario");

        // Friendly gate-name labels render for each gate.
        cut.Markup.Should().Contain("Scanner adapter wired");
        cut.Markup.Should().Contain("Edge round-trip");
        cut.Markup.Should().Contain("Analyst decisioned a real case");
        cut.Markup.Should().Contain("External system round-trip");
        cut.Markup.Should().Contain("Multi-tenant invariants");

        // Multi-tenant gate's three sub-checks render.
        cut.Markup.Should().Contain("rls_read_isolation");
        cut.Markup.Should().Contain("system_context_register");
        cut.Markup.Should().Contain("cross_tenant_export_gate");

        // Proof-event ids render as audit-log links for the gates that
        // have them (scanner + edge from the seeded audit rows; analyst
        // resolves verdictProofId via the EntityId=caseId match in the
        // production probe code).
        cut.Markup.Should().Contain(scanProofId.ToString());
        cut.Markup.Should().Contain(edgeProofId.ToString());
        cut.Markup.Should().Contain(verdictProofId.ToString());
        cut.Markup.Should().Contain("/audit-log?eventId=");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RealService_WithSyntheticOnlyData_AnalystGateStaysNotYetObserved()
    {
        using var tenancyCtx = BuildTenancyCtx();
        using var auditCtx = BuildAuditCtx();

        // Seed only a synthetic verdict — the audit event exists, but
        // HasDecisionedRealCase returns false because the only verdicted
        // case is IsSynthetic = true.
        auditCtx.Events.Add(
            NewAuditRow(Guid.NewGuid(), TenantId, "nickerp.inspection.verdict_set", "InspectionCase", Guid.NewGuid().ToString(), Now.AddMinutes(-1)));
        auditCtx.SaveChanges();

        var inspection = new MockInspectionProbeDataSource
        {
            HasReal = false,        // synthetic only
            HasAcceptedSubmission = false,
        };
        var probe = new StubInvariantProbe(allPass: true);
        var realSvc = new PilotReadinessService(
            tenancyCtx, auditCtx, inspection, probe,
            new FakeClock(Now),
            NullLogger<PilotReadinessService>.Instance);
        _ctx.Services.AddSingleton<IPilotReadinessService>(realSvc);

        var cut = _ctx.RenderComponent<PilotReadiness>();

        // The dashboard reflects the gate state: analyst-gate row says
        // NOT YET OBSERVED with the operator-guidance hint.
        cut.Markup.Should().Contain("NOT YET OBSERVED");
        cut.Markup.Should().Contain("decisioned a non-synthetic case",
            because: "the analyst gate's NotYetObserved note guides the operator to "
                     + "verdict at least one production (non-synthetic) case");
    }

    // ---- helpers ----

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
        int count = 0;
        int i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    private static TenancyDbContext BuildTenancyCtx()
    {
        var name = "tenancy-bunit-" + Guid.NewGuid();
        var opts = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TenancyDbContext(opts);
    }

    private static AuditDbContext BuildAuditCtx()
    {
        var name = "audit-bunit-" + Guid.NewGuid();
        var opts = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestAuditDbContext(opts);
    }

    private static DomainEventRow NewAuditRow(
        Guid eventId, long tenantId, string eventType, string entityType, string entityId, DateTimeOffset occurredAt)
    {
        return new DomainEventRow
        {
            EventId = eventId,
            TenantId = tenantId,
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            Payload = JsonDocument.Parse("{}"),
            OccurredAt = occurredAt,
            IngestedAt = occurredAt,
            IdempotencyKey = "ipk-" + eventId,
        };
    }

    private sealed class MockInspectionProbeDataSource : IInspectionPilotProbeDataSource
    {
        public bool HasReal { get; set; }
        public Guid? LatestRealCaseId { get; set; }
        public bool HasAcceptedSubmission { get; set; }

        public Task<bool> HasDecisionedRealCaseAsync(long tenantId, CancellationToken ct = default)
            => Task.FromResult(HasReal);

        public Task<bool> HasSuccessfulOutboundSubmissionAsync(long tenantId, CancellationToken ct = default)
            => Task.FromResult(HasAcceptedSubmission);

        public Task<Guid?> LatestDecisionedRealCaseIdAsync(long tenantId, CancellationToken ct = default)
            => Task.FromResult(LatestRealCaseId);
    }

    private sealed class StubInvariantProbe : MultiTenantInvariantProbe
    {
        private readonly bool _allPass;

        public StubInvariantProbe(bool allPass)
            : base(BuildTenancyCtx(), TimeProvider.System, NullLogger<MultiTenantInvariantProbe>.Instance)
        {
            _allPass = allPass;
        }

        public override Task<MultiTenantInvariantProbeResult> RunAsync(long tenantId, CancellationToken ct = default)
        {
            var sub = new MultiTenantInvariantSubCheck(_allPass, _allPass ? "ok" : "stub-fail");
            return Task.FromResult(new MultiTenantInvariantProbeResult(
                OverallPass: _allPass,
                ObservedAt: Now,
                ProofEventId: Guid.NewGuid(),
                RlsReadIsolation: sub,
                SystemContextRegister: sub,
                CrossTenantExportGate: sub));
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeClock(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
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

    /// <summary>
    /// EF-in-memory-friendly variant of <see cref="AuditDbContext"/>:
    /// the production context maps <c>Payload</c> as <c>jsonb</c>; the
    /// in-memory provider needs an explicit ValueConverter to round-trip
    /// the <see cref="JsonDocument"/>. Mirrors the same shim
    /// <see cref="PilotReadinessServiceTests.TestAuditDbContext"/> uses.
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
