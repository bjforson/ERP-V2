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
/// Sprint 35 / B8.2 — coverage for <see cref="TenantSettingsService"/>.
/// Mirrors <see cref="FeatureFlagServiceTests"/> but with string values
/// + the int-parse helper. Audit-emission shape is the
/// <c>nickerp.tenancy.setting_changed</c> sibling of the feature flag
/// event.
/// </summary>
public sealed class TenantSettingsServiceTests
{
    private readonly DateTimeOffset _now = new(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly List<DomainEvent> _events = new();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAsync_ReturnsDefault_WhenNoRowExists()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);

        var raw = await svc.GetAsync("comms.email.smtp_host", 1, defaultValue: "smtp.fallback.local");

        raw.Should().Be("smtp.fallback.local");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAsync_ReturnsPersistedValue_WhenRowExists()
    {
        await using var ctx = BuildCtx();
        ctx.TenantSettings.Add(new TenantSetting
        {
            Id = Guid.NewGuid(),
            TenantId = 1,
            SettingKey = "comms.email.smtp_host",
            Value = "smtp.real.example.com",
            UpdatedAt = _now,
        });
        await ctx.SaveChangesAsync();
        var svc = BuildService(ctx);

        var raw = await svc.GetAsync("comms.email.smtp_host", 1, defaultValue: "ignored");
        raw.Should().Be("smtp.real.example.com");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetIntAsync_ParsesPersistedValue()
    {
        await using var ctx = BuildCtx();
        ctx.TenantSettings.Add(new TenantSetting
        {
            Id = Guid.NewGuid(),
            TenantId = 1,
            SettingKey = "comms.email.smtp_port",
            Value = "587",
            UpdatedAt = _now,
        });
        await ctx.SaveChangesAsync();
        var svc = BuildService(ctx);

        var port = await svc.GetIntAsync("comms.email.smtp_port", 1, defaultValue: 25);
        port.Should().Be(587);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetIntAsync_FallsBackOnNonNumericValue()
    {
        await using var ctx = BuildCtx();
        ctx.TenantSettings.Add(new TenantSetting
        {
            Id = Guid.NewGuid(),
            TenantId = 1,
            SettingKey = "comms.email.smtp_port",
            Value = "not-a-number",
            UpdatedAt = _now,
        });
        await ctx.SaveChangesAsync();
        var svc = BuildService(ctx);

        var port = await svc.GetIntAsync("comms.email.smtp_port", 1, defaultValue: 25);
        port.Should().Be(25);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetAsync_NoExistingRow_InsertsNewRow()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);
        var actor = Guid.NewGuid();

        var dto = await svc.SetAsync("comms.email.smtp_host", 5, "smtp.example.com", actor);

        dto.TenantId.Should().Be(5);
        dto.SettingKey.Should().Be("comms.email.smtp_host");
        dto.Value.Should().Be("smtp.example.com");
        dto.UpdatedByUserId.Should().Be(actor);
        dto.UpdatedAt.Should().Be(_now);

        var rows = await ctx.TenantSettings.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetAsync_ExistingRow_UpdatesInPlace()
    {
        await using var ctx = BuildCtx();
        ctx.TenantSettings.Add(new TenantSetting
        {
            Id = Guid.NewGuid(),
            TenantId = 1,
            SettingKey = "comms.email.smtp_host",
            Value = "smtp.old.local",
            UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await ctx.SaveChangesAsync();
        var svc = BuildService(ctx);

        var dto = await svc.SetAsync("comms.email.smtp_host", 1, "smtp.new.local", actorUserId: null);

        dto.Value.Should().Be("smtp.new.local");
        dto.UpdatedAt.Should().Be(_now);

        var rows = await ctx.TenantSettings.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetAsync_NormalisesSettingKey()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);

        await svc.SetAsync("  Comms.Email.Smtp_Host  ", 1, "host", actorUserId: null);

        var rows = await ctx.TenantSettings.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle()
            .Which.SettingKey.Should().Be("comms.email.smtp_host");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetAsync_RejectsBlankSettingKey()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);

        Func<Task> act = () => svc.SetAsync("  ", 1, "v", null);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetAsync_RejectsNullValue()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);

        Func<Task> act = () => svc.SetAsync("k", 1, value: null!, actorUserId: null);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_FiltersByTenant()
    {
        await using var ctx = BuildCtx();
        ctx.TenantSettings.AddRange(
            new TenantSetting { Id = Guid.NewGuid(), TenantId = 1, SettingKey = "a", Value = "1", UpdatedAt = _now },
            new TenantSetting { Id = Guid.NewGuid(), TenantId = 1, SettingKey = "b", Value = "2", UpdatedAt = _now },
            new TenantSetting { Id = Guid.NewGuid(), TenantId = 2, SettingKey = "a", Value = "3", UpdatedAt = _now });
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
    public async Task SetAsync_NewRow_EmitsChangedEvent()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx);
        var actor = Guid.NewGuid();

        var dto = await svc.SetAsync("inspection.sla.default_budget_minutes", 7, "120", actor);

        _events.Should().ContainSingle()
            .Which.EventType.Should().Be("nickerp.tenancy.setting_changed");

        var evt = _events.Single();
        evt.TenantId.Should().Be(7);
        evt.ActorUserId.Should().Be(actor);
        evt.EntityType.Should().Be("TenantSetting");
        evt.EntityId.Should().Be(dto.Id.ToString());
        evt.IdempotencyKey.Should().NotBeNullOrEmpty();

        var root = evt.Payload;
        root.GetProperty("tenantId").GetInt64().Should().Be(7);
        root.GetProperty("settingKey").GetString().Should().Be("inspection.sla.default_budget_minutes");
        root.GetProperty("value").GetString().Should().Be("120");
        root.GetProperty("oldValue").GetString().Should().Be(string.Empty,
            because: "new rows synthesise oldValue = empty string");
        root.GetProperty("userId").GetString().Should().Be(actor.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetAsync_ExistingRow_EmitsRealOldValue()
    {
        await using var ctx = BuildCtx();
        ctx.TenantSettings.Add(new TenantSetting
        {
            Id = Guid.NewGuid(),
            TenantId = 3,
            SettingKey = "comms.email.smtp_host",
            Value = "smtp.old.local",
            UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await ctx.SaveChangesAsync();
        var svc = BuildService(ctx);

        await svc.SetAsync("comms.email.smtp_host", 3, "smtp.new.local", actorUserId: null);

        _events.Should().ContainSingle();
        var root = _events.Single().Payload;
        root.GetProperty("value").GetString().Should().Be("smtp.new.local");
        root.GetProperty("oldValue").GetString().Should().Be("smtp.old.local");
    }

    // -- helpers --------------------------------------------------------

    private TenantSettingsService BuildService(TenancyDbContext ctx)
    {
        var clock = new FakeTimeProvider(_now);
        var publisher = new CapturingEventPublisher(_events);
        return new TenantSettingsService(ctx, clock, publisher, NullLogger<TenantSettingsService>.Instance);
    }

    private static TenancyDbContext BuildCtx()
    {
        var name = "tenant-settings-" + Guid.NewGuid();
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
