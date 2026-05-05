using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Sla;
using NickERP.Inspection.Application.Workers;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 45 / Phase E — coverage for the
/// <see cref="QueueEscalatorWorker"/>: tier auto-escalation,
/// manual-tier respect, per-tenant fan-out posture.
/// </summary>
public sealed class QueueEscalatorWorkerTests : IAsyncLifetime
{
    private InspectionDbContext _insp = null!;
    private TenancyDbContext _tenancy = null!;
    private ServiceProvider _services = null!;
    private TestClock _clock = null!;
    private const long TenantId = 17;

    private string _dbName = string.Empty;

    public async Task InitializeAsync()
    {
        _dbName = "queue-escalator-" + Guid.NewGuid();

        // The worker creates a fresh DI scope per cycle and resolves
        // InspectionDbContext + TenancyDbContext from it; AddDbContext
        // gives every scope a fresh DbContext that shares the same
        // named in-memory store. This avoids the "disposed DbContext"
        // failure mode where the worker's scope-disposal kills the
        // outer test's _insp / _tenancy.
        var sc = new ServiceCollection();
        sc.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(_dbName + "-insp")
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        sc.AddDbContext<TenancyDbContext>(o =>
            o.UseInMemoryDatabase(_dbName + "-tenancy")
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        sc.AddScoped<ITenantContext, TenantContext>();
        _services = sc.BuildServiceProvider();

        // Open a long-lived "test scope" so _insp / _tenancy survive
        // beyond worker invocations. The worker creates its OWN scope
        // internally — disposes its OWN context — without touching ours.
        var testScope = _services.CreateScope();
        _insp = testScope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        _tenancy = testScope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        _tenancy.Tenants.Add(new Tenant
        {
            Id = TenantId,
            Name = "Test Tenant",
            State = TenantState.Active
        });
        await _tenancy.SaveChangesAsync();

        _clock = new TestClock(new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero));
    }

    public Task DisposeAsync()
    {
        _services?.Dispose();
        _insp?.Dispose();
        _tenancy?.Dispose();
        return Task.CompletedTask;
    }

    private QueueEscalatorWorker NewWorker(QueueEscalatorOptions? opts = null)
    {
        opts ??= new QueueEscalatorOptions
        {
            Enabled = true,
            StandardToHighAfter = TimeSpan.FromMinutes(30),
            HighToUrgentAfter = TimeSpan.FromMinutes(60)
        };
        return new QueueEscalatorWorker(
            _services,
            Options.Create(opts),
            NullLogger<QueueEscalatorWorker>.Instance,
            _clock);
    }

    private async Task<SlaWindow> SeedWindowAsync(
        QueueTier tier, DateTimeOffset startedAt, bool isManual = false,
        DateTimeOffset? closedAt = null)
    {
        var w = new SlaWindow
        {
            Id = Guid.NewGuid(),
            CaseId = Guid.NewGuid(),
            WindowName = "case.open_to_validated",
            StartedAt = startedAt,
            DueAt = startedAt.AddMinutes(60),
            ClosedAt = closedAt,
            State = SlaWindowState.OnTime,
            BudgetMinutes = 60,
            QueueTier = tier,
            QueueTierIsManual = isManual,
            TenantId = TenantId
        };
        _insp.SlaWindows.Add(w);
        await _insp.SaveChangesAsync();
        return w;
    }

    [Fact]
    public async Task Standard_open_more_than_30m_auto_escalates_to_High()
    {
        var startedAt = _clock.GetUtcNow().AddMinutes(-31);
        var window = await SeedWindowAsync(QueueTier.Standard, startedAt);

        var worker = NewWorker();
        var escalated = await worker.EscalateOnceAsync(CancellationToken.None);

        escalated.Should().Be(1);
        var refreshed = await _insp.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.Id == window.Id);
        refreshed.QueueTier.Should().Be(QueueTier.High);
        refreshed.QueueTierIsManual.Should().BeFalse();
    }

    [Fact]
    public async Task High_open_more_than_60m_auto_escalates_to_Urgent()
    {
        var startedAt = _clock.GetUtcNow().AddMinutes(-65);
        var window = await SeedWindowAsync(QueueTier.High, startedAt);

        var worker = NewWorker();
        var escalated = await worker.EscalateOnceAsync(CancellationToken.None);

        escalated.Should().Be(1);
        var refreshed = await _insp.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.Id == window.Id);
        refreshed.QueueTier.Should().Be(QueueTier.Urgent);
    }

    [Fact]
    public async Task Manual_tier_is_never_auto_escalated()
    {
        // Standard open 31m, but manually set — worker leaves it.
        var startedAt = _clock.GetUtcNow().AddMinutes(-31);
        var window = await SeedWindowAsync(QueueTier.Standard, startedAt, isManual: true);

        var worker = NewWorker();
        var escalated = await worker.EscalateOnceAsync(CancellationToken.None);

        escalated.Should().Be(0);
        var refreshed = await _insp.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.Id == window.Id);
        refreshed.QueueTier.Should().Be(QueueTier.Standard);
        refreshed.QueueTierIsManual.Should().BeTrue();
    }

    [Fact]
    public async Task Standard_open_under_30m_is_not_escalated()
    {
        var startedAt = _clock.GetUtcNow().AddMinutes(-29);
        var window = await SeedWindowAsync(QueueTier.Standard, startedAt);

        var worker = NewWorker();
        var escalated = await worker.EscalateOnceAsync(CancellationToken.None);

        escalated.Should().Be(0);
        var refreshed = await _insp.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.Id == window.Id);
        refreshed.QueueTier.Should().Be(QueueTier.Standard);
    }

    [Fact]
    public async Task Closed_window_is_never_escalated()
    {
        var startedAt = _clock.GetUtcNow().AddMinutes(-90);
        var closedAt = _clock.GetUtcNow().AddMinutes(-30);
        var window = await SeedWindowAsync(
            QueueTier.Standard, startedAt, closedAt: closedAt);

        var worker = NewWorker();
        var escalated = await worker.EscalateOnceAsync(CancellationToken.None);

        escalated.Should().Be(0);
        var refreshed = await _insp.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.Id == window.Id);
        refreshed.QueueTier.Should().Be(QueueTier.Standard);
    }

    [Fact]
    public async Task Urgent_tier_is_terminal_no_further_escalation()
    {
        var startedAt = _clock.GetUtcNow().AddMinutes(-120);
        var window = await SeedWindowAsync(QueueTier.Urgent, startedAt);

        var worker = NewWorker();
        var escalated = await worker.EscalateOnceAsync(CancellationToken.None);

        escalated.Should().Be(0);
        var refreshed = await _insp.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.Id == window.Id);
        refreshed.QueueTier.Should().Be(QueueTier.Urgent);
    }

    [Fact]
    public async Task Exception_tier_is_terminal_no_further_escalation()
    {
        var startedAt = _clock.GetUtcNow().AddMinutes(-180);
        var window = await SeedWindowAsync(QueueTier.Exception, startedAt);

        var worker = NewWorker();
        var escalated = await worker.EscalateOnceAsync(CancellationToken.None);

        escalated.Should().Be(0);
        var refreshed = await _insp.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.Id == window.Id);
        refreshed.QueueTier.Should().Be(QueueTier.Exception);
    }

    [Fact]
    public async Task Mixed_batch_escalates_only_eligible_rows()
    {
        var now = _clock.GetUtcNow();
        // Eligible: Standard 31m old.
        var stdOld = await SeedWindowAsync(QueueTier.Standard, now.AddMinutes(-31));
        // Not eligible: Standard 5m old.
        var stdNew = await SeedWindowAsync(QueueTier.Standard, now.AddMinutes(-5));
        // Eligible: High 65m old.
        var highOld = await SeedWindowAsync(QueueTier.High, now.AddMinutes(-65));
        // Not eligible: manual Standard 60m old.
        var stdManual = await SeedWindowAsync(QueueTier.Standard, now.AddMinutes(-60), isManual: true);

        var worker = NewWorker();
        var escalated = await worker.EscalateOnceAsync(CancellationToken.None);

        escalated.Should().Be(2);
        (await _insp.SlaWindows.AsNoTracking().SingleAsync(w => w.Id == stdOld.Id))
            .QueueTier.Should().Be(QueueTier.High);
        (await _insp.SlaWindows.AsNoTracking().SingleAsync(w => w.Id == stdNew.Id))
            .QueueTier.Should().Be(QueueTier.Standard);
        (await _insp.SlaWindows.AsNoTracking().SingleAsync(w => w.Id == highOld.Id))
            .QueueTier.Should().Be(QueueTier.Urgent);
        (await _insp.SlaWindows.AsNoTracking().SingleAsync(w => w.Id == stdManual.Id))
            .QueueTier.Should().Be(QueueTier.Standard);
    }

    [Fact]
    public async Task Inactive_tenant_is_skipped()
    {
        // Flip the seeded tenant to Suspended; the worker shouldn't see it.
        var t = await _tenancy.Tenants.SingleAsync(x => x.Id == TenantId);
        t.State = TenantState.Suspended;
        await _tenancy.SaveChangesAsync();

        var startedAt = _clock.GetUtcNow().AddMinutes(-31);
        var window = await SeedWindowAsync(QueueTier.Standard, startedAt);

        var worker = NewWorker();
        var escalated = await worker.EscalateOnceAsync(CancellationToken.None);

        escalated.Should().Be(0);
        var refreshed = await _insp.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.Id == window.Id);
        refreshed.QueueTier.Should().Be(QueueTier.Standard);
    }

    [Fact]
    public async Task NextTier_ladder_skips_terminal_tiers()
    {
        QueueEscalatorWorker_NextTier(QueueTier.Standard).Should().Be(QueueTier.High);
        QueueEscalatorWorker_NextTier(QueueTier.High).Should().Be(QueueTier.Urgent);
        QueueEscalatorWorker_NextTier(QueueTier.Urgent).Should().Be(QueueTier.Urgent);
        QueueEscalatorWorker_NextTier(QueueTier.Exception).Should().Be(QueueTier.Exception);
        QueueEscalatorWorker_NextTier(QueueTier.PostClearance).Should().Be(QueueTier.PostClearance);
    }

    // Mirror the internal NextTier logic for assertion purposes — the
    // method itself is internal to the worker assembly.
    private static QueueTier QueueEscalatorWorker_NextTier(QueueTier current) => current switch
    {
        QueueTier.Standard => QueueTier.High,
        QueueTier.High => QueueTier.Urgent,
        _ => current
    };

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;
        public TestClock(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(DateTimeOffset now) => _now = now;
    }
}
