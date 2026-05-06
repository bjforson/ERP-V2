using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;
using NickERP.Portal.Services;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 56 / FU-cross-tenant-aggregation — Phase D coverage for the
/// portal-side <see cref="CrossTenantAuditService"/>.
///
/// <para>
/// Asserts the per-tenant fan-out shape: tenant discovery via
/// <see cref="TenancyDbContext.Tenants"/>, per-tenant scope flip through
/// <see cref="ITenantContext.SetTenant"/>, partition-pruning
/// <c>OccurredAt</c> filter on every audit-events query, 60 s cache TTL
/// behaviour, and that <i>no</i> code path calls
/// <see cref="ITenantContext.SetSystemContext"/> (defended by an
/// observable behaviour test rather than a grep — the test scope ends
/// with <see cref="ITenantContext.IsSystem"/> = false).
/// </para>
///
/// <para>
/// EF in-memory provider (same shape as
/// <see cref="AuditNotificationProjectorTests"/>) keeps the assertions
/// deterministic without a Postgres dependency. RLS enforcement is
/// covered by <see cref="SystemContextTests"/> against real Postgres.
/// </para>
/// </summary>
public sealed class CrossTenantAuditServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly FakeClock _clock = new();

    public CrossTenantAuditServiceTests()
    {
        var dbName = "s56-cross-tenant-audit-" + Guid.NewGuid();
        var services = new ServiceCollection();

        services.AddDbContext<AuditDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<AuditDbContext>(sp =>
            new TestAuditDbContext(sp.GetRequiredService<DbContextOptions<AuditDbContext>>()));

        services.AddDbContext<TenancyDbContext>(o =>
            o.UseInMemoryDatabase("tenancy-" + dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        services.AddScoped<ITenantContext, TenantContext>();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.Configure<CrossTenantAuditOptions>(o =>
        {
            o.CacheTtl = TimeSpan.FromSeconds(60);
            o.MaxWindow = TimeSpan.FromDays(30);
            o.DefaultWindow = TimeSpan.FromHours(24);
            o.TopErrorsTake = 5;
        });
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<CrossTenantAuditCache>(sp =>
            new CrossTenantAuditCache(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ICrossTenantAuditService>(sp => new CrossTenantAuditService(
            sp,
            sp.GetRequiredService<IOptions<CrossTenantAuditOptions>>(),
            NullLogger<CrossTenantAuditService>.Instance,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<CrossTenantAuditCache>()));

        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Summary_with_no_active_tenants_returns_empty()
    {
        var svc = _sp.GetRequiredService<ICrossTenantAuditService>();
        var rows = await svc.GetCrossTenantSummaryAsync(TimeSpan.FromHours(24));
        rows.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Summary_fan_out_covers_every_active_tenant()
    {
        await SeedTenantAsync(1, "tenant-a", TenantState.Active);
        await SeedTenantAsync(2, "tenant-b", TenantState.Active);
        await SeedTenantAsync(3, "tenant-c", TenantState.Active);

        var svc = _sp.GetRequiredService<ICrossTenantAuditService>();
        var rows = await svc.GetCrossTenantSummaryAsync(TimeSpan.FromHours(24));

        rows.Should().HaveCount(3);
        rows.Select(r => r.TenantCode).Should().BeEquivalentTo(new[] { "tenant-a", "tenant-b", "tenant-c" });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Summary_excludes_soft_deleted_and_suspended_tenants()
    {
        await SeedTenantAsync(1, "active-1", TenantState.Active);
        await SeedTenantAsync(2, "suspended", TenantState.Suspended);
        await SeedTenantAsync(3, "soft-deleted", TenantState.SoftDeleted);
        await SeedTenantAsync(4, "pending-purge", TenantState.PendingHardPurge);

        var svc = _sp.GetRequiredService<ICrossTenantAuditService>();
        var rows = await svc.GetCrossTenantSummaryAsync(TimeSpan.FromHours(24));

        rows.Should().ContainSingle(r => r.TenantCode == "active-1");
        rows.Should().NotContain(r => r.TenantCode == "suspended");
        rows.Should().NotContain(r => r.TenantCode == "soft-deleted");
        rows.Should().NotContain(r => r.TenantCode == "pending-purge");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Summary_counts_events_and_errors_per_tenant_within_window()
    {
        await SeedTenantAsync(1, "t1", TenantState.Active);
        await SeedTenantAsync(2, "t2", TenantState.Active);

        var now = _clock.GetUtcNow();
        // Tenant 1: 3 events, 1 error.
        await SeedAuditEventAsync(1, "nickerp.inspection.case_opened", now.AddMinutes(-30));
        await SeedAuditEventAsync(1, "nickerp.inspection.case_decided", now.AddMinutes(-20));
        await SeedAuditEventAsync(1, "nickerp.inspection.fetch.error", now.AddMinutes(-10));
        // Tenant 2: 1 event, 0 errors.
        await SeedAuditEventAsync(2, "nickerp.tenancy.tenant_created", now.AddMinutes(-5));

        var svc = _sp.GetRequiredService<ICrossTenantAuditService>();
        var rows = await svc.GetCrossTenantSummaryAsync(TimeSpan.FromHours(1));

        var t1 = rows.Single(r => r.TenantId == 1);
        t1.EventCount.Should().Be(3);
        t1.ErrorCount.Should().Be(1);
        t1.LastEventAt.Should().NotBeNull();

        var t2 = rows.Single(r => r.TenantId == 2);
        t2.EventCount.Should().Be(1);
        t2.ErrorCount.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Summary_partition_prune_filter_excludes_events_outside_window()
    {
        await SeedTenantAsync(1, "t1", TenantState.Active);
        var now = _clock.GetUtcNow();
        // Inside the 1h window.
        await SeedAuditEventAsync(1, "test.in_window", now.AddMinutes(-10));
        // Outside the 1h window — service must filter on OccurredAt so
        // partition pruning is observable.
        await SeedAuditEventAsync(1, "test.out_of_window", now.AddDays(-2));

        var svc = _sp.GetRequiredService<ICrossTenantAuditService>();
        var rows = await svc.GetCrossTenantSummaryAsync(TimeSpan.FromHours(1));
        rows.Single().EventCount.Should().Be(1, "the out-of-window event must be filtered before counting");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Cache_returns_same_instance_on_second_call_inside_TTL()
    {
        await SeedTenantAsync(1, "t1", TenantState.Active);
        await SeedAuditEventAsync(1, "evt.in", _clock.GetUtcNow().AddMinutes(-5));

        var svc = _sp.GetRequiredService<ICrossTenantAuditService>();
        var first = await svc.GetCrossTenantSummaryAsync(TimeSpan.FromHours(1));

        // Add another event AFTER the first call. Inside the TTL the
        // cached result hides the new event from the second call.
        await SeedAuditEventAsync(1, "evt.in_after_cache", _clock.GetUtcNow().AddMinutes(-2));
        var second = await svc.GetCrossTenantSummaryAsync(TimeSpan.FromHours(1));
        second.Single().EventCount.Should().Be(first.Single().EventCount,
            "cache must hide late-arriving events while inside the TTL window");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Cache_expires_after_TTL_and_re_fans_out()
    {
        await SeedTenantAsync(1, "t1", TenantState.Active);
        await SeedAuditEventAsync(1, "evt.first", _clock.GetUtcNow().AddMinutes(-5));

        var svc = _sp.GetRequiredService<ICrossTenantAuditService>();
        var first = await svc.GetCrossTenantSummaryAsync(TimeSpan.FromHours(1));
        first.Single().EventCount.Should().Be(1);

        // Add another event, then advance the fake clock past the 60s TTL.
        await SeedAuditEventAsync(1, "evt.second", _clock.GetUtcNow().AddMinutes(-1));
        _clock.Advance(TimeSpan.FromSeconds(61));

        var second = await svc.GetCrossTenantSummaryAsync(TimeSpan.FromHours(1));
        second.Single().EventCount.Should().Be(2,
            "after the cache TTL elapses the service must re-fan-out and observe the new event");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Cache_clear_forces_re_fan_out()
    {
        await SeedTenantAsync(1, "t1", TenantState.Active);
        await SeedAuditEventAsync(1, "evt.first", _clock.GetUtcNow().AddMinutes(-5));

        var svc = _sp.GetRequiredService<ICrossTenantAuditService>();
        await svc.GetCrossTenantSummaryAsync(TimeSpan.FromHours(1));

        await SeedAuditEventAsync(1, "evt.second", _clock.GetUtcNow().AddMinutes(-1));
        _sp.GetRequiredService<CrossTenantAuditCache>().Clear();

        var refreshed = await svc.GetCrossTenantSummaryAsync(TimeSpan.FromHours(1));
        refreshed.Single().EventCount.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TopErrors_orders_by_error_rate_then_count()
    {
        await SeedTenantAsync(1, "noisy", TenantState.Active);
        await SeedTenantAsync(2, "tiny-error", TenantState.Active);
        await SeedTenantAsync(3, "clean", TenantState.Active);
        await SeedTenantAsync(4, "idle", TenantState.Active); // zero events — must be excluded

        var now = _clock.GetUtcNow();
        // tenant 1 — noisy: 10 events, 1 error → 10% rate
        for (int i = 0; i < 9; i++)
            await SeedAuditEventAsync(1, "evt.normal", now.AddMinutes(-i));
        await SeedAuditEventAsync(1, "evt.fetch.error", now.AddMinutes(-10));

        // tenant 2 — tiny-error: 2 events, 1 error → 50% rate
        await SeedAuditEventAsync(2, "evt.normal", now.AddMinutes(-1));
        await SeedAuditEventAsync(2, "evt.fetch.error", now.AddMinutes(-2));

        // tenant 3 — clean: 3 events, 0 errors → 0% rate
        for (int i = 0; i < 3; i++)
            await SeedAuditEventAsync(3, "evt.normal", now.AddMinutes(-i));

        var svc = _sp.GetRequiredService<ICrossTenantAuditService>();
        var rows = await svc.GetCrossTenantTopErrorsAsync(TimeSpan.FromHours(1), take: 3);

        // Excludes idle (zero events). Includes the rest, ordered by rate.
        rows.Should().NotContain(r => r.TenantCode == "idle");
        rows.First().TenantCode.Should().Be("tiny-error", "50% > 10% > 0%");
        rows.Should().OnlyContain(r => r.EventCount > 0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TopErrors_take_clamps_to_request()
    {
        for (int i = 1; i <= 7; i++)
        {
            await SeedTenantAsync(i, "t" + i, TenantState.Active);
            await SeedAuditEventAsync(i, "evt.x.error", _clock.GetUtcNow().AddMinutes(-i));
        }

        var svc = _sp.GetRequiredService<ICrossTenantAuditService>();
        var rows = await svc.GetCrossTenantTopErrorsAsync(TimeSpan.FromHours(1), take: 3);
        rows.Should().HaveCount(3);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Webhook_health_returns_empty_when_no_inspection_dbcontext_registered()
    {
        // The default _sp fixture does NOT register InspectionDbContext —
        // mirroring a portal-only deployment without
        // ConnectionStrings:Inspection set. The service must gracefully
        // return empty rather than crash.
        await SeedTenantAsync(1, "t1", TenantState.Active);

        var svc = _sp.GetRequiredService<ICrossTenantAuditService>();
        var rows = await svc.GetCrossTenantWebhookHealthAsync();
        rows.Should().BeEmpty(
            "portal-only hosts without InspectionDbContext have no WebhookCursor table to read");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Tenant_activity_filters_by_event_type_substring()
    {
        await SeedTenantAsync(1, "t1", TenantState.Active);
        var now = _clock.GetUtcNow();
        await SeedAuditEventAsync(1, "nickerp.inspection.case_opened", now.AddMinutes(-1));
        await SeedAuditEventAsync(1, "nickerp.tenancy.tenant_created", now.AddMinutes(-2));

        var svc = _sp.GetRequiredService<ICrossTenantAuditService>();
        var activity = await svc.GetTenantAuditActivityAsync(
            tenantId: 1,
            window: TimeSpan.FromHours(1),
            take: 50,
            filter: new TenantAuditActivityFilter(EventTypeContains: "tenancy"));

        activity.Rows.Should().ContainSingle();
        activity.Rows.Single().EventType.Should().Contain("tenancy");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Tenant_activity_errors_only_filters_to_dot_error_events()
    {
        await SeedTenantAsync(1, "t1", TenantState.Active);
        var now = _clock.GetUtcNow();
        await SeedAuditEventAsync(1, "evt.normal", now.AddMinutes(-1));
        await SeedAuditEventAsync(1, "evt.fetch.error", now.AddMinutes(-2));

        var svc = _sp.GetRequiredService<ICrossTenantAuditService>();
        var activity = await svc.GetTenantAuditActivityAsync(
            tenantId: 1,
            window: TimeSpan.FromHours(1),
            take: 50,
            filter: new TenantAuditActivityFilter(ErrorsOnly: true));

        activity.Rows.Should().ContainSingle();
        activity.Rows.Single().IsError.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Service_does_not_call_SetSystemContext()
    {
        // Sprint 56 architectural constraint — the service uses per-tenant
        // fan-out, never SetSystemContext. We assert by constructing a
        // recording ITenantContext that fails the test if SetSystemContext
        // is called.
        var dbName = "s56-no-system-context-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<AuditDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<AuditDbContext>(sp =>
            new TestAuditDbContext(sp.GetRequiredService<DbContextOptions<AuditDbContext>>()));
        services.AddDbContext<TenancyDbContext>(o =>
            o.UseInMemoryDatabase("tenancy-" + dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<ITenantContext, RecordingTenantContext>();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.Configure<CrossTenantAuditOptions>(o => { });
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<CrossTenantAuditCache>();
        services.AddSingleton<ICrossTenantAuditService>(sp => new CrossTenantAuditService(
            sp,
            sp.GetRequiredService<IOptions<CrossTenantAuditOptions>>(),
            NullLogger<CrossTenantAuditService>.Instance,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<CrossTenantAuditCache>()));
        await using var sp = services.BuildServiceProvider();

        // Seed via the test sp (NOT the fixture's _sp) so the recording
        // context is what gets used.
        using (var scope = sp.CreateScope())
        {
            var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            tenancy.Tenants.Add(new Tenant
            {
                Id = 1,
                Code = "t1",
                Name = "T1",
                State = TenantState.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await tenancy.SaveChangesAsync();
        }
        using (var scope = sp.CreateScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            audit.Events.Add(new DomainEventRow
            {
                EventId = Guid.NewGuid(),
                TenantId = 1,
                EventType = "evt.test",
                EntityType = "X",
                EntityId = "1",
                Payload = JsonDocument.Parse("{}"),
                OccurredAt = _clock.GetUtcNow().AddMinutes(-1),
                IngestedAt = _clock.GetUtcNow().AddMinutes(-1),
                IdempotencyKey = "ipk-" + Guid.NewGuid()
            });
            await audit.SaveChangesAsync();
        }

        var svc = sp.GetRequiredService<ICrossTenantAuditService>();
        await svc.GetCrossTenantSummaryAsync(TimeSpan.FromHours(1));
        await svc.GetCrossTenantTopErrorsAsync(TimeSpan.FromHours(1), take: 5);
        await svc.GetTenantAuditActivityAsync(1, TimeSpan.FromHours(1), 50);

        // Aggregate the SetSystemContext call count across every scoped
        // RecordingTenantContext instance the service produced.
        RecordingTenantContext.GlobalSetSystemContextCount.Should().Be(0,
            "Sprint 56 must not introduce a new SetSystemContext caller — see "
            + "docs/system-context-audit-register.md and feedback_confirm_before_weakening_security");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Window_is_clamped_to_max_window_ceiling()
    {
        await SeedTenantAsync(1, "t1", TenantState.Active);
        var now = _clock.GetUtcNow();
        // Event 60 days ago — well outside the 30 d max window. If
        // clamping fails the service would surface this row.
        await SeedAuditEventAsync(1, "evt.ancient", now.AddDays(-60));
        // Event today — must be visible.
        await SeedAuditEventAsync(1, "evt.recent", now.AddMinutes(-30));

        var svc = _sp.GetRequiredService<ICrossTenantAuditService>();
        // Ask for a year — service must clamp to the configured 30 d.
        var rows = await svc.GetCrossTenantSummaryAsync(TimeSpan.FromDays(365));
        rows.Single().EventCount.Should().Be(1,
            "ancient event sits outside the clamped 30 d ceiling");
    }

    // ----- helpers -------------------------------------------------

    private async Task SeedTenantAsync(long id, string code, TenantState state)
    {
        using var scope = _sp.CreateScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        if (await tenancy.Tenants.AnyAsync(t => t.Id == id)) return;
        tenancy.Tenants.Add(new Tenant
        {
            Id = id,
            Code = code,
            Name = "Tenant " + code,
            State = state,
            BillingPlan = "internal",
            TimeZone = "UTC",
            Locale = "en",
            Currency = "USD",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await tenancy.SaveChangesAsync();
    }

    private async Task SeedAuditEventAsync(long tenantId, string eventType, DateTimeOffset at)
    {
        using var scope = _sp.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        audit.Events.Add(new DomainEventRow
        {
            EventId = Guid.NewGuid(),
            TenantId = tenantId,
            EventType = eventType,
            EntityType = "TestEntity",
            EntityId = "e-" + Guid.NewGuid().ToString("N")[..8],
            Payload = JsonDocument.Parse("{}"),
            OccurredAt = at,
            IngestedAt = at,
            IdempotencyKey = "ipk-" + Guid.NewGuid()
        });
        await audit.SaveChangesAsync();
    }

    /// <summary>
    /// Test-only subclass that adds a JsonDocument↔string value converter
    /// on <c>DomainEventRow.Payload</c> so the EF in-memory provider can
    /// materialise the column. Production runs use Postgres jsonb directly
    /// and never hit this converter.
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

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new DateTimeOffset(2026, 5, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) { _now = _now.Add(by); }
    }

    /// <summary>
    /// Recording <see cref="ITenantContext"/> that increments a static
    /// counter every time <see cref="SetSystemContext"/> is called. The
    /// counter is process-static so the service's per-tenant DI scope
    /// flips all aggregate to one number.
    /// </summary>
    private sealed class RecordingTenantContext : ITenantContext
    {
        public static int GlobalSetSystemContextCount;

        private long _tenantId;
        private bool _resolved;
        private bool _system;

        public long TenantId => _tenantId;
        public bool IsResolved => _resolved;
        public bool IsSystem => _system;

        public void SetTenant(long tenantId)
        {
            _tenantId = tenantId;
            _resolved = true;
            _system = false;
        }

        public void SetSystemContext()
        {
            System.Threading.Interlocked.Increment(ref GlobalSetSystemContextCount);
            _system = true;
            _tenantId = -1;
            _resolved = true;
        }
    }
}
