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
/// Sprint 49 / FU-sla-trend-sparkline — coverage for
/// <see cref="SlaDashboardService.GetTrendAsync"/>. Asserts that the
/// per-day buckets are returned chronologically, that each bucket
/// counts opened / closed / breached on the right anchor (StartedAt /
/// ClosedAt / DueAt as appropriate), and that the default 14-day
/// window yields exactly 14 buckets.
/// </summary>
public sealed class SlaDashboardServiceTrendTests : IDisposable
{
    private readonly InspectionDbContext _db;
    private readonly TenantContext _tenant;
    private readonly SlaTrackerOptions _options;
    private const long TenantId = 23;

    public SlaDashboardServiceTrendTests()
    {
        var dbOptions = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("sla-dash-trend-" + Guid.NewGuid())
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

    private SlaWindow MakeWindow(
        DateTimeOffset startedAt,
        DateTimeOffset? closedAt = null,
        DateTimeOffset? dueAt = null,
        SlaWindowState state = SlaWindowState.OnTime)
    {
        return new SlaWindow
        {
            Id = Guid.NewGuid(),
            CaseId = Guid.NewGuid(),
            WindowName = "case.open_to_validated",
            StartedAt = startedAt,
            DueAt = dueAt ?? startedAt.AddMinutes(60),
            ClosedAt = closedAt,
            State = state,
            BudgetMinutes = 60,
            QueueTier = QueueTier.Standard,
            QueueTierIsManual = false,
            TenantId = TenantId
        };
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTrend_DefaultWindow_Returns14Buckets()
    {
        var svc = NewService();
        var rows = await svc.GetTrendAsync();
        rows.Should().HaveCount(14);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTrend_BucketsAreChronological_OldestFirst()
    {
        var svc = NewService();
        var rows = await svc.GetTrendAsync(days: 7);
        rows.Should().HaveCount(7);
        // Each bucket's Day must be one UTC day before the next.
        for (var i = 1; i < rows.Count; i++)
        {
            (rows[i].Day - rows[i - 1].Day).Should().Be(TimeSpan.FromDays(1));
        }
        // Last bucket ends at todayUtc midnight.
        var todayUtc = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        rows[^1].Day.Should().Be(todayUtc);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTrend_GroupsOpenedByStartedAt()
    {
        // Three windows opened on the same day, two on a different day
        // inside the trailing window — the bucket counts must reflect
        // the StartedAt anchor.
        var todayUtc = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var twoDaysAgo = todayUtc.AddDays(-2).AddHours(10);
        var fourDaysAgo = todayUtc.AddDays(-4).AddHours(2);

        _db.SlaWindows.AddRange(
            MakeWindow(twoDaysAgo),
            MakeWindow(twoDaysAgo.AddMinutes(10)),
            MakeWindow(twoDaysAgo.AddMinutes(20)),
            MakeWindow(fourDaysAgo),
            MakeWindow(fourDaysAgo.AddMinutes(5)));
        await _db.SaveChangesAsync();

        var svc = NewService();
        var rows = (await svc.GetTrendAsync(days: 7)).ToList();

        // Day -2 and Day -4 buckets should hold the opened counts.
        rows.Single(r => r.Day == todayUtc.AddDays(-2)).Opened.Should().Be(3);
        rows.Single(r => r.Day == todayUtc.AddDays(-4)).Opened.Should().Be(2);

        // Other days are zero.
        rows.Where(r => r.Day != todayUtc.AddDays(-2) && r.Day != todayUtc.AddDays(-4))
            .Sum(r => r.Opened).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTrend_GroupsClosedByClosedAt()
    {
        var todayUtc = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var startedFiveAgo = todayUtc.AddDays(-5).AddHours(2);
        var closedThreeAgo = todayUtc.AddDays(-3).AddHours(8);

        // One window started -5d, closed -3d: opened = -5d, closed = -3d.
        _db.SlaWindows.Add(MakeWindow(startedFiveAgo, closedAt: closedThreeAgo));
        await _db.SaveChangesAsync();

        var svc = NewService();
        var rows = (await svc.GetTrendAsync(days: 10)).ToList();

        rows.Single(r => r.Day == todayUtc.AddDays(-5)).Opened.Should().Be(1);
        rows.Single(r => r.Day == todayUtc.AddDays(-3)).Closed.Should().Be(1);
        rows.Single(r => r.Day == todayUtc.AddDays(-3)).Opened.Should().Be(0);
        rows.Single(r => r.Day == todayUtc.AddDays(-5)).Closed.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTrend_BreachedAnchoredOnClosedAt_ForClosedBreaches()
    {
        var todayUtc = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var startedSixAgo = todayUtc.AddDays(-6).AddHours(1);
        var closedTwoAgo = todayUtc.AddDays(-2).AddHours(1);
        // Closed-and-breached: should anchor breached on ClosedAt (-2d).
        _db.SlaWindows.Add(MakeWindow(
            startedSixAgo,
            closedAt: closedTwoAgo,
            state: SlaWindowState.Breached));
        await _db.SaveChangesAsync();

        var svc = NewService();
        var rows = (await svc.GetTrendAsync(days: 10)).ToList();

        rows.Single(r => r.Day == todayUtc.AddDays(-2)).Breached.Should().Be(1);
        rows.Single(r => r.Day == todayUtc.AddDays(-6)).Breached.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTrend_BreachedAnchoredOnDueAt_ForOpenPastDue()
    {
        var todayUtc = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var startedFiveAgo = todayUtc.AddDays(-5).AddHours(1);
        // DueAt -3d, still open ⇒ breach moment lives on -3d.
        var dueThreeAgo = todayUtc.AddDays(-3).AddHours(2);
        _db.SlaWindows.Add(MakeWindow(
            startedFiveAgo,
            closedAt: null,
            dueAt: dueThreeAgo));
        await _db.SaveChangesAsync();

        var svc = NewService();
        var rows = (await svc.GetTrendAsync(days: 10)).ToList();

        rows.Single(r => r.Day == todayUtc.AddDays(-3)).Breached.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTrend_FiltersByTenant()
    {
        var todayUtc = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var twoAgo = todayUtc.AddDays(-2).AddHours(2);
        // Tenant 23 row.
        _db.SlaWindows.Add(MakeWindow(twoAgo));
        // Other tenant — same day — must NOT be counted.
        _db.SlaWindows.Add(new SlaWindow
        {
            Id = Guid.NewGuid(),
            CaseId = Guid.NewGuid(),
            WindowName = "case.open_to_validated",
            StartedAt = twoAgo,
            DueAt = twoAgo.AddMinutes(60),
            BudgetMinutes = 60,
            QueueTier = QueueTier.Standard,
            TenantId = 999,
        });
        await _db.SaveChangesAsync();

        var svc = NewService();
        var rows = (await svc.GetTrendAsync(days: 5)).ToList();
        rows.Single(r => r.Day == todayUtc.AddDays(-2)).Opened.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTrend_NegativeDaysClampsToDefault()
    {
        var svc = NewService();
        var rows = await svc.GetTrendAsync(days: -3);
        rows.Should().HaveCount(14);
    }
}
