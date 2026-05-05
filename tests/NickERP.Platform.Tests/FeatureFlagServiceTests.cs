using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Database.Services;
using NickERP.Platform.Tenancy.Entities;
using NickERP.Platform.Tenancy.Features;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 35 / B8.2 — coverage for <see cref="FeatureFlagService"/>.
/// Mirrors the Sprint 32 FU-B refit of <c>TenantModuleSettingsService</c>:
/// upsert behaviour, sparse-row read fallback, audit-emission shape.
/// </summary>
public sealed class FeatureFlagServiceTests
{
    private readonly DateTimeOffset _now = new(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly List<DomainEvent> _events = new();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IsEnabled_ReturnsDefault_WhenNoRowExists()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);

        var defaultedTrue = await svc.IsEnabledAsync("portal.test.flag", tenantId: 1, defaultValue: true);
        var defaultedFalse = await svc.IsEnabledAsync("portal.test.flag", tenantId: 1, defaultValue: false);

        defaultedTrue.Should().BeTrue();
        defaultedFalse.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IsEnabled_ReturnsPersistedValue_WhenRowExists()
    {
        await using var ctx = BuildCtx();
        ctx.FeatureFlags.Add(new FeatureFlag
        {
            Id = Guid.NewGuid(),
            TenantId = 1,
            FlagKey = "inspection.test.enabled",
            Enabled = true,
            UpdatedAt = _now,
        });
        ctx.FeatureFlags.Add(new FeatureFlag
        {
            Id = Guid.NewGuid(),
            TenantId = 1,
            FlagKey = "inspection.test.disabled",
            Enabled = false,
            UpdatedAt = _now,
        });
        await ctx.SaveChangesAsync();
        var svc = BuildService(ctx);

        // Persisted value wins over the caller's default.
        (await svc.IsEnabledAsync("inspection.test.enabled", 1, defaultValue: false)).Should().BeTrue();
        (await svc.IsEnabledAsync("inspection.test.disabled", 1, defaultValue: true)).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetAsync_NoExistingRow_InsertsNewRow()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);
        var actor = Guid.NewGuid();

        var dto = await svc.SetAsync("portal.test.flag", tenantId: 5, enabled: true, actorUserId: actor);

        dto.TenantId.Should().Be(5);
        dto.FlagKey.Should().Be("portal.test.flag");
        dto.Enabled.Should().BeTrue();
        dto.UpdatedByUserId.Should().Be(actor);
        dto.UpdatedAt.Should().Be(_now);

        var rows = await ctx.FeatureFlags.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            TenantId = 5L,
            FlagKey = "portal.test.flag",
            Enabled = true,
            UpdatedByUserId = (Guid?)actor,
            UpdatedAt = _now,
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetAsync_ExistingRow_UpdatesInPlace()
    {
        await using var ctx = BuildCtx();
        ctx.FeatureFlags.Add(new FeatureFlag
        {
            Id = Guid.NewGuid(),
            TenantId = 1,
            FlagKey = "comms.email.development_outbox_only",
            Enabled = false,
            UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await ctx.SaveChangesAsync();
        var svc = BuildService(ctx);
        var actor = Guid.NewGuid();

        var dto = await svc.SetAsync(
            "comms.email.development_outbox_only", 1, enabled: true, actorUserId: actor);

        dto.Enabled.Should().BeTrue();
        dto.UpdatedAt.Should().Be(_now);
        dto.UpdatedByUserId.Should().Be(actor);

        var rows = await ctx.FeatureFlags.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetAsync_NormalisesFlagKey()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);

        await svc.SetAsync("  Inspection.Test.Flag  ", 1, enabled: true, actorUserId: null);

        var rows = await ctx.FeatureFlags.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle()
            .Which.FlagKey.Should().Be("inspection.test.flag");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetAsync_RejectsBlankFlagKey()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);

        Func<Task> act = () => svc.SetAsync("  ", 1, enabled: true, actorUserId: null);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_FiltersByTenant()
    {
        await using var ctx = BuildCtx();
        ctx.FeatureFlags.AddRange(
            new FeatureFlag { Id = Guid.NewGuid(), TenantId = 1, FlagKey = "a", Enabled = true, UpdatedAt = _now },
            new FeatureFlag { Id = Guid.NewGuid(), TenantId = 1, FlagKey = "b", Enabled = false, UpdatedAt = _now },
            new FeatureFlag { Id = Guid.NewGuid(), TenantId = 2, FlagKey = "a", Enabled = true, UpdatedAt = _now });
        await ctx.SaveChangesAsync();
        var svc = BuildService(ctx);

        var t1 = await svc.ListAsync(1);
        t1.Should().HaveCount(2);
        t1.Should().OnlyContain(r => r.TenantId == 1);

        var t2 = await svc.ListAsync(2);
        t2.Should().ContainSingle().Which.TenantId.Should().Be(2);
    }

    // -- audit-emission coverage ----------------------------------------

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetAsync_NewRow_EmitsToggledEvent()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);
        var actor = Guid.NewGuid();

        var dto = await svc.SetAsync("portal.test.flag", tenantId: 7, enabled: true, actorUserId: actor);

        _events.Should().ContainSingle()
            .Which.EventType.Should().Be("nickerp.tenancy.feature_flag_toggled");

        var evt = _events.Single();
        evt.TenantId.Should().Be(7);
        evt.ActorUserId.Should().Be(actor);
        evt.EntityType.Should().Be("FeatureFlag");
        evt.EntityId.Should().Be(dto.Id.ToString());
        evt.IdempotencyKey.Should().NotBeNullOrEmpty();

        var root = evt.Payload;
        root.GetProperty("tenantId").GetInt64().Should().Be(7);
        root.GetProperty("flagKey").GetString().Should().Be("portal.test.flag");
        root.GetProperty("enabled").GetBoolean().Should().BeTrue();
        root.GetProperty("oldEnabled").GetBoolean().Should().BeFalse(
            because: "no prior row → synthesise oldEnabled = !enabled so the toggle delta is visible");
        root.GetProperty("userId").GetString().Should().Be(actor.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetAsync_ExistingRow_EmitsRealOldEnabled()
    {
        await using var ctx = BuildCtx();
        ctx.FeatureFlags.Add(new FeatureFlag
        {
            Id = Guid.NewGuid(),
            TenantId = 3,
            FlagKey = "portal.flag",
            Enabled = true,
            UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await ctx.SaveChangesAsync();
        var svc = BuildService(ctx);

        await svc.SetAsync("portal.flag", 3, enabled: false, actorUserId: null);

        _events.Should().ContainSingle();
        var root = _events.Single().Payload;
        root.GetProperty("enabled").GetBoolean().Should().BeFalse();
        root.GetProperty("oldEnabled").GetBoolean().Should().BeTrue(
            because: "existing row carries the real prior value");
    }

    // -- helpers --------------------------------------------------------

    private FeatureFlagService BuildService(TenancyDbContext ctx)
    {
        var clock = new FakeTimeProvider(_now);
        var publisher = new CapturingEventPublisher(_events);
        return new FeatureFlagService(ctx, clock, publisher, NullLogger<FeatureFlagService>.Instance);
    }

    private static TenancyDbContext BuildCtx()
    {
        var name = "feature-flags-" + Guid.NewGuid();
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
}
