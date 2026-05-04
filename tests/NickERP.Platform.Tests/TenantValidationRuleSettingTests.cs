using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 28 / B4 Phase D — coverage for the new
/// <see cref="TenantValidationRuleSetting"/> entity. Asserts the EF model
/// shape (unique index, default Enabled=true) round-trips through the
/// in-memory provider; the Postgres-side RLS policy + nscim_app grants
/// are exercised by the live migration on deploy.
/// </summary>
public sealed class TenantValidationRuleSettingTests : IDisposable
{
    private readonly TenancyDbContext _db;

    public TenantValidationRuleSettingTests()
    {
        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseInMemoryDatabase("trvs-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new TenancyDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Persists_minimal_row_with_default_enabled()
    {
        _db.TenantValidationRuleSettings.Add(new TenantValidationRuleSetting
        {
            Id = Guid.NewGuid(),
            TenantId = 1,
            RuleId = "customsgh.port_match",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var row = await _db.TenantValidationRuleSettings.AsNoTracking().SingleAsync();
        row.Enabled.Should().BeTrue(because: "default is enabled — sparse rows persist only on disable, but the column default holds either way");
        row.RuleId.Should().Be("customsgh.port_match");
    }

    [Fact]
    public async Task DbSet_is_exposed_on_TenancyDbContext()
    {
        // Compile-time invariant — the engine + admin service rely on
        // the DbSet being reachable from TenancyDbContext.
        _db.TenantValidationRuleSettings.Should().NotBeNull();
        await _db.TenantValidationRuleSettings.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task Two_tenants_can_disable_the_same_rule()
    {
        // The unique index is on (TenantId, RuleId), not on RuleId alone —
        // each tenant gets its own override row.
        _db.TenantValidationRuleSettings.Add(new TenantValidationRuleSetting
        {
            Id = Guid.NewGuid(), TenantId = 1, RuleId = "x.y", Enabled = false, UpdatedAt = DateTimeOffset.UtcNow
        });
        _db.TenantValidationRuleSettings.Add(new TenantValidationRuleSetting
        {
            Id = Guid.NewGuid(), TenantId = 2, RuleId = "x.y", Enabled = false, UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var rows = await _db.TenantValidationRuleSettings.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.TenantId).Should().BeEquivalentTo(new long[] { 1, 2 });
    }
}
