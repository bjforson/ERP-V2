using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;
using NickERP.Portal.Services.Modules;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 29 — coverage for <see cref="TenantModuleSettingsService"/>.
/// Verifies upsert behaviour (insert vs update), actor stamping, and
/// per-tenant scoping.
///
/// <para>
/// Sprint 32 FU-B — extended with audit-emission coverage. Every
/// successful upsert now publishes a <c>nickerp.tenancy.module_toggled</c>
/// event; tests assert the event is emitted on enable + disable, that
/// the payload carries the right fields (camelCase), and that the
/// <c>EntityType</c> + <c>EntityId</c> are set per the Sprint 28
/// audit-event-shape conventions.
/// </para>
/// </summary>
public sealed class TenantModuleSettingsServiceTests
{
    private readonly DateTimeOffset _now = new(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);
    private readonly List<DomainEvent> _events = new();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetEnabled_NoExistingRow_InsertsNewRow()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);
        var actor = Guid.NewGuid();

        var dto = await svc.SetEnabledAsync(
            tenantId: 5, moduleId: "inspection", enabled: false, actorUserId: actor);

        dto.TenantId.Should().Be(5);
        dto.ModuleId.Should().Be("inspection");
        dto.Enabled.Should().BeFalse();
        dto.UpdatedByUserId.Should().Be(actor);
        dto.UpdatedAt.Should().Be(_now);

        var rows = await ctx.TenantModuleSettings.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                TenantId = 5L,
                ModuleId = "inspection",
                Enabled = false,
                UpdatedByUserId = (Guid?)actor,
                UpdatedAt = _now,
            });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetEnabled_ExistingRow_UpdatesInPlace()
    {
        await using var ctx = BuildCtx();
        ctx.TenantModuleSettings.Add(new TenantModuleSetting
        {
            TenantId = 1,
            ModuleId = "nickfinance",
            Enabled = true,
            UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await ctx.SaveChangesAsync();
        var svc = BuildService(ctx);
        var actor = Guid.NewGuid();

        var dto = await svc.SetEnabledAsync(
            tenantId: 1, moduleId: "nickfinance", enabled: false, actorUserId: actor);

        dto.Enabled.Should().BeFalse();
        dto.UpdatedAt.Should().Be(_now);
        dto.UpdatedByUserId.Should().Be(actor);

        var rows = await ctx.TenantModuleSettings.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetEnabled_NormalisesModuleIdToLowercase()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);

        await svc.SetEnabledAsync(
            tenantId: 1, moduleId: "  Inspection  ", enabled: true, actorUserId: null);

        var rows = await ctx.TenantModuleSettings.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle().Which.ModuleId.Should().Be("inspection");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetEnabled_RejectsBlankModuleId()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);

        Func<Task> act = () => svc.SetEnabledAsync(1, "  ", true, null);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetSettingsForTenant_FiltersByTenantId()
    {
        await using var ctx = BuildCtx();
        ctx.TenantModuleSettings.AddRange(
            new TenantModuleSetting { TenantId = 1, ModuleId = "inspection", Enabled = false, UpdatedAt = _now },
            new TenantModuleSetting { TenantId = 1, ModuleId = "nickhr", Enabled = true, UpdatedAt = _now },
            new TenantModuleSetting { TenantId = 2, ModuleId = "inspection", Enabled = true, UpdatedAt = _now });
        await ctx.SaveChangesAsync();
        var svc = BuildService(ctx);

        var t1 = await svc.GetSettingsForTenantAsync(1);
        t1.Should().HaveCount(2);
        t1.Should().OnlyContain(r => r.TenantId == 1);

        var t2 = await svc.GetSettingsForTenantAsync(2);
        t2.Should().ContainSingle().Which.TenantId.Should().Be(2);
    }

    private TenantModuleSettingsService BuildService(TenancyDbContext ctx)
    {
        var clock = new FakeTimeProvider(_now);
        var publisher = new CapturingEventPublisher(_events);
        var logger = NullLogger<TenantModuleSettingsService>.Instance;
        return new TenantModuleSettingsService(ctx, clock, publisher, logger);
    }

    private static TenancyDbContext BuildCtx()
    {
        var name = "tenant-module-settings-" + Guid.NewGuid();
        var opts = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TenancyDbContext(opts);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// Sprint 32 FU-B — captures every published event so tests can
    /// assert audit emission without booting Postgres. Mirrors the
    /// pattern from <c>RulesAdminServiceTests.CapturingEventPublisher</c>.
    /// </summary>
    private sealed class CapturingEventPublisher : IEventPublisher
    {
        private readonly List<DomainEvent> _sink;
        public CapturingEventPublisher(List<DomainEvent> sink) => _sink = sink;
        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default)
        {
            _sink.Add(evt);
            return Task.FromResult(evt);
        }
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
        {
            _sink.AddRange(events);
            return Task.FromResult(events);
        }
    }

    // ============================================================================
    // Sprint 32 FU-B — audit-event coverage. The service emits one
    // nickerp.tenancy.module_toggled event per successful upsert (whether
    // the underlying row already existed or not). Best-effort emission
    // (try/catch + log) is verified by the "publisher throws" test below
    // — the upsert still lands.
    // ============================================================================

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetEnabled_NewRow_EmitsModuleToggledEventOnEnable()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);
        var actor = Guid.NewGuid();

        var dto = await svc.SetEnabledAsync(
            tenantId: 7, moduleId: "inspection", enabled: true, actorUserId: actor);

        _events.Should().ContainSingle()
            .Which.EventType.Should().Be("nickerp.tenancy.module_toggled");

        var evt = _events.Single();
        evt.TenantId.Should().Be(7);
        evt.ActorUserId.Should().Be(actor);
        evt.EntityType.Should().Be("TenantModuleSetting");
        evt.EntityId.Should().Be(dto.Id.ToString());
        evt.IdempotencyKey.Should().NotBeNullOrEmpty();

        // Payload shape — camelCase keys, full set: tenantId, moduleId,
        // enabled, oldEnabled, userId. New row → oldEnabled = !enabled.
        var root = evt.Payload;
        root.GetProperty("tenantId").GetInt64().Should().Be(7);
        root.GetProperty("moduleId").GetString().Should().Be("inspection");
        root.GetProperty("enabled").GetBoolean().Should().BeTrue();
        root.GetProperty("oldEnabled").GetBoolean().Should().BeFalse(
            because: "no prior row → synthesise oldEnabled = !enabled so the toggle delta is visible");
        root.GetProperty("userId").GetString().Should().Be(actor.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetEnabled_NewRow_EmitsModuleToggledEventOnDisable()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);
        var actor = Guid.NewGuid();

        var dto = await svc.SetEnabledAsync(
            tenantId: 9, moduleId: "nickfinance", enabled: false, actorUserId: actor);

        _events.Should().ContainSingle()
            .Which.EventType.Should().Be("nickerp.tenancy.module_toggled");

        var root = _events.Single().Payload;
        root.GetProperty("enabled").GetBoolean().Should().BeFalse();
        root.GetProperty("oldEnabled").GetBoolean().Should().BeTrue(
            because: "the new-row case synthesises oldEnabled = !enabled, so disabling a fresh row reads as 'was true → now false'");
        // EntityId mirrors the persisted row's Id.
        _events.Single().EntityId.Should().Be(dto.Id.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetEnabled_ExistingRow_EmitsEventWithRealOldEnabled()
    {
        await using var ctx = BuildCtx();
        ctx.TenantModuleSettings.Add(new TenantModuleSetting
        {
            TenantId = 3,
            ModuleId = "inspection",
            Enabled = true,
            UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await ctx.SaveChangesAsync();
        var svc = BuildService(ctx);
        var actor = Guid.NewGuid();

        await svc.SetEnabledAsync(
            tenantId: 3, moduleId: "inspection", enabled: false, actorUserId: actor);

        _events.Should().ContainSingle();
        var root = _events.Single().Payload;
        root.GetProperty("enabled").GetBoolean().Should().BeFalse();
        root.GetProperty("oldEnabled").GetBoolean().Should().BeTrue(
            because: "the prior row's Enabled was true; the audit payload preserves the real old value");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetEnabled_TwoToggles_EmitsTwoEvents()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);

        await svc.SetEnabledAsync(1, "inspection", enabled: false, actorUserId: null);
        await svc.SetEnabledAsync(1, "inspection", enabled: true, actorUserId: null);

        _events.Should().HaveCount(2);
        _events.Should().AllSatisfy(e => e.EventType.Should().Be("nickerp.tenancy.module_toggled"));
        // First flip: new row, enabled=false (synth oldEnabled=true).
        // Second flip: existing row, enabled=true with oldEnabled=false.
        _events[0].Payload.GetProperty("enabled").GetBoolean().Should().BeFalse();
        _events[1].Payload.GetProperty("enabled").GetBoolean().Should().BeTrue();
        _events[1].Payload.GetProperty("oldEnabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetEnabled_NormalisedModuleIdAppearsInPayload()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);

        await svc.SetEnabledAsync(2, "  Inspection  ", enabled: true, actorUserId: null);

        _events.Should().ContainSingle();
        _events.Single().Payload.GetProperty("moduleId").GetString().Should().Be("inspection",
            because: "the audit row should carry the canonical normalised id, not the raw user-supplied form");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetEnabled_PublisherThrows_RowStillPersists()
    {
        // Best-effort audit emission: a failed publisher must not roll
        // back the upsert. Same posture as RulesAdminService.
        await using var ctx = BuildCtx();
        var clock = new FakeTimeProvider(_now);
        var throwingPublisher = new ThrowingEventPublisher();
        var svc = new TenantModuleSettingsService(
            ctx, clock, throwingPublisher, NullLogger<TenantModuleSettingsService>.Instance);

        var dto = await svc.SetEnabledAsync(4, "nickhr", enabled: true, actorUserId: null);

        // Row landed despite the publisher throwing.
        var rows = await ctx.TenantModuleSettings.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Enabled.Should().BeTrue();
        rows[0].ModuleId.Should().Be("nickhr");
        dto.Enabled.Should().BeTrue();
    }

    private sealed class ThrowingEventPublisher : IEventPublisher
    {
        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated audit-bus failure");
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated audit-bus failure");
    }
}
