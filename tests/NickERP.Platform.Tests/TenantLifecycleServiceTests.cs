using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Database.Services;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 18 — exercises <see cref="TenantLifecycleService"/> state
/// transitions against the EF in-memory provider. RLS is not in scope
/// for these tests; the surface under test is the state machine plus
/// audit-event emission. Cross-DB orchestration is exercised via
/// <see cref="StubTenantPurgeOrchestrator"/> (the live orchestrator
/// needs Postgres and is covered by integration tests once the migration
/// is applied).
/// </summary>
public sealed class TenantLifecycleServiceTests
{
    private readonly DateTimeOffset _now = new(2026, 5, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SuspendTenant_FlipsToSuspended_AndEmitsAudit()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        var actor = Guid.NewGuid();
        var tenantId = await SeedTenantAsync(ctx, TenantState.Active);

        var svc = BuildService(ctx, publisher, orchestrator);
        await svc.SuspendTenantAsync(tenantId, "missed-payment", actor);

        var t = await ctx.Tenants.IgnoreQueryFilters().FirstAsync(x => x.Id == tenantId);
        t.State.Should().Be(TenantState.Suspended);
        publisher.Events.Should().ContainSingle(e =>
            e.EventType == "nickerp.tenancy.tenant_suspended"
            && e.ActorUserId == actor);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SuspendTenant_FromSuspended_IsIdempotent()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        var tenantId = await SeedTenantAsync(ctx, TenantState.Suspended);

        var svc = BuildService(ctx, publisher, orchestrator);
        await svc.SuspendTenantAsync(tenantId, null, Guid.NewGuid());

        publisher.Events.Should().BeEmpty("re-suspending a suspended tenant emits no audit event");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SuspendTenant_FromSoftDeleted_Throws()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        var tenantId = await SeedTenantAsync(ctx, TenantState.SoftDeleted, deletedAtOffset: TimeSpan.FromDays(-1));
        var svc = BuildService(ctx, publisher, orchestrator);

        var act = async () => await svc.SuspendTenantAsync(tenantId, null, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResumeTenant_FromSuspended_FlipsToActive_AndEmits()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        var actor = Guid.NewGuid();
        var tenantId = await SeedTenantAsync(ctx, TenantState.Suspended);
        var svc = BuildService(ctx, publisher, orchestrator);

        await svc.ResumeTenantAsync(tenantId, actor);

        var t = await ctx.Tenants.IgnoreQueryFilters().FirstAsync(x => x.Id == tenantId);
        t.State.Should().Be(TenantState.Active);
        publisher.Events.Should().ContainSingle(e =>
            e.EventType == "nickerp.tenancy.tenant_resumed");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResumeTenant_FromSoftDeleted_Throws()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        var tenantId = await SeedTenantAsync(ctx, TenantState.SoftDeleted, deletedAtOffset: TimeSpan.FromDays(-1));
        var svc = BuildService(ctx, publisher, orchestrator);

        var act = async () => await svc.ResumeTenantAsync(tenantId, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SoftDeleteTenant_StampsAllFields_AndComputesPurgeAfter()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        var actor = Guid.NewGuid();
        var tenantId = await SeedTenantAsync(ctx, TenantState.Active);
        var svc = BuildService(ctx, publisher, orchestrator);

        await svc.SoftDeleteTenantAsync(tenantId, "customer churned", actor, retentionDays: 30);

        var t = await ctx.Tenants.IgnoreQueryFilters().FirstAsync(x => x.Id == tenantId);
        t.State.Should().Be(TenantState.SoftDeleted);
        t.DeletedAt.Should().Be(_now);
        t.DeletedByUserId.Should().Be(actor);
        t.DeletionReason.Should().Be("customer churned");
        t.RetentionDays.Should().Be(30);
        t.HardPurgeAfter.Should().Be(_now.AddDays(30));
        publisher.Events.Should().ContainSingle(e =>
            e.EventType == "nickerp.tenancy.tenant_soft_deleted");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SoftDeleteTenant_FromSuspended_IsAllowed()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        var tenantId = await SeedTenantAsync(ctx, TenantState.Suspended);
        var svc = BuildService(ctx, publisher, orchestrator);

        await svc.SoftDeleteTenantAsync(tenantId, "switching off", Guid.NewGuid());

        var t = await ctx.Tenants.IgnoreQueryFilters().FirstAsync(x => x.Id == tenantId);
        t.State.Should().Be(TenantState.SoftDeleted);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SoftDeleteTenant_FromSoftDeleted_Throws()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        var tenantId = await SeedTenantAsync(ctx, TenantState.SoftDeleted, deletedAtOffset: TimeSpan.FromDays(-1));
        var svc = BuildService(ctx, publisher, orchestrator);

        var act = async () => await svc.SoftDeleteTenantAsync(tenantId, "x", Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SoftDeleteTenant_NegativeRetention_ThrowsArgument()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        var tenantId = await SeedTenantAsync(ctx, TenantState.Active);
        var svc = BuildService(ctx, publisher, orchestrator);

        var act = async () => await svc.SoftDeleteTenantAsync(tenantId, "x", Guid.NewGuid(), retentionDays: -1);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestoreTenant_WithinWindow_FlipsToActive_AndClearsFields()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        // Soft-delete-style row: HardPurgeAfter is in the future relative to _now
        var tenantId = await SeedTenantAsync(ctx, TenantState.SoftDeleted,
            deletedAtOffset: TimeSpan.FromDays(-10), hardPurgeAfter: _now.AddDays(80));
        var svc = BuildService(ctx, publisher, orchestrator);

        await svc.RestoreTenantAsync(tenantId, Guid.NewGuid());

        var t = await ctx.Tenants.IgnoreQueryFilters().FirstAsync(x => x.Id == tenantId);
        t.State.Should().Be(TenantState.Active);
        t.DeletedAt.Should().BeNull();
        t.DeletedByUserId.Should().BeNull();
        t.DeletionReason.Should().BeNull();
        t.HardPurgeAfter.Should().BeNull();
        // RetentionDays is intentionally preserved.
        t.RetentionDays.Should().Be(90);
        publisher.Events.Should().ContainSingle(e =>
            e.EventType == "nickerp.tenancy.tenant_restored");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestoreTenant_AfterRetentionExpired_Throws()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        var tenantId = await SeedTenantAsync(ctx, TenantState.SoftDeleted,
            deletedAtOffset: TimeSpan.FromDays(-100),
            hardPurgeAfter: _now.AddDays(-10)); // expired
        var svc = BuildService(ctx, publisher, orchestrator);

        var act = async () => await svc.RestoreTenantAsync(tenantId, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestoreTenant_FromActive_Throws()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        var tenantId = await SeedTenantAsync(ctx, TenantState.Active);
        var svc = BuildService(ctx, publisher, orchestrator);

        var act = async () => await svc.RestoreTenantAsync(tenantId, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MarkPendingHardPurge_BeforeWindowExpires_ReturnsFalse_NoStateFlip()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        var tenantId = await SeedTenantAsync(ctx, TenantState.SoftDeleted,
            deletedAtOffset: TimeSpan.FromDays(-1),
            hardPurgeAfter: _now.AddDays(89));
        var svc = BuildService(ctx, publisher, orchestrator);

        var flipped = await svc.MarkPendingHardPurgeAsync(tenantId);

        flipped.Should().BeFalse();
        var t = await ctx.Tenants.IgnoreQueryFilters().FirstAsync(x => x.Id == tenantId);
        t.State.Should().Be(TenantState.SoftDeleted);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MarkPendingHardPurge_AfterWindowExpires_FlipsAndAudits()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        var tenantId = await SeedTenantAsync(ctx, TenantState.SoftDeleted,
            deletedAtOffset: TimeSpan.FromDays(-100),
            hardPurgeAfter: _now.AddDays(-1));
        var svc = BuildService(ctx, publisher, orchestrator);

        var flipped = await svc.MarkPendingHardPurgeAsync(tenantId);

        flipped.Should().BeTrue();
        var t = await ctx.Tenants.IgnoreQueryFilters().FirstAsync(x => x.Id == tenantId);
        t.State.Should().Be(TenantState.PendingHardPurge);
        publisher.Events.Should().ContainSingle(e =>
            e.EventType == "nickerp.tenancy.tenant_pending_hard_purge");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HardPurge_FromPendingHardPurge_DelegatesToOrchestrator()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        var tenantId = await SeedTenantAsync(ctx, TenantState.PendingHardPurge,
            deletedAtOffset: TimeSpan.FromDays(-100),
            hardPurgeAfter: _now.AddDays(-1));
        var svc = BuildService(ctx, publisher, orchestrator);
        var actor = Guid.NewGuid();

        var result = await svc.HardPurgeTenantAsync(tenantId, actor);

        result.Outcome.Should().Be("completed");
        orchestrator.Calls.Should().ContainSingle(c =>
            c.TenantId == tenantId && c.ConfirmingUserId == actor);
        publisher.Events.Should().ContainSingle(e =>
            e.EventType == "nickerp.tenancy.tenant_hard_purged"
            && e.TenantId == null /* system-tenant audit row */);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HardPurge_FromActive_Throws()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        var tenantId = await SeedTenantAsync(ctx, TenantState.Active);
        var svc = BuildService(ctx, publisher, orchestrator);

        var act = async () => await svc.HardPurgeTenantAsync(tenantId, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
        orchestrator.Calls.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GlobalQueryFilter_HidesSoftDeleted_FromDefaultQueries()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        await SeedTenantAsync(ctx, TenantState.Active);
        await SeedTenantAsync(ctx, TenantState.SoftDeleted, deletedAtOffset: TimeSpan.FromDays(-1));
        await SeedTenantAsync(ctx, TenantState.PendingHardPurge,
            deletedAtOffset: TimeSpan.FromDays(-100), hardPurgeAfter: _now.AddDays(-1));
        await SeedTenantAsync(ctx, TenantState.Suspended);

        // Default query — global filter excludes SoftDeleted + PendingHardPurge
        var defaultList = await ctx.Tenants.AsNoTracking().ToListAsync();
        defaultList.Select(t => t.State).Should()
            .OnlyContain(s => s == TenantState.Active || s == TenantState.Suspended);

        // IgnoreQueryFilters — admin path sees everything
        var allList = await ctx.Tenants.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        allList.Select(t => t.State).Should().Contain(new[]
        {
            TenantState.Active, TenantState.Suspended,
            TenantState.SoftDeleted, TenantState.PendingHardPurge
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TenantState_IsActive_ComputedProperty_AlignsWithState()
    {
        var active = new Tenant { State = TenantState.Active };
        var suspended = new Tenant { State = TenantState.Suspended };
        var deleted = new Tenant { State = TenantState.SoftDeleted };
        var pending = new Tenant { State = TenantState.PendingHardPurge };

        active.IsActive.Should().BeTrue();
        suspended.IsActive.Should().BeFalse();
        deleted.IsActive.Should().BeFalse();
        pending.IsActive.Should().BeFalse();

        await Task.CompletedTask;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadTenant_WithUnknownId_Throws()
    {
        await using var ctx = BuildCtx(out var publisher, out var orchestrator);
        var svc = BuildService(ctx, publisher, orchestrator);

        var act = async () => await svc.SuspendTenantAsync(99999L, null, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // -----------------------------------------------------------------
    // Test scaffolding
    // -----------------------------------------------------------------

    private TenancyDbContext BuildCtx(out RecordingPublisher publisher, out StubTenantPurgeOrchestrator orchestrator)
    {
        var dbName = "tenant-lifecycle-" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        publisher = new RecordingPublisher();
        orchestrator = new StubTenantPurgeOrchestrator();
        return new TenancyDbContext(options);
    }

    private TenantLifecycleService BuildService(
        TenancyDbContext ctx,
        IEventPublisher publisher,
        ITenantPurgeOrchestrator orchestrator)
    {
        var clock = new FakeClock(_now);
        return new TenantLifecycleService(
            ctx, publisher, orchestrator,
            NullLogger<TenantLifecycleService>.Instance, clock);
    }

    private async Task<long> SeedTenantAsync(
        TenancyDbContext ctx, TenantState state,
        TimeSpan? deletedAtOffset = null,
        DateTimeOffset? hardPurgeAfter = null)
    {
        var t = new Tenant
        {
            Code = "seed-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Seed Tenant",
            BillingPlan = "internal",
            TimeZone = "UTC",
            Locale = "en",
            Currency = "USD",
            State = state,
            CreatedAt = _now.AddDays(-30),
            RetentionDays = 90,
        };
        if (deletedAtOffset is not null)
        {
            t.DeletedAt = _now + deletedAtOffset.Value;
            t.DeletionReason = "seeded";
            t.HardPurgeAfter = hardPurgeAfter ?? (_now + deletedAtOffset.Value).AddDays(90);
        }
        ctx.Tenants.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    /// <summary>
    /// Captures every event published so tests can assert audit emission
    /// without booting a real Postgres instance.
    /// </summary>
    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<DomainEvent> Events { get; } = new();

        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return Task.FromResult(evt with { EventId = Guid.NewGuid() });
        }

        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
        {
            Events.AddRange(events);
            return Task.FromResult<IReadOnlyList<DomainEvent>>(events);
        }
    }

    /// <summary>
    /// Stubs the cross-DB orchestrator. Records every call and returns a
    /// "completed" result without touching any database.
    /// </summary>
    private sealed class StubTenantPurgeOrchestrator : ITenantPurgeOrchestrator
    {
        public List<TenantPurgeContext> Calls { get; } = new();

        public Task<TenantPurgeResult> PurgeAsync(TenantPurgeContext context, CancellationToken ct = default)
        {
            Calls.Add(context);
            var rowCounts = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                { "stub.table", 0 }
            };
            return Task.FromResult(new TenantPurgeResult(
                PurgeLogId: Guid.NewGuid(),
                Outcome: "completed",
                RowCounts: rowCounts,
                FailureNote: null));
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeClock(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
