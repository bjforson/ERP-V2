using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Sla;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 31 / B5.1 Phase D — coverage for the
/// <see cref="SlaTracker"/> open/close/recompute paths. Asserts the
/// idempotency guarantees, breach detection, and the standard-window
/// helper.
/// </summary>
public sealed class SlaTrackerTests : IDisposable
{
    private readonly InspectionDbContext _db;
    private readonly TenantContext _tenant;
    private readonly InMemorySlaSettingsProvider _settings;
    private readonly SlaTrackerOptions _options;

    public SlaTrackerTests()
    {
        var dbOptions = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("sla-tracker-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new InspectionDbContext(dbOptions);
        _tenant = new TenantContext();
        _tenant.SetTenant(1);
        _settings = new InMemorySlaSettingsProvider();
        _options = new SlaTrackerOptions();
    }

    public void Dispose() => _db.Dispose();

    private SlaTracker NewTracker()
        => new(_db, _settings, _tenant, Options.Create(_options), NullLogger<SlaTracker>.Instance);

    [Fact]
    public async Task OpenStandardWindows_creates_three_rows()
    {
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var openedAt = new DateTimeOffset(2026, 5, 5, 8, 0, 0, TimeSpan.Zero);
        var rows = await tracker.OpenStandardWindowsAsync(caseId, openedAt);
        rows.Should().HaveCount(3);
        rows.Select(r => r.WindowName).Should().BeEquivalentTo(SlaTracker.StandardWindows);
        rows.All(r => r.State == SlaWindowState.OnTime).Should().BeTrue();
        rows.All(r => r.ClosedAt == null).Should().BeTrue();
    }

    [Fact]
    public async Task Open_is_idempotent_per_case_window()
    {
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        await tracker.OpenStandardWindowsAsync(caseId, t);
        // re-open shouldn't create extras
        await tracker.OpenStandardWindowsAsync(caseId, t);
        (await _db.SlaWindows.AsNoTracking().CountAsync(w => w.CaseId == caseId))
            .Should().Be(3);
    }

    [Fact]
    public async Task CloseWindow_flips_to_Closed_when_under_due()
    {
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        await tracker.OpenStandardWindowsAsync(caseId, t);
        var closeAt = t.AddMinutes(10);
        var ok = await tracker.CloseWindowAsync(caseId, SlaTracker.OpenToValidated, closeAt);
        ok.Should().BeTrue();
        var row = await _db.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.CaseId == caseId && w.WindowName == SlaTracker.OpenToValidated);
        row.State.Should().Be(SlaWindowState.Closed);
        row.ClosedAt.Should().Be(closeAt);
    }

    [Fact]
    public async Task CloseWindow_flips_to_Breached_when_over_due()
    {
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        await tracker.OpenStandardWindowsAsync(caseId, t);
        // OpenToValidated default budget = 60min; close 90min later
        var closeAt = t.AddMinutes(90);
        var ok = await tracker.CloseWindowAsync(caseId, SlaTracker.OpenToValidated, closeAt);
        ok.Should().BeTrue();
        var row = await _db.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.CaseId == caseId && w.WindowName == SlaTracker.OpenToValidated);
        row.State.Should().Be(SlaWindowState.Breached);
    }

    [Fact]
    public async Task CloseAllOpen_flips_every_open_row()
    {
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        await tracker.OpenStandardWindowsAsync(caseId, t);
        var closed = await tracker.CloseAllOpenWindowsAsync(caseId, t.AddMinutes(15));
        closed.Should().Be(3);
        (await _db.SlaWindows.CountAsync(w => w.CaseId == caseId && w.ClosedAt != null))
            .Should().Be(3);
    }

    [Fact]
    public async Task RefreshStates_promotes_open_to_AtRisk_then_Breached()
    {
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        await tracker.OpenStandardWindowsAsync(caseId, t);
        // OpenToValidated = 60min budget; AtRiskFraction = 0.5 → 30min in.
        var asOf = t.AddMinutes(45);
        await tracker.RefreshStatesAsync(caseId, asOf);
        var row = await _db.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.CaseId == caseId && w.WindowName == SlaTracker.OpenToValidated);
        row.State.Should().Be(SlaWindowState.AtRisk);

        // Past due (60min in)
        await tracker.RefreshStatesAsync(caseId, t.AddMinutes(75));
        row = await _db.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.CaseId == caseId && w.WindowName == SlaTracker.OpenToValidated);
        row.State.Should().Be(SlaWindowState.Breached);
    }

    [Fact]
    public async Task TenantOverride_takes_precedence_over_engine_default()
    {
        // Override OpenToValidated to 5min for tenant 1
        _settings.Set(1, SlaTracker.OpenToValidated, enabled: true, targetMinutes: 5);
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        await tracker.OpenStandardWindowsAsync(caseId, t);
        var row = await _db.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.CaseId == caseId && w.WindowName == SlaTracker.OpenToValidated);
        row.BudgetMinutes.Should().Be(5);
        row.DueAt.Should().Be(t.AddMinutes(5));
    }

    [Fact]
    public async Task Disabled_window_is_silently_skipped()
    {
        _settings.Set(1, SlaTracker.OpenToValidated, enabled: false, targetMinutes: 60);
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        await tracker.OpenStandardWindowsAsync(caseId, t);
        // OpenToValidated should be missing; the other two stay.
        var rows = await _db.SlaWindows.AsNoTracking()
            .Where(w => w.CaseId == caseId).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.WindowName).Should().NotContain(SlaTracker.OpenToValidated);
    }

    [Fact]
    public void ComputeOpenState_static_helper_matches_state_machine()
    {
        var t = new DateTimeOffset(2026, 5, 5, 0, 0, 0, TimeSpan.Zero);
        var window = new SlaWindow
        {
            StartedAt = t,
            DueAt = t.AddMinutes(60),
            State = SlaWindowState.OnTime,
            BudgetMinutes = 60
        };
        // Under threshold
        SlaTracker.ComputeOpenState(window, t.AddMinutes(15), 0.5)
            .Should().Be(SlaWindowState.OnTime);
        // At-risk threshold
        SlaTracker.ComputeOpenState(window, t.AddMinutes(45), 0.5)
            .Should().Be(SlaWindowState.AtRisk);
        // Breached
        SlaTracker.ComputeOpenState(window, t.AddMinutes(75), 0.5)
            .Should().Be(SlaWindowState.Breached);
    }
}
