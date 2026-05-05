using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Sla;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 45 / Phase E — coverage for the
/// <see cref="SlaDashboardService"/>'s per-tier breakdown +
/// reclassify path. Asserts the cards order by escalation
/// priority, the manual count surfaces, and reclassify flips both
/// tier + manual flag.
/// </summary>
public sealed class SlaDashboardServiceTierTests : IDisposable
{
    private readonly InspectionDbContext _db;
    private readonly TenantContext _tenant;
    private readonly SlaTrackerOptions _options;
    private const long TenantId = 17;

    public SlaDashboardServiceTierTests()
    {
        var dbOptions = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("sla-dash-tier-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new InspectionDbContext(dbOptions);
        _tenant = new TenantContext();
        _tenant.SetTenant(TenantId);
        _options = new SlaTrackerOptions();
    }

    public void Dispose() => _db.Dispose();

    private SlaDashboardService NewService()
        => new(_db, _tenant, Options.Create(_options),
               NullLogger<SlaDashboardService>.Instance);

    private async Task<SlaWindow> SeedWindowAsync(
        QueueTier tier, bool isManual = false,
        DateTimeOffset? closedAt = null,
        SlaWindowState state = SlaWindowState.OnTime)
    {
        var t = DateTimeOffset.UtcNow.AddMinutes(-15);
        var w = new SlaWindow
        {
            Id = Guid.NewGuid(),
            CaseId = Guid.NewGuid(),
            WindowName = "case.open_to_validated",
            StartedAt = t,
            DueAt = t.AddMinutes(60),
            ClosedAt = closedAt,
            State = state,
            BudgetMinutes = 60,
            QueueTier = tier,
            QueueTierIsManual = isManual,
            TenantId = TenantId
        };
        _db.SlaWindows.Add(w);
        await _db.SaveChangesAsync();
        return w;
    }

    [Fact]
    public async Task ListByTier_returns_one_row_per_tier_in_priority_order()
    {
        var svc = NewService();
        var rows = await svc.ListByTierAsync();
        rows.Should().HaveCount(5);
        rows.Select(r => r.Tier).Should().BeEquivalentTo(
            new[]
            {
                QueueTier.Urgent,
                QueueTier.High,
                QueueTier.Standard,
                QueueTier.Exception,
                QueueTier.PostClearance
            },
            options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task ListByTier_counts_open_windows_per_tier()
    {
        await SeedWindowAsync(QueueTier.Standard);
        await SeedWindowAsync(QueueTier.Standard);
        await SeedWindowAsync(QueueTier.High);
        await SeedWindowAsync(QueueTier.Urgent);

        var svc = NewService();
        var rows = await svc.ListByTierAsync();
        var byTier = rows.ToDictionary(r => r.Tier);

        // OnTime + AtRisk + Breached should add up to 2 / 1 / 1 / 0 / 0.
        var stdTotal = byTier[QueueTier.Standard].OpenOnTime
            + byTier[QueueTier.Standard].OpenAtRisk
            + byTier[QueueTier.Standard].OpenBreached;
        stdTotal.Should().Be(2);

        var highTotal = byTier[QueueTier.High].OpenOnTime
            + byTier[QueueTier.High].OpenAtRisk
            + byTier[QueueTier.High].OpenBreached;
        highTotal.Should().Be(1);

        var urgentTotal = byTier[QueueTier.Urgent].OpenOnTime
            + byTier[QueueTier.Urgent].OpenAtRisk
            + byTier[QueueTier.Urgent].OpenBreached;
        urgentTotal.Should().Be(1);

        byTier[QueueTier.Exception].OpenOnTime.Should().Be(0);
        byTier[QueueTier.PostClearance].OpenOnTime.Should().Be(0);
    }

    [Fact]
    public async Task ListByTier_surfaces_manual_count()
    {
        await SeedWindowAsync(QueueTier.High, isManual: true);
        await SeedWindowAsync(QueueTier.High, isManual: false);

        var svc = NewService();
        var rows = await svc.ListByTierAsync();
        var high = rows.Single(r => r.Tier == QueueTier.High);
        high.ManualCount.Should().Be(1);
    }

    [Fact]
    public async Task ReclassifyTier_flips_tier_and_manual_flag()
    {
        var w = await SeedWindowAsync(QueueTier.Standard);

        var svc = NewService();
        var ok = await svc.ReclassifyTierAsync(w.Id, QueueTier.Exception);

        ok.Should().BeTrue();
        var refreshed = await _db.SlaWindows.AsNoTracking()
            .SingleAsync(x => x.Id == w.Id);
        refreshed.QueueTier.Should().Be(QueueTier.Exception);
        refreshed.QueueTierIsManual.Should().BeTrue();
    }

    [Fact]
    public async Task ReclassifyTier_returns_false_for_unknown_id()
    {
        var svc = NewService();
        var ok = await svc.ReclassifyTierAsync(Guid.NewGuid(), QueueTier.Urgent);
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task ReclassifyTier_scopes_by_tenant()
    {
        // Seed under another tenant.
        var w = new SlaWindow
        {
            Id = Guid.NewGuid(),
            CaseId = Guid.NewGuid(),
            WindowName = "case.open_to_validated",
            StartedAt = DateTimeOffset.UtcNow,
            DueAt = DateTimeOffset.UtcNow.AddMinutes(60),
            BudgetMinutes = 60,
            QueueTier = QueueTier.Standard,
            TenantId = 99 // not _tenant.TenantId
        };
        _db.SlaWindows.Add(w);
        await _db.SaveChangesAsync();

        var svc = NewService();
        var ok = await svc.ReclassifyTierAsync(w.Id, QueueTier.Urgent);
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task ListByTier_excludes_closed_outside_lookback_window()
    {
        // Seed a closed row 30 days ago — outside the default 7-day
        // closed lookback.
        var t = DateTimeOffset.UtcNow.AddDays(-30);
        _db.SlaWindows.Add(new SlaWindow
        {
            Id = Guid.NewGuid(),
            CaseId = Guid.NewGuid(),
            WindowName = "case.open_to_validated",
            StartedAt = t,
            DueAt = t.AddMinutes(60),
            ClosedAt = t.AddMinutes(30),
            State = SlaWindowState.Closed,
            BudgetMinutes = 60,
            QueueTier = QueueTier.Standard,
            TenantId = TenantId
        });
        await _db.SaveChangesAsync();

        var svc = NewService();
        var rows = await svc.ListByTierAsync();
        rows.Single(r => r.Tier == QueueTier.Standard).ClosedOnTime.Should().Be(0);
    }
}
