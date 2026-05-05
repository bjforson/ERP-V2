using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Database.Entities;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Database.Pilot;
using NickERP.Platform.Tenancy.Database.Services;
using NickERP.Platform.Tenancy.Pilot;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 43 Phase B — exercises <see cref="PilotReadinessService"/>
/// against the EF in-memory provider. Each test asserts the gate the
/// scenario targets transitions correctly + the snapshot row is
/// persisted.
/// </summary>
public sealed class PilotReadinessServiceTests
{
    private const long TenantId = 5;
    private static readonly DateTimeOffset Now = new(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FreshTenant_AllGatesNotYetObserved_AndProbeStubFlagsInvariantsFail()
    {
        await using var tenancyCtx = BuildTenancyCtx();
        await using var auditCtx = BuildAuditCtx();
        var inspection = new FakeInspectionProbeDataSource();
        var probe = new StubInvariantProbe(allPass: true);
        var svc = BuildSvc(tenancyCtx, auditCtx, inspection, probe);

        var report = await svc.GetReadinessAsync(TenantId);

        report.Gates.Should().HaveCount(5);
        report.Gates.Single(g => g.GateId == PilotReadinessGate.ScannerAdapter).State
            .Should().Be(PilotReadinessState.NotYetObserved);
        report.Gates.Single(g => g.GateId == PilotReadinessGate.EdgeRoundtrip).State
            .Should().Be(PilotReadinessState.NotYetObserved);
        report.Gates.Single(g => g.GateId == PilotReadinessGate.AnalystDecisionedRealCase).State
            .Should().Be(PilotReadinessState.NotYetObserved);
        report.Gates.Single(g => g.GateId == PilotReadinessGate.ExternalSystemRoundtrip).State
            .Should().Be(PilotReadinessState.NotYetObserved);
        // Stubbed probe says all pass, so multi-tenant gate is Pass.
        report.Gates.Single(g => g.GateId == PilotReadinessGate.MultiTenantInvariants).State
            .Should().Be(PilotReadinessState.Pass);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ScanRecordedEvent_FlipsScannerAdapterGateToPass()
    {
        await using var tenancyCtx = BuildTenancyCtx();
        await using var auditCtx = BuildAuditCtx();
        var proofId = Guid.NewGuid();
        auditCtx.Events.Add(NewEvent(proofId, TenantId, "nickerp.inspection.scan_recorded", "Scan", "scan-1", Now.AddMinutes(-1)));
        await auditCtx.SaveChangesAsync();
        var svc = BuildSvc(tenancyCtx, auditCtx, new FakeInspectionProbeDataSource(), new StubInvariantProbe(true));

        var report = await svc.GetReadinessAsync(TenantId);

        var gate = report.Gates.Single(g => g.GateId == PilotReadinessGate.ScannerAdapter);
        gate.State.Should().Be(PilotReadinessState.Pass);
        gate.ProofEventId.Should().Be(proofId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ScanRecordedEventForDifferentTenant_DoesNotFlipGate()
    {
        await using var tenancyCtx = BuildTenancyCtx();
        await using var auditCtx = BuildAuditCtx();
        // Note: TenantId 99, not 5.
        auditCtx.Events.Add(NewEvent(Guid.NewGuid(), tenantId: 99, "nickerp.inspection.scan_recorded", "Scan", "scan-1", Now.AddMinutes(-1)));
        await auditCtx.SaveChangesAsync();
        var svc = BuildSvc(tenancyCtx, auditCtx, new FakeInspectionProbeDataSource(), new StubInvariantProbe(true));

        var report = await svc.GetReadinessAsync(TenantId);

        report.Gates.Single(g => g.GateId == PilotReadinessGate.ScannerAdapter).State
            .Should().Be(PilotReadinessState.NotYetObserved);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EdgeReplayEvent_FlipsEdgeRoundtripGateToPass()
    {
        await using var tenancyCtx = BuildTenancyCtx();
        await using var auditCtx = BuildAuditCtx();
        var proofId = Guid.NewGuid();
        auditCtx.Events.Add(NewEvent(proofId, TenantId, "inspection.scan.captured", "Scan", "scan-1", Now.AddMinutes(-1)));
        await auditCtx.SaveChangesAsync();
        var svc = BuildSvc(tenancyCtx, auditCtx, new FakeInspectionProbeDataSource(), new StubInvariantProbe(true));

        var report = await svc.GetReadinessAsync(TenantId);

        var gate = report.Gates.Single(g => g.GateId == PilotReadinessGate.EdgeRoundtrip);
        gate.State.Should().Be(PilotReadinessState.Pass);
        gate.ProofEventId.Should().Be(proofId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DecisionedRealCase_FlipsAnalystGateToPass_WithProofEvent()
    {
        await using var tenancyCtx = BuildTenancyCtx();
        await using var auditCtx = BuildAuditCtx();
        var caseId = Guid.NewGuid();
        var proofId = Guid.NewGuid();
        auditCtx.Events.Add(NewEvent(proofId, TenantId, "nickerp.inspection.verdict_set", "InspectionCase", caseId.ToString(), Now.AddMinutes(-1)));
        await auditCtx.SaveChangesAsync();
        var inspection = new FakeInspectionProbeDataSource
        {
            HasReal = true,
            LatestRealCaseId = caseId,
        };
        var svc = BuildSvc(tenancyCtx, auditCtx, inspection, new StubInvariantProbe(true));

        var report = await svc.GetReadinessAsync(TenantId);

        var gate = report.Gates.Single(g => g.GateId == PilotReadinessGate.AnalystDecisionedRealCase);
        gate.State.Should().Be(PilotReadinessState.Pass);
        gate.ProofEventId.Should().Be(proofId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DecisionedSyntheticCase_LeavesAnalystGateNotYetObserved()
    {
        await using var tenancyCtx = BuildTenancyCtx();
        await using var auditCtx = BuildAuditCtx();
        // The verdict_set audit event exists but the case is synthetic
        // (FakeInspectionProbeDataSource.HasReal = false simulates that
        // the only verdicted case is IsSynthetic = true).
        auditCtx.Events.Add(NewEvent(Guid.NewGuid(), TenantId, "nickerp.inspection.verdict_set", "InspectionCase", Guid.NewGuid().ToString(), Now.AddMinutes(-1)));
        await auditCtx.SaveChangesAsync();
        var svc = BuildSvc(tenancyCtx, auditCtx, new FakeInspectionProbeDataSource { HasReal = false }, new StubInvariantProbe(true));

        var report = await svc.GetReadinessAsync(TenantId);

        report.Gates.Single(g => g.GateId == PilotReadinessGate.AnalystDecisionedRealCase).State
            .Should().Be(PilotReadinessState.NotYetObserved);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SuccessfulOutboundSubmission_FlipsExternalSystemGateToPass()
    {
        await using var tenancyCtx = BuildTenancyCtx();
        await using var auditCtx = BuildAuditCtx();
        var inspection = new FakeInspectionProbeDataSource { HasAcceptedSubmission = true };
        var svc = BuildSvc(tenancyCtx, auditCtx, inspection, new StubInvariantProbe(true));

        var report = await svc.GetReadinessAsync(TenantId);

        report.Gates.Single(g => g.GateId == PilotReadinessGate.ExternalSystemRoundtrip).State
            .Should().Be(PilotReadinessState.Pass);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InvariantProbeFailure_BubblesIntoGateNote()
    {
        await using var tenancyCtx = BuildTenancyCtx();
        await using var auditCtx = BuildAuditCtx();
        var probe = new StubInvariantProbe(allPass: false);
        var svc = BuildSvc(tenancyCtx, auditCtx, new FakeInspectionProbeDataSource(), probe);

        var report = await svc.GetReadinessAsync(TenantId);

        var gate = report.Gates.Single(g => g.GateId == PilotReadinessGate.MultiTenantInvariants);
        gate.State.Should().Be(PilotReadinessState.Fail);
        gate.Note.Should().Contain("rls_read_isolation:fail");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InspectionDataSourceThrows_GateRecordedAsFail_NotThrown()
    {
        await using var tenancyCtx = BuildTenancyCtx();
        await using var auditCtx = BuildAuditCtx();
        var inspection = new FakeInspectionProbeDataSource { ThrowOnHasReal = true };
        var svc = BuildSvc(tenancyCtx, auditCtx, inspection, new StubInvariantProbe(true));

        var report = await svc.GetReadinessAsync(TenantId);

        var gate = report.Gates.Single(g => g.GateId == PilotReadinessGate.AnalystDecisionedRealCase);
        gate.State.Should().Be(PilotReadinessState.Fail);
        gate.Note.Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetReadiness_PersistsOneSnapshotRowPerGate()
    {
        await using var tenancyCtx = BuildTenancyCtx();
        await using var auditCtx = BuildAuditCtx();
        var svc = BuildSvc(tenancyCtx, auditCtx, new FakeInspectionProbeDataSource(), new StubInvariantProbe(true));

        await svc.GetReadinessAsync(TenantId);

        var rows = await tenancyCtx.PilotReadinessSnapshots.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(5);
        rows.Select(r => r.GateId).Should().BeEquivalentTo(PilotReadinessGate.All);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BackToBackRefresh_AppendsNotUpdates()
    {
        await using var tenancyCtx = BuildTenancyCtx();
        await using var auditCtx = BuildAuditCtx();
        var svc = BuildSvc(tenancyCtx, auditCtx, new FakeInspectionProbeDataSource(), new StubInvariantProbe(true));

        await svc.GetReadinessAsync(TenantId);
        await svc.GetReadinessAsync(TenantId);

        var rows = await tenancyCtx.PilotReadinessSnapshots.AsNoTracking().ToListAsync();
        // 5 gates × 2 refreshes = 10 rows.
        rows.Should().HaveCount(10);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NonPositiveTenantId_Throws()
    {
        await using var tenancyCtx = BuildTenancyCtx();
        await using var auditCtx = BuildAuditCtx();
        var svc = BuildSvc(tenancyCtx, auditCtx, new FakeInspectionProbeDataSource(), new StubInvariantProbe(true));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.GetReadinessAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.GetReadinessAsync(-1));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Report_GatesPreservesStableOrder()
    {
        await using var tenancyCtx = BuildTenancyCtx();
        await using var auditCtx = BuildAuditCtx();
        var svc = BuildSvc(tenancyCtx, auditCtx, new FakeInspectionProbeDataSource(), new StubInvariantProbe(true));

        var report = await svc.GetReadinessAsync(TenantId);

        report.Gates.Select(g => g.GateId).Should().BeEquivalentTo(PilotReadinessGate.All, opt => opt.WithStrictOrdering());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SnapshotRow_PreservesGateState()
    {
        await using var tenancyCtx = BuildTenancyCtx();
        await using var auditCtx = BuildAuditCtx();
        auditCtx.Events.Add(NewEvent(Guid.NewGuid(), TenantId, "nickerp.inspection.scan_recorded", "Scan", "s1", Now.AddMinutes(-5)));
        await auditCtx.SaveChangesAsync();
        var svc = BuildSvc(tenancyCtx, auditCtx, new FakeInspectionProbeDataSource(), new StubInvariantProbe(true));

        var report = await svc.GetReadinessAsync(TenantId);

        var rows = await tenancyCtx.PilotReadinessSnapshots.AsNoTracking().ToListAsync();
        var passRow = rows.Single(r => r.GateId == PilotReadinessGate.ScannerAdapter);
        passRow.State.Should().Be(PilotReadinessState.Pass);
        var notObservedRow = rows.Single(r => r.GateId == PilotReadinessGate.EdgeRoundtrip);
        notObservedRow.State.Should().Be(PilotReadinessState.NotYetObserved);
        notObservedRow.Note.Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LatestSnapshotPerGateQuery_ReflectsTransition()
    {
        await using var tenancyCtx = BuildTenancyCtx();
        await using var auditCtx = BuildAuditCtx();
        var inspection = new FakeInspectionProbeDataSource();
        // Two refreshes with explicitly distinct clocks so the snapshot
        // rows carry distinct ObservedAt values regardless of in-memory
        // provider ordering tie-break behaviour.
        var clockA = new FakeClock(Now);
        var clockB = new FakeClock(Now.AddSeconds(5));

        var svcA = new PilotReadinessService(
            tenancyCtx, auditCtx, inspection, new StubInvariantProbe(true),
            clockA, NullLogger<PilotReadinessService>.Instance);

        // First refresh — no scan yet, NotYetObserved.
        await svcA.GetReadinessAsync(TenantId);

        // Now seed a scan event and refresh again under a later clock so
        // ObservedAt is unambiguously later for the second snapshot row.
        auditCtx.Events.Add(NewEvent(Guid.NewGuid(), TenantId, "nickerp.inspection.scan_recorded", "Scan", "s1", Now));
        await auditCtx.SaveChangesAsync();
        var svcB = new PilotReadinessService(
            tenancyCtx, auditCtx, inspection, new StubInvariantProbe(true),
            clockB, NullLogger<PilotReadinessService>.Instance);
        await svcB.GetReadinessAsync(TenantId);

        var rows = await tenancyCtx.PilotReadinessSnapshots.AsNoTracking()
            .Where(r => r.TenantId == TenantId && r.GateId == PilotReadinessGate.ScannerAdapter)
            .ToListAsync();
        rows.Should().HaveCount(2);
        var latest = rows.OrderByDescending(r => r.ObservedAt).First();
        latest.State.Should().Be(PilotReadinessState.Pass);
        var first = rows.OrderBy(r => r.ObservedAt).First();
        first.State.Should().Be(PilotReadinessState.NotYetObserved);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AllGatesPass_OverallPassConfirmed()
    {
        await using var tenancyCtx = BuildTenancyCtx();
        await using var auditCtx = BuildAuditCtx();
        var caseId = Guid.NewGuid();
        auditCtx.Events.AddRange(
            NewEvent(Guid.NewGuid(), TenantId, "nickerp.inspection.scan_recorded", "Scan", "s1", Now.AddMinutes(-3)),
            NewEvent(Guid.NewGuid(), TenantId, "inspection.scan.captured", "Scan", "s1", Now.AddMinutes(-2)),
            NewEvent(Guid.NewGuid(), TenantId, "nickerp.inspection.verdict_set", "InspectionCase", caseId.ToString(), Now.AddMinutes(-1)));
        await auditCtx.SaveChangesAsync();
        var inspection = new FakeInspectionProbeDataSource
        {
            HasReal = true,
            LatestRealCaseId = caseId,
            HasAcceptedSubmission = true,
        };
        var svc = BuildSvc(tenancyCtx, auditCtx, inspection, new StubInvariantProbe(true));

        var report = await svc.GetReadinessAsync(TenantId);

        report.Gates.All(g => g.State == PilotReadinessState.Pass).Should().BeTrue();
    }

    // ---- helpers ----

    private static PilotReadinessService BuildSvc(
        TenancyDbContext tenancyCtx,
        AuditDbContext auditCtx,
        FakeInspectionProbeDataSource inspection,
        StubInvariantProbe probe)
    {
        return new PilotReadinessService(
            tenancyCtx, auditCtx, inspection, probe,
            new FakeClock(Now),
            NullLogger<PilotReadinessService>.Instance);
    }

    private static TenancyDbContext BuildTenancyCtx()
    {
        var name = "tenancy-" + Guid.NewGuid();
        var opts = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TenancyDbContext(opts);
    }

    private static AuditDbContext BuildAuditCtx()
    {
        var name = "audit-" + Guid.NewGuid();
        var opts = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestAuditDbContext(opts);
    }

    private static DomainEventRow NewEvent(
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

    private sealed class FakeClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeClock(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// Advances by <c>step</c> on every <see cref="GetUtcNow"/> call so
    /// back-to-back refreshes produce strictly increasing ObservedAt
    /// values. Used by the LatestSnapshotPerGateQuery test where
    /// ordering must be deterministic.
    /// </summary>
    private sealed class SteppedClock : TimeProvider
    {
        private DateTimeOffset _now;
        private readonly TimeSpan _step;
        public SteppedClock(DateTimeOffset start, TimeSpan step) { _now = start; _step = step; }
        public override DateTimeOffset GetUtcNow()
        {
            var v = _now;
            _now = _now.Add(_step);
            return v;
        }
    }

    private sealed class FakeInspectionProbeDataSource : IInspectionPilotProbeDataSource
    {
        public bool HasReal { get; set; }
        public Guid? LatestRealCaseId { get; set; }
        public bool HasAcceptedSubmission { get; set; }
        public bool ThrowOnHasReal { get; set; }

        public Task<bool> HasDecisionedRealCaseAsync(long tenantId, CancellationToken ct = default)
        {
            if (ThrowOnHasReal) throw new InvalidOperationException("synthetic test failure");
            return Task.FromResult(HasReal);
        }

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
                ProofEventId: null,
                RlsReadIsolation: sub,
                SystemContextRegister: sub,
                CrossTenantExportGate: sub));
        }
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
