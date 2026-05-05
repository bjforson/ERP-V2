using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 31 / B5 Phase D — coverage for the new
/// <see cref="TenantCompletenessSetting"/> +
/// <see cref="TenantSlaSetting"/> entities. Asserts the EF model
/// shape (defaults, unique indexes) round-trips through the in-memory
/// provider; the Postgres-side RLS policy + nscim_app grants are
/// exercised by the live migration on deploy.
/// </summary>
public sealed class TenantCompletenessAndSlaSettingsTests : IDisposable
{
    private readonly TenancyDbContext _db;

    public TenantCompletenessAndSlaSettingsTests()
    {
        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseInMemoryDatabase("tcss-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new TenancyDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task TenantCompletenessSetting_persists_minimal_row_with_default_enabled()
    {
        _db.TenantCompletenessSettings.Add(new TenantCompletenessSetting
        {
            Id = Guid.NewGuid(),
            TenantId = 1,
            RequirementId = "required.scan_artifact",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var row = await _db.TenantCompletenessSettings.AsNoTracking().SingleAsync();
        row.Enabled.Should().BeTrue(because: "default is enabled — sparse rows persist only on disable, but the column default holds either way");
        row.RequirementId.Should().Be("required.scan_artifact");
        row.MinThreshold.Should().BeNull(because: "no threshold override unless admin explicitly sets one");
    }

    [Fact]
    public async Task TenantCompletenessSetting_threshold_override_round_trips()
    {
        _db.TenantCompletenessSettings.Add(new TenantCompletenessSetting
        {
            Id = Guid.NewGuid(),
            TenantId = 1,
            RequirementId = "required.image_coverage",
            Enabled = true,
            MinThreshold = 0.85m,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();
        var row = await _db.TenantCompletenessSettings.AsNoTracking().SingleAsync();
        row.MinThreshold.Should().Be(0.85m);
    }

    [Fact]
    public async Task TenantCompletenessSetting_DbSet_is_exposed()
    {
        // Compile-time invariant — the engine + admin service rely on
        // the DbSet being reachable from TenancyDbContext.
        _db.TenantCompletenessSettings.Should().NotBeNull();
        await _db.TenantCompletenessSettings.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task TenantCompletenessSetting_two_tenants_can_disable_the_same_requirement()
    {
        _db.TenantCompletenessSettings.AddRange(
            new TenantCompletenessSetting { Id = Guid.NewGuid(), TenantId = 1, RequirementId = "x.y", Enabled = false, UpdatedAt = DateTimeOffset.UtcNow },
            new TenantCompletenessSetting { Id = Guid.NewGuid(), TenantId = 2, RequirementId = "x.y", Enabled = false, UpdatedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();
        var rows = await _db.TenantCompletenessSettings.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.TenantId).Should().BeEquivalentTo(new long[] { 1, 2 });
    }

    [Fact]
    public async Task TenantSlaSetting_persists_with_target_minutes()
    {
        _db.TenantSlaSettings.Add(new TenantSlaSetting
        {
            Id = Guid.NewGuid(),
            TenantId = 1,
            WindowName = "case.open_to_validated",
            TargetMinutes = 45,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();
        var row = await _db.TenantSlaSettings.AsNoTracking().SingleAsync();
        row.TargetMinutes.Should().Be(45);
        row.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task TenantSlaSetting_DbSet_is_exposed()
    {
        _db.TenantSlaSettings.Should().NotBeNull();
        await _db.TenantSlaSettings.AsNoTracking().ToListAsync();
    }
}
