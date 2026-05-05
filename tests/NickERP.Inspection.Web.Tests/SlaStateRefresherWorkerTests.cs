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
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 36 / FU-sla-state-refresher-worker — Phase D coverage for the
/// new periodic <see cref="SlaStateRefresherWorker"/>.
///
/// <para>
/// Asserts the worker's discovery-then-refresh shape: tenant
/// enumeration via <see cref="TenancyDbContext.Tenants"/>; per-tenant
/// scope flip; per-case <see cref="ISlaTracker.RefreshStatesAsync"/>
/// invocation. Uses a deterministic
/// <see cref="SlaRefresherFakeTimeProvider"/> so AtRisk / Breached
/// computations are reproducible.
/// </para>
/// </summary>
public sealed class SlaStateRefresherWorkerTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly long _tenantId = 1;
    private readonly RecordingEventPublisher _events = new();
    private readonly SlaRefresherFakeTimeProvider _clock = new();

    public SlaStateRefresherWorkerTests()
    {
        var dbName = "s36-sla-refresher-" + Guid.NewGuid();
        var services = new ServiceCollection();

        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddDbContext<TenancyDbContext>(o =>
            o.UseInMemoryDatabase("tenancy-" + dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        services.AddScoped<ITenantContext, TenantContext>();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        // SLA tracker — production wiring through DI but no DB-level
        // settings provider needed because the worker only calls
        // RefreshStatesAsync (which doesn't consult settings).
        services.AddSingleton<ISlaSettingsProvider, NoOpSlaSettingsProvider>();
        services.AddScoped<SlaTracker>();
        services.AddScoped<ISlaTracker>(sp => sp.GetRequiredService<SlaTracker>());
        services.Configure<SlaTrackerOptions>(o =>
        {
            o.AtRiskFraction = 0.5;
            o.FallbackBudgetMinutes = 60;
            o.DefaultBudgets = new(StringComparer.OrdinalIgnoreCase)
            {
                ["test.window"] = 60
            };
        });

        services.AddSingleton<IEventPublisher>(_events);

        services.Configure<SlaStateRefresherOptions>(o =>
        {
            o.Enabled = true;
            o.PollInterval = TimeSpan.FromSeconds(1);
            o.StartupDelay = TimeSpan.Zero;
        });

        services.AddSingleton<SlaStateRefresherWorker>(sp => new SlaStateRefresherWorker(
            sp,
            sp.GetRequiredService<IOptions<SlaStateRefresherOptions>>(),
            NullLogger<SlaStateRefresherWorker>.Instance,
            _clock));

        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    public async Task NoActiveTenants_returns_zero()
    {
        var worker = _sp.GetRequiredService<SlaStateRefresherWorker>();
        var refreshed = await InvokeAsync<int>(worker, "RefreshOnceAsync");
        Assert.Equal(0, refreshed);
    }

    [Fact]
    public async Task Tenant_with_no_open_windows_is_skipped_silently()
    {
        await SeedTenantAsync();
        var worker = _sp.GetRequiredService<SlaStateRefresherWorker>();
        var refreshed = await InvokeAsync<int>(worker, "RefreshOnceAsync");
        Assert.Equal(0, refreshed);
        Assert.DoesNotContain(_events.Events, e => e.EventType == "inspection.sla.state_refreshed");
    }

    [Fact]
    public async Task Open_window_within_budget_stays_OnTime_no_audit()
    {
        await SeedTenantAsync();
        var caseId = await SeedCaseAsync();

        // 60-minute budget, 10 minutes elapsed → OnTime (below 50% AtRisk).
        await SeedSlaWindowAsync(caseId, "test.window", openedAt: _clock.UtcNow.AddMinutes(-10), budget: 60);

        var worker = _sp.GetRequiredService<SlaStateRefresherWorker>();
        var refreshed = await InvokeAsync<int>(worker, "RefreshOnceAsync");
        Assert.Equal(0, refreshed); // no flips

        Assert.DoesNotContain(_events.Events, e => e.EventType == "inspection.sla.state_refreshed");
    }

    [Fact]
    public async Task Window_past_AtRisk_threshold_flips_and_emits_audit()
    {
        await SeedTenantAsync();
        var caseId = await SeedCaseAsync();

        // Opened 40 minutes ago against a 60-minute budget. 40/60 ≈ 67%
        // > 50% AtRiskFraction so the refresher flips OnTime → AtRisk.
        await SeedSlaWindowAsync(caseId, "test.window", openedAt: _clock.UtcNow.AddMinutes(-40), budget: 60);

        var worker = _sp.GetRequiredService<SlaStateRefresherWorker>();
        var refreshed = await InvokeAsync<int>(worker, "RefreshOnceAsync");

        Assert.Equal(1, refreshed);

        // Confirm DB row reflects the flip.
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(_tenantId);
        var window = await db.Set<SlaWindow>().AsNoTracking().FirstAsync();
        Assert.Equal(SlaWindowState.AtRisk, window.State);

        // Audit event fires once.
        var auditEvents = _events.Events.Where(e => e.EventType == "inspection.sla.state_refreshed").ToList();
        Assert.Single(auditEvents);
        Assert.Equal(1, auditEvents[0].Payload.GetProperty("windowsFlipped").GetInt32());
    }

    [Fact]
    public async Task Window_past_DueAt_flips_to_Breached()
    {
        await SeedTenantAsync();
        var caseId = await SeedCaseAsync();

        // Opened 90 minutes ago against a 60-minute budget — 30 minutes past due.
        await SeedSlaWindowAsync(caseId, "test.window", openedAt: _clock.UtcNow.AddMinutes(-90), budget: 60);

        var worker = _sp.GetRequiredService<SlaStateRefresherWorker>();
        var refreshed = await InvokeAsync<int>(worker, "RefreshOnceAsync");

        Assert.Equal(1, refreshed);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(_tenantId);
        var window = await db.Set<SlaWindow>().AsNoTracking().FirstAsync();
        Assert.Equal(SlaWindowState.Breached, window.State);
    }

    [Fact]
    public async Task Closed_windows_are_not_touched()
    {
        await SeedTenantAsync();
        var caseId = await SeedCaseAsync();

        // Already-closed window — refresher must not touch.
        await SeedSlaWindowAsync(caseId, "test.window", openedAt: _clock.UtcNow.AddMinutes(-90),
            budget: 60, closedAt: _clock.UtcNow.AddMinutes(-50));

        var worker = _sp.GetRequiredService<SlaStateRefresherWorker>();
        var refreshed = await InvokeAsync<int>(worker, "RefreshOnceAsync");
        Assert.Equal(0, refreshed);
    }

    [Fact]
    public async Task Multiple_tenants_each_get_per_tenant_audit()
    {
        // Seed two tenants, each with one breached open window.
        await SeedTenantAsync(1, "t1");
        await SeedTenantAsync(2, "t2");
        var caseA = await SeedCaseAsync(tenantId: 1);
        var caseB = await SeedCaseAsync(tenantId: 2);
        await SeedSlaWindowAsync(caseA, "test.window", _clock.UtcNow.AddMinutes(-90), budget: 60, tenantId: 1);
        await SeedSlaWindowAsync(caseB, "test.window", _clock.UtcNow.AddMinutes(-90), budget: 60, tenantId: 2);

        var worker = _sp.GetRequiredService<SlaStateRefresherWorker>();
        var refreshed = await InvokeAsync<int>(worker, "RefreshOnceAsync");
        Assert.Equal(2, refreshed);

        var auditEvents = _events.Events.Where(e => e.EventType == "inspection.sla.state_refreshed").ToList();
        Assert.Equal(2, auditEvents.Count);
        Assert.Contains(auditEvents, e => e.TenantId == 1);
        Assert.Contains(auditEvents, e => e.TenantId == 2);
    }

    [Fact]
    public async Task Idempotent_run_no_double_flip_no_double_audit()
    {
        await SeedTenantAsync();
        var caseId = await SeedCaseAsync();
        await SeedSlaWindowAsync(caseId, "test.window", _clock.UtcNow.AddMinutes(-90), budget: 60);

        var worker = _sp.GetRequiredService<SlaStateRefresherWorker>();
        var first = await InvokeAsync<int>(worker, "RefreshOnceAsync");
        Assert.Equal(1, first); // first run flips

        var second = await InvokeAsync<int>(worker, "RefreshOnceAsync");
        Assert.Equal(0, second); // already Breached — no flip

        // Audit event only fires once.
        var audit = _events.Events.Count(e => e.EventType == "inspection.sla.state_refreshed");
        Assert.Equal(1, audit);
    }

    [Fact]
    public async Task Disabled_worker_ExecuteAsync_does_not_throw()
    {
        // Build a fresh SP with the worker disabled.
        var services = new ServiceCollection();
        services.Configure<SlaStateRefresherOptions>(o => { o.Enabled = false; });
        services.AddSingleton<IEventPublisher>(new RecordingEventPublisher());
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        var sp = services.BuildServiceProvider();
        var worker = new SlaStateRefresherWorker(
            sp,
            sp.GetRequiredService<IOptions<SlaStateRefresherOptions>>(),
            NullLogger<SlaStateRefresherWorker>.Instance);

        // StartAsync → ExecuteAsync should return immediately when disabled.
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await worker.StopAsync(cts.Token);
    }

    [Fact]
    public void WorkerName_and_state_are_exposed()
    {
        var worker = _sp.GetRequiredService<SlaStateRefresherWorker>();
        Assert.False(string.IsNullOrWhiteSpace(worker.WorkerName));
        Assert.NotNull(worker.GetState());
    }

    [Fact]
    public void DefaultOptions_is_disabled()
    {
        // OOTB Sprint-36 default — Enabled = false (matches Sprint 24
        // architectural decision for new B3-style workers).
        Assert.False(new SlaStateRefresherOptions().Enabled);
    }

    // ---------------------------------------------------------------
    // Seeding helpers
    // ---------------------------------------------------------------

    private Task SeedTenantAsync() => SeedTenantAsync(_tenantId, "t1");

    private async Task SeedTenantAsync(long tenantId, string code)
    {
        using var scope = _sp.CreateScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        if (await tenancy.Tenants.AnyAsync(t => t.Id == tenantId)) return;
        tenancy.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Code = code,
            Name = "Tenant " + code,
            State = TenantState.Active,
            BillingPlan = "internal",
            TimeZone = "UTC",
            Locale = "en",
            Currency = "USD",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await tenancy.SaveChangesAsync();
    }

    private async Task<Guid> SeedCaseAsync(long tenantId = 1)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(tenantId);
        var caseId = Guid.NewGuid();
        db.Cases.Add(new InspectionCase
        {
            Id = caseId,
            LocationId = Guid.NewGuid(),
            SubjectType = CaseSubjectType.Container,
            SubjectIdentifier = "X-" + caseId.ToString("N")[..6],
            SubjectPayloadJson = "{}",
            State = InspectionWorkflowState.Open,
            OpenedAt = _clock.UtcNow,
            StateEnteredAt = _clock.UtcNow,
            TenantId = tenantId
        });
        await db.SaveChangesAsync();
        return caseId;
    }

    private async Task SeedSlaWindowAsync(
        Guid caseId, string windowName,
        DateTimeOffset openedAt, int budget,
        DateTimeOffset? closedAt = null,
        long tenantId = 1)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(tenantId);
        db.Set<SlaWindow>().Add(new SlaWindow
        {
            Id = Guid.NewGuid(),
            CaseId = caseId,
            WindowName = windowName,
            StartedAt = openedAt,
            DueAt = openedAt.AddMinutes(budget),
            BudgetMinutes = budget,
            ClosedAt = closedAt,
            State = SlaWindowState.OnTime,
            TenantId = tenantId
        });
        await db.SaveChangesAsync();
    }

    private static async Task<T> InvokeAsync<T>(object target, string methodName)
    {
        var method = target.GetType().GetMethod(
            methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task<T>)method!.Invoke(target, new object[] { CancellationToken.None })!;
        return await task;
    }
}

/// <summary>
/// Test-time TimeProvider that exposes UtcNow as a settable property
/// so tests can drive AtRisk / Breached transitions deterministically.
/// </summary>
internal sealed class SlaRefresherFakeTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

/// <summary>
/// No-op SLA settings provider — the refresher worker doesn't consult
/// settings (only RefreshStatesAsync), but the SlaTracker DI graph
/// requires an ISlaSettingsProvider to construct.
/// </summary>
internal sealed class NoOpSlaSettingsProvider : ISlaSettingsProvider
{
    public Task<IReadOnlyDictionary<string, SlaSettingSnapshot>> GetSettingsAsync(
        long tenantId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, SlaSettingSnapshot> empty =
            new Dictionary<string, SlaSettingSnapshot>();
        return Task.FromResult(empty);
    }
}
