using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;
using NickERP.Portal.Services.Modules;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 29 — coverage for <see cref="TenantModuleSettingsService"/>.
/// Verifies upsert behaviour (insert vs update), actor stamping, and
/// per-tenant scoping.
/// </summary>
public sealed class TenantModuleSettingsServiceTests
{
    private readonly DateTimeOffset _now = new(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);

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
        return new TenantModuleSettingsService(ctx, clock);
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
}
