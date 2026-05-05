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
/// Sprint 45 / Phase E — coverage for the
/// <see cref="SlaTracker"/>'s tier-aware budget resolution path.
/// Asserts:
/// <list type="bullet">
///   <item><description>Standard tier honours engine defaults (backward compat).</description></item>
///   <item><description>High tier picks per-tier budget (5m first-review).</description></item>
///   <item><description>Urgent tier picks per-tier budget (1m first-review).</description></item>
///   <item><description>Per-tenant overrides still beat per-tier budgets.</description></item>
///   <item><description>QueueTier persists onto the SlaWindow row.</description></item>
///   <item><description>Tenant-supplied tier override overrides hard defaults.</description></item>
/// </list>
/// </summary>
public sealed class SlaTrackerQueueTierTests : IDisposable
{
    private readonly InspectionDbContext _db;
    private readonly TenantContext _tenant;
    private readonly InMemorySlaSettingsProvider _settings;
    private readonly SlaTrackerOptions _options;

    public SlaTrackerQueueTierTests()
    {
        var dbOptions = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("sla-tier-" + Guid.NewGuid())
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
    public async Task Standard_tier_uses_engine_defaults_for_backward_compat()
    {
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;

        await tracker.OpenWindowsAsync(
            caseId, SlaTracker.StandardWindows, t, QueueTier.Standard);

        var rows = await _db.SlaWindows.AsNoTracking()
            .Where(w => w.CaseId == caseId).ToListAsync();
        // Engine default for OpenToValidated is 60min — Standard tier
        // doesn't override engine defaults to preserve Sprint 31 behaviour.
        rows.Single(r => r.WindowName == SlaTracker.OpenToValidated)
            .BudgetMinutes.Should().Be(60);
        rows.All(r => r.QueueTier == QueueTier.Standard).Should().BeTrue();
        rows.All(r => !r.QueueTierIsManual).Should().BeTrue();
    }

    [Fact]
    public async Task High_tier_picks_5m_first_review_budget()
    {
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;

        await tracker.OpenWindowsAsync(
            caseId, new[] { SlaTracker.OpenToValidated }, t, QueueTier.High);

        var row = await _db.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.CaseId == caseId);
        row.BudgetMinutes.Should().Be(5);
        row.QueueTier.Should().Be(QueueTier.High);
    }

    [Fact]
    public async Task Urgent_tier_picks_1m_first_review_budget()
    {
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;

        await tracker.OpenWindowsAsync(
            caseId, new[] { SlaTracker.OpenToValidated }, t, QueueTier.Urgent);

        var row = await _db.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.CaseId == caseId);
        row.BudgetMinutes.Should().Be(1);
        row.QueueTier.Should().Be(QueueTier.Urgent);
    }

    [Fact]
    public async Task PostClearance_tier_picks_24h_budget()
    {
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;

        await tracker.OpenWindowsAsync(
            caseId, new[] { SlaTracker.OpenToValidated }, t, QueueTier.PostClearance);

        var row = await _db.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.CaseId == caseId);
        row.BudgetMinutes.Should().Be(24 * 60);
        row.QueueTier.Should().Be(QueueTier.PostClearance);
    }

    [Fact]
    public async Task Exception_tier_skips_window_when_zero_minutes()
    {
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;

        // Exception tier has (FirstReviewMinutes, FinalMinutes) = (0, 0)
        // in defaults — IsTierIndefinite + IsTierBoundWindow gate
        // means the tracker SKIPS the row rather than falling through
        // to engine defaults. This preserves the "indefinite hold"
        // contract: no SLA enforcement when the operator manually
        // pushes a case into the Exception tier.
        await tracker.OpenWindowsAsync(
            caseId, new[] { SlaTracker.OpenToValidated }, t, QueueTier.Exception);

        var rows = await _db.SlaWindows.AsNoTracking()
            .Where(w => w.CaseId == caseId).ToListAsync();
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Tenant_per_window_override_beats_tier_default()
    {
        // Tenant overrides OpenToValidated to 90m — wins over Urgent's 1m.
        _settings.Set(1, SlaTracker.OpenToValidated, enabled: true, targetMinutes: 90);
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;

        await tracker.OpenWindowsAsync(
            caseId, new[] { SlaTracker.OpenToValidated }, t, QueueTier.Urgent);

        var row = await _db.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.CaseId == caseId);
        row.BudgetMinutes.Should().Be(90);
        row.QueueTier.Should().Be(QueueTier.Urgent);
    }

    [Fact]
    public async Task TierOverride_in_options_beats_hard_default()
    {
        _options.TierOverrides[QueueTier.High] = (FirstReviewMinutes: 7, FinalMinutes: 25);
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;

        await tracker.OpenWindowsAsync(
            caseId, new[] { SlaTracker.OpenToValidated }, t, QueueTier.High);

        var row = await _db.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.CaseId == caseId);
        row.BudgetMinutes.Should().Be(7);
    }

    [Fact]
    public async Task Final_window_uses_final_budget_for_tier()
    {
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;

        // ValidatedToVerdict + VerdictToSubmitted both map to "final" budget
        // for the tier; engine default for ValidatedToVerdict is 240
        // (4h), but engine defaults take precedence for Standard. Use
        // High tier to exercise the per-tier final budget (30m).
        await tracker.OpenWindowsAsync(
            caseId,
            new[] { SlaTracker.ValidatedToVerdict, SlaTracker.VerdictToSubmitted },
            t, QueueTier.High);

        var rows = await _db.SlaWindows.AsNoTracking()
            .Where(w => w.CaseId == caseId).ToListAsync();
        rows.Should().HaveCount(2);
        rows.All(r => r.BudgetMinutes == 30).Should().BeTrue();
        rows.All(r => r.QueueTier == QueueTier.High).Should().BeTrue();
    }

    [Fact]
    public async Task Legacy_OpenWindowsAsync_overload_defaults_to_Standard_tier()
    {
        var tracker = NewTracker();
        var caseId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;

        // 4-arg overload — Standard tier implicit.
        await tracker.OpenStandardWindowsAsync(caseId, t);

        var rows = await _db.SlaWindows.AsNoTracking()
            .Where(w => w.CaseId == caseId).ToListAsync();
        rows.All(r => r.QueueTier == QueueTier.Standard).Should().BeTrue();
        rows.All(r => !r.QueueTierIsManual).Should().BeTrue();
    }

    [Fact]
    public void ResolveTierBudget_static_helper_returns_hard_defaults_when_no_override()
    {
        var opts = new SlaTrackerOptions();
        SlaTracker.ResolveTierBudget(QueueTier.Standard, opts).Should().Be((15, 60));
        SlaTracker.ResolveTierBudget(QueueTier.High, opts).Should().Be((5, 30));
        SlaTracker.ResolveTierBudget(QueueTier.Urgent, opts).Should().Be((1, 10));
        SlaTracker.ResolveTierBudget(QueueTier.PostClearance, opts).Should().Be((24 * 60, 24 * 60));
        SlaTracker.ResolveTierBudget(QueueTier.Exception, opts).Should().Be((0, 0));
    }

    [Fact]
    public void ResolveTierBudget_picks_overrides_when_present()
    {
        var opts = new SlaTrackerOptions();
        opts.TierOverrides[QueueTier.Urgent] = (2, 12);
        SlaTracker.ResolveTierBudget(QueueTier.Urgent, opts).Should().Be((2, 12));
        // Standard still pulls hard default.
        SlaTracker.ResolveTierBudget(QueueTier.Standard, opts).Should().Be((15, 60));
    }
}
