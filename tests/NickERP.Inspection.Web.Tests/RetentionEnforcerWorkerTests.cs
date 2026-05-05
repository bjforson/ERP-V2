using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Workers;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Core.Retention;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;
using NickERP.Platform.Tenancy.Features;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 44 / Phase D — coverage for
/// <see cref="RetentionEnforcerWorker"/>: per-tenant fan-out + the
/// eligibility predicate (LegalHold trump-card, Standard/Extended
/// only, ClosedAt past cutoff). Asserts the worker SURFACES candidates
/// (does not delete) + emits one audit event per tenant per tick when
/// candidates &gt; 0.
/// </summary>
public sealed class RetentionEnforcerWorkerTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly RecordingEventPublisher _events = new();
    private readonly RetentionFakeTimeProvider _clock = new();

    public RetentionEnforcerWorkerTests()
    {
        var dbName = "s44-retention-worker-" + Guid.NewGuid();
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

        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<IEventPublisher>(_events);
        services.AddSingleton<ITenantSettingsService>(new InMemoryTenantSettingsService());

        services.AddScoped<RetentionService>();

        services.Configure<RetentionEnforcerOptions>(o =>
        {
            o.Enabled = true;
            o.PollInterval = TimeSpan.FromHours(6);
            o.StartupDelay = TimeSpan.Zero;
        });

        services.AddSingleton<RetentionEnforcerWorker>(sp => new RetentionEnforcerWorker(
            sp,
            sp.GetRequiredService<IOptions<RetentionEnforcerOptions>>(),
            NullLogger<RetentionEnforcerWorker>.Instance,
            _clock));

        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    public async Task NoActiveTenants_returns_zero()
    {
        var worker = _sp.GetRequiredService<RetentionEnforcerWorker>();
        var found = await InvokeAsync<int>(worker, "SweepOnceAsync");
        Assert.Equal(0, found);
        Assert.Empty(_events.Events);
    }

    [Fact]
    public async Task Tenant_with_no_eligible_cases_emits_no_audit()
    {
        await SeedTenantAsync(1, "t1");
        await SeedCaseAsync(1, RetentionClass.Standard, closedAt: null); // Open case never eligible.

        var worker = _sp.GetRequiredService<RetentionEnforcerWorker>();
        var found = await InvokeAsync<int>(worker, "SweepOnceAsync");
        Assert.Equal(0, found);
        Assert.DoesNotContain(_events.Events, e => e.EventType == "nickerp.inspection.retention_purge_candidates_found");
    }

    [Fact]
    public async Task Standard_case_past_cutoff_becomes_candidate()
    {
        await SeedTenantAsync(1, "t1");
        // Closed 6 years ago — past 1825-day Standard fallback.
        await SeedCaseAsync(1, RetentionClass.Standard, closedAt: _clock.UtcNow.AddYears(-6));

        var worker = _sp.GetRequiredService<RetentionEnforcerWorker>();
        var found = await InvokeAsync<int>(worker, "SweepOnceAsync");
        Assert.Equal(1, found);

        var evt = Assert.Single(_events.Events.Where(e => e.EventType == "nickerp.inspection.retention_purge_candidates_found"));
        Assert.Equal(1L, evt.TenantId);
        Assert.Equal(1, evt.Payload.GetProperty("candidateCount").GetInt32());
        Assert.Equal(1, evt.Payload.GetProperty("standardCount").GetInt32());
        Assert.Equal(0, evt.Payload.GetProperty("extendedCount").GetInt32());
    }

    [Fact]
    public async Task Standard_case_within_window_is_not_candidate()
    {
        await SeedTenantAsync(1, "t1");
        // Closed only 1 year ago — within 1825-day Standard fallback.
        await SeedCaseAsync(1, RetentionClass.Standard, closedAt: _clock.UtcNow.AddYears(-1));

        var worker = _sp.GetRequiredService<RetentionEnforcerWorker>();
        var found = await InvokeAsync<int>(worker, "SweepOnceAsync");
        Assert.Equal(0, found);
    }

    [Fact]
    public async Task LegalHold_trumps_retention_class_no_candidate()
    {
        await SeedTenantAsync(1, "t1");
        // Standard, closed 10 years ago (past cutoff), but LegalHold=true.
        await SeedCaseAsync(1, RetentionClass.Standard,
            closedAt: _clock.UtcNow.AddYears(-10), legalHold: true);

        var worker = _sp.GetRequiredService<RetentionEnforcerWorker>();
        var found = await InvokeAsync<int>(worker, "SweepOnceAsync");
        Assert.Equal(0, found);
        Assert.DoesNotContain(_events.Events, e => e.EventType == "nickerp.inspection.retention_purge_candidates_found");
    }

    [Fact]
    public async Task Enforcement_class_never_auto_purges()
    {
        await SeedTenantAsync(1, "t1");
        // Closed 20 years ago — way past any reasonable window.
        await SeedCaseAsync(1, RetentionClass.Enforcement, closedAt: _clock.UtcNow.AddYears(-20));

        var worker = _sp.GetRequiredService<RetentionEnforcerWorker>();
        var found = await InvokeAsync<int>(worker, "SweepOnceAsync");
        Assert.Equal(0, found);
    }

    [Fact]
    public async Task Training_class_never_auto_purges()
    {
        await SeedTenantAsync(1, "t1");
        await SeedCaseAsync(1, RetentionClass.Training, closedAt: _clock.UtcNow.AddYears(-20));

        var worker = _sp.GetRequiredService<RetentionEnforcerWorker>();
        var found = await InvokeAsync<int>(worker, "SweepOnceAsync");
        Assert.Equal(0, found);
    }

    [Fact]
    public async Task Extended_case_uses_2555_day_fallback()
    {
        await SeedTenantAsync(1, "t1");
        // Closed 8 years ago — past 2555-day (7y) Extended fallback.
        await SeedCaseAsync(1, RetentionClass.Extended, closedAt: _clock.UtcNow.AddYears(-8));
        // Closed 5 years ago — within window.
        await SeedCaseAsync(1, RetentionClass.Extended, closedAt: _clock.UtcNow.AddYears(-5));

        var worker = _sp.GetRequiredService<RetentionEnforcerWorker>();
        var found = await InvokeAsync<int>(worker, "SweepOnceAsync");
        Assert.Equal(1, found);

        var evt = Assert.Single(_events.Events.Where(e => e.EventType == "nickerp.inspection.retention_purge_candidates_found"));
        Assert.Equal(0, evt.Payload.GetProperty("standardCount").GetInt32());
        Assert.Equal(1, evt.Payload.GetProperty("extendedCount").GetInt32());
    }

    [Fact]
    public async Task Multi_tenant_fan_out_emits_one_audit_per_tenant()
    {
        await SeedTenantAsync(1, "t1");
        await SeedTenantAsync(2, "t2");
        await SeedCaseAsync(1, RetentionClass.Standard, closedAt: _clock.UtcNow.AddYears(-6));
        await SeedCaseAsync(2, RetentionClass.Standard, closedAt: _clock.UtcNow.AddYears(-6));

        var worker = _sp.GetRequiredService<RetentionEnforcerWorker>();
        var found = await InvokeAsync<int>(worker, "SweepOnceAsync");
        Assert.Equal(2, found);

        var audit = _events.Events.Where(e => e.EventType == "nickerp.inspection.retention_purge_candidates_found").ToList();
        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, e => e.TenantId == 1);
        Assert.Contains(audit, e => e.TenantId == 2);
    }

    [Fact]
    public async Task Open_case_never_a_candidate_regardless_of_age()
    {
        await SeedTenantAsync(1, "t1");
        // Standard class but ClosedAt is null (still open).
        await SeedCaseAsync(1, RetentionClass.Standard, closedAt: null);

        var worker = _sp.GetRequiredService<RetentionEnforcerWorker>();
        var found = await InvokeAsync<int>(worker, "SweepOnceAsync");
        Assert.Equal(0, found);
    }

    [Fact]
    public async Task Tenant_setting_override_shrinks_window()
    {
        await SeedTenantAsync(1, "t1");
        // Override to 30 days for Standard.
        var settings = (InMemoryTenantSettingsService)_sp.GetRequiredService<ITenantSettingsService>();
        settings.Set(1, RetentionPolicyDefaults.StandardDaysKey, "30");

        // Closed 60 days ago — past the override but within the fallback.
        await SeedCaseAsync(1, RetentionClass.Standard, closedAt: _clock.UtcNow.AddDays(-60));

        var worker = _sp.GetRequiredService<RetentionEnforcerWorker>();
        var found = await InvokeAsync<int>(worker, "SweepOnceAsync");
        Assert.Equal(1, found);
    }

    [Fact]
    public async Task Disabled_worker_ExecuteAsync_does_not_throw()
    {
        var services = new ServiceCollection();
        services.Configure<RetentionEnforcerOptions>(o => { o.Enabled = false; });
        services.AddSingleton<IEventPublisher>(new RecordingEventPublisher());
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        var sp = services.BuildServiceProvider();
        var worker = new RetentionEnforcerWorker(
            sp,
            sp.GetRequiredService<IOptions<RetentionEnforcerOptions>>(),
            NullLogger<RetentionEnforcerWorker>.Instance);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await worker.StopAsync(cts.Token);
    }

    [Fact]
    public void WorkerName_and_state_are_exposed()
    {
        var worker = _sp.GetRequiredService<RetentionEnforcerWorker>();
        Assert.False(string.IsNullOrWhiteSpace(worker.WorkerName));
        Assert.NotNull(worker.GetState());
    }

    [Fact]
    public void DefaultOptions_is_disabled()
    {
        Assert.False(new RetentionEnforcerOptions().Enabled);
    }

    [Fact]
    public void DefaultOptions_polls_every_six_hours()
    {
        Assert.Equal(TimeSpan.FromHours(6), new RetentionEnforcerOptions().PollInterval);
    }

    // ---------------------------------------------------------------
    // Seeding helpers
    // ---------------------------------------------------------------

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

    private async Task<Guid> SeedCaseAsync(
        long tenantId,
        RetentionClass cls,
        DateTimeOffset? closedAt = null,
        bool legalHold = false)
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
            State = closedAt.HasValue ? InspectionWorkflowState.Closed : InspectionWorkflowState.Open,
            OpenedAt = closedAt?.AddDays(-7) ?? _clock.UtcNow,
            StateEnteredAt = closedAt ?? _clock.UtcNow,
            ClosedAt = closedAt,
            RetentionClass = cls,
            LegalHold = legalHold,
            LegalHoldAppliedAt = legalHold ? _clock.UtcNow : null,
            LegalHoldReason = legalHold ? "test" : null,
            TenantId = tenantId
        });
        await db.SaveChangesAsync();
        return caseId;
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
