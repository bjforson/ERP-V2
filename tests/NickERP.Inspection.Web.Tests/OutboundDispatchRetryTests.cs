using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Workers;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Plugins;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 36 / FU-outbound-dispatch-retry — Phase D coverage for the
/// bounded retry budget + exponential backoff path on
/// <see cref="OutboundSubmissionDispatchWorker"/>.
///
/// <para>
/// Distinct from the existing Sprint 24 fixture so the test seam is
/// clear: this fixture wires only the dispatch worker + a
/// <see cref="RecordingExternalSystemAdapter"/> and exercises the
/// failure path (transient throw → retry, exhaust budget → error).
/// Uses a deterministic <see cref="FakeTimeProvider"/> so backoff
/// scheduling is reproducible.
/// </para>
/// </summary>
public sealed class OutboundDispatchRetryTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly long _tenantId = 1;
    private readonly Guid _locationId = Guid.NewGuid();
    private readonly Guid _externalSystemInstanceId = Guid.NewGuid();
    private readonly RecordingExternalSystemAdapter _esAdapter = new();
    private readonly RecordingEventPublisher _events = new();
    private readonly RetryFakeTimeProvider _clock = new();

    public OutboundDispatchRetryTests()
    {
        var dbName = "s36-retry-" + Guid.NewGuid();
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

        services.AddSingleton(_esAdapter);
        services.AddSingleton<IPluginRegistry>(new RetrySinglePluginRegistry(_esAdapter));
        services.AddSingleton<IEventPublisher>(_events);

        services.Configure<IcumsSubmissionDispatchOptions>(o =>
        {
            o.Enabled = true;
            o.PollInterval = TimeSpan.FromSeconds(1);
            o.StartupDelay = TimeSpan.Zero;
            o.BatchLimit = 50;
        });
        // Tight retry options for fast tests.
        services.Configure<OutboundSubmissionRetryOptions>(o =>
        {
            o.MaxRetries = 3;
            o.BaseBackoff = TimeSpan.FromSeconds(10);
            o.MaxBackoff = TimeSpan.FromMinutes(5);
        });

        services.AddSingleton<OutboundSubmissionDispatchWorker>(sp => new OutboundSubmissionDispatchWorker(
            sp,
            sp.GetRequiredService<IOptions<IcumsSubmissionDispatchOptions>>(),
            sp.GetRequiredService<IOptions<OutboundSubmissionRetryOptions>>(),
            NullLogger<OutboundSubmissionDispatchWorker>.Instance,
            _clock));

        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    public async Task Transient_failure_increments_retry_count_and_schedules_backoff()
    {
        await SeedTenantAndCaseAsync();
        var subId = await SeedSubmissionAsync("idem-retry-1");

        _esAdapter.ShouldThrowOnSubmit = true;

        var worker = _sp.GetRequiredService<OutboundSubmissionDispatchWorker>();
        await InvokeAsync<int>(worker, "DispatchOnceAsync");

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var sub = await db.OutboundSubmissions.AsNoTracking().FirstAsync(s => s.Id == subId);

        Assert.Equal("pending", sub.Status);
        Assert.Equal(1, sub.RetryCount);
        Assert.NotNull(sub.NextAttemptAt);
        Assert.True(sub.NextAttemptAt > _clock.GetUtcNow());
        Assert.NotNull(sub.LastAttemptAt);
        Assert.NotNull(sub.ErrorMessage);
    }

    [Fact]
    public async Task Backoff_window_blocks_pickup_until_elapsed()
    {
        await SeedTenantAndCaseAsync();
        var subId = await SeedSubmissionAsync("idem-retry-backoff");

        _esAdapter.ShouldThrowOnSubmit = true;
        var worker = _sp.GetRequiredService<OutboundSubmissionDispatchWorker>();

        // First attempt — schedules backoff.
        var first = await InvokeAsync<int>(worker, "DispatchOnceAsync");
        Assert.Equal(1, first);

        // Reset adapter to succeed on retry.
        _esAdapter.ShouldThrowOnSubmit = false;
        _esAdapter.NextSubmissionResult = new SubmissionResult(true, "{\"ok\":true}", null);

        // Run again WITHOUT advancing the clock — pickup query should
        // skip the row because NextAttemptAt is in the future.
        var blockedDispatched = await InvokeAsync<int>(worker, "DispatchOnceAsync");
        Assert.Equal(0, blockedDispatched);

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var sub = await db.OutboundSubmissions.AsNoTracking().FirstAsync(s => s.Id == subId);
            Assert.Equal("pending", sub.Status);
            Assert.Equal(1, sub.RetryCount); // still 1 — never retried
        }

        // Advance clock past the backoff window.
        _clock.UtcNow = _clock.UtcNow.AddHours(2);

        // Now the row is eligible.
        var unblockedDispatched = await InvokeAsync<int>(worker, "DispatchOnceAsync");
        Assert.Equal(1, unblockedDispatched);

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var sub = await db.OutboundSubmissions.AsNoTracking().FirstAsync(s => s.Id == subId);
            Assert.Equal("accepted", sub.Status);
            // Successful retry clears NextAttemptAt.
            Assert.Null(sub.NextAttemptAt);
        }
    }

    [Fact]
    public async Task Exhausting_retry_budget_flips_to_error()
    {
        await SeedTenantAndCaseAsync();
        var subId = await SeedSubmissionAsync("idem-retry-exhaust");

        _esAdapter.ShouldThrowOnSubmit = true;
        var worker = _sp.GetRequiredService<OutboundSubmissionDispatchWorker>();

        // MaxRetries = 3, so attempts 1..3 stay pending; attempt 4 flips
        // to error. Each iteration we advance the clock past the backoff
        // window so the row is eligible for the next attempt.
        for (int i = 0; i < 4; i++)
        {
            _clock.UtcNow = _clock.UtcNow.AddHours(2);
            await InvokeAsync<int>(worker, "DispatchOnceAsync");
        }

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var sub = await db.OutboundSubmissions.AsNoTracking().FirstAsync(s => s.Id == subId);

        Assert.Equal("error", sub.Status);
        Assert.Equal(4, sub.RetryCount);
        // Error path clears the backoff window — operator requeue is
        // the next state transition.
        Assert.Null(sub.NextAttemptAt);
    }

    [Fact]
    public async Task Audit_event_retry_scheduled_emits_for_each_failure()
    {
        await SeedTenantAndCaseAsync();
        await SeedSubmissionAsync("idem-retry-audit");

        _esAdapter.ShouldThrowOnSubmit = true;
        var worker = _sp.GetRequiredService<OutboundSubmissionDispatchWorker>();

        // Fail twice (within budget) so we get two retry_scheduled events.
        await InvokeAsync<int>(worker, "DispatchOnceAsync");
        _clock.UtcNow = _clock.UtcNow.AddHours(2);
        await InvokeAsync<int>(worker, "DispatchOnceAsync");

        var retryEvents = _events.Events
            .Where(e => e.EventType == "nickerp.icums.submission.retry_scheduled")
            .ToList();
        Assert.Equal(2, retryEvents.Count);
        // Audit payload carries the increasing retry count.
        Assert.Contains(retryEvents, e => e.Payload.GetProperty("retryCount").GetInt32() == 1);
        Assert.Contains(retryEvents, e => e.Payload.GetProperty("retryCount").GetInt32() == 2);
    }

    [Fact]
    public async Task Audit_event_retry_exhausted_emits_on_budget_burnout()
    {
        await SeedTenantAndCaseAsync();
        await SeedSubmissionAsync("idem-retry-burnout");

        _esAdapter.ShouldThrowOnSubmit = true;
        var worker = _sp.GetRequiredService<OutboundSubmissionDispatchWorker>();

        // 4 attempts → first 3 retry_scheduled, 4th retry_exhausted.
        for (int i = 0; i < 4; i++)
        {
            _clock.UtcNow = _clock.UtcNow.AddHours(2);
            await InvokeAsync<int>(worker, "DispatchOnceAsync");
        }

        var scheduled = _events.Events.Count(e => e.EventType == "nickerp.icums.submission.retry_scheduled");
        var exhausted = _events.Events.Count(e => e.EventType == "nickerp.icums.submission.retry_exhausted");
        Assert.Equal(3, scheduled);
        Assert.Equal(1, exhausted);
    }

    [Fact]
    public async Task Successful_dispatch_clears_NextAttemptAt()
    {
        await SeedTenantAndCaseAsync();
        var subId = await SeedSubmissionAsync("idem-success-clears");

        // Pre-seed NextAttemptAt to simulate a prior backoff window.
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            tenant.SetTenant(_tenantId);
            var sub = await db.OutboundSubmissions.FirstAsync(s => s.Id == subId);
            sub.RetryCount = 2;
            sub.NextAttemptAt = _clock.GetUtcNow().AddHours(-1); // already elapsed
            await db.SaveChangesAsync();
        }

        _esAdapter.NextSubmissionResult = new SubmissionResult(true, "{}", null);

        var worker = _sp.GetRequiredService<OutboundSubmissionDispatchWorker>();
        await InvokeAsync<int>(worker, "DispatchOnceAsync");

        using var scope2 = _sp.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var refreshed = await db2.OutboundSubmissions.AsNoTracking().FirstAsync(s => s.Id == subId);
        Assert.Equal("accepted", refreshed.Status);
        Assert.Null(refreshed.NextAttemptAt);
    }

    [Fact]
    public async Task Rejection_clears_NextAttemptAt_too()
    {
        await SeedTenantAndCaseAsync();
        var subId = await SeedSubmissionAsync("idem-reject-clears");

        // Pre-seed NextAttemptAt elapsed.
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            tenant.SetTenant(_tenantId);
            var sub = await db.OutboundSubmissions.FirstAsync(s => s.Id == subId);
            sub.NextAttemptAt = _clock.GetUtcNow().AddHours(-1);
            await db.SaveChangesAsync();
        }

        _esAdapter.NextSubmissionResult = new SubmissionResult(false, null, "rejected by authority");
        var worker = _sp.GetRequiredService<OutboundSubmissionDispatchWorker>();
        await InvokeAsync<int>(worker, "DispatchOnceAsync");

        using var scope2 = _sp.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var refreshed = await db2.OutboundSubmissions.AsNoTracking().FirstAsync(s => s.Id == subId);
        Assert.Equal("rejected", refreshed.Status);
        Assert.Null(refreshed.NextAttemptAt);
    }

    [Fact]
    public void ComputeBackoff_grows_exponentially_then_caps()
    {
        var worker = _sp.GetRequiredService<OutboundSubmissionDispatchWorker>();
        var opts = new OutboundSubmissionRetryOptions
        {
            BaseBackoff = TimeSpan.FromSeconds(10),
            MaxBackoff = TimeSpan.FromMinutes(5),
            MaxRetries = 5
        };

        // 10s * 2^1 = 20s; jittered ±25% → [15s, 25s)
        var b1 = InvokeComputeBackoff(worker, opts, 1);
        Assert.True(b1 >= TimeSpan.FromSeconds(15) && b1 < TimeSpan.FromSeconds(25));

        // 10s * 2^2 = 40s; jittered → [30s, 50s)
        var b2 = InvokeComputeBackoff(worker, opts, 2);
        Assert.True(b2 >= TimeSpan.FromSeconds(30) && b2 < TimeSpan.FromSeconds(50));

        // 10s * 2^10 = ~170 minutes — exceeds MaxBackoff = 5 min.
        // Capped at MaxBackoff before jitter; jittered ±25% → [3.75min, 6.25min).
        var bCapped = InvokeComputeBackoff(worker, opts, 10);
        Assert.True(bCapped <= TimeSpan.FromMinutes(6.25));
    }

    private static TimeSpan InvokeComputeBackoff(
        OutboundSubmissionDispatchWorker worker,
        OutboundSubmissionRetryOptions opts,
        int retryCount)
    {
        var method = worker.GetType().GetMethod(
            "ComputeBackoff",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (TimeSpan)method.Invoke(worker, new object[] { opts, retryCount })!;
    }

    [Fact]
    public async Task Pickup_filter_includes_rows_with_null_NextAttemptAt()
    {
        await SeedTenantAndCaseAsync();
        var subId = await SeedSubmissionAsync("idem-null-next");

        // Confirm initial NextAttemptAt is null on a fresh row.
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var sub = await db.OutboundSubmissions.AsNoTracking().FirstAsync(s => s.Id == subId);
            Assert.Null(sub.NextAttemptAt);
        }

        _esAdapter.NextSubmissionResult = new SubmissionResult(true, "{}", null);
        var worker = _sp.GetRequiredService<OutboundSubmissionDispatchWorker>();
        var dispatched = await InvokeAsync<int>(worker, "DispatchOnceAsync");

        Assert.Equal(1, dispatched);
    }

    // ---------------------------------------------------------------
    // Seeding helpers
    // ---------------------------------------------------------------

    private async Task SeedTenantAndCaseAsync()
    {
        using var scope = _sp.CreateScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        if (!await tenancy.Tenants.AnyAsync(t => t.Id == _tenantId))
        {
            tenancy.Tenants.Add(new Tenant
            {
                Id = _tenantId,
                Code = "t1",
                Name = "Test Tenant",
                State = TenantState.Active,
                BillingPlan = "internal",
                TimeZone = "UTC",
                Locale = "en",
                Currency = "USD",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await tenancy.SaveChangesAsync();
        }

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(_tenantId);

        if (!await db.Locations.AnyAsync(l => l.Id == _locationId))
        {
            db.Locations.Add(new Location
            {
                Id = _locationId, Code = "loc", Name = "Loc", TimeZone = "UTC",
                IsActive = true, TenantId = _tenantId
            });
            await db.SaveChangesAsync();
        }

        if (!await db.ExternalSystemInstances.AnyAsync(e => e.Id == _externalSystemInstanceId))
        {
            db.ExternalSystemInstances.Add(new ExternalSystemInstance
            {
                Id = _externalSystemInstanceId,
                TypeCode = "test-authority",
                DisplayName = "Test Authority",
                Description = "Test stub",
                Scope = ExternalSystemBindingScope.Shared,
                ConfigJson = "{}",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                TenantId = _tenantId
            });
            await db.SaveChangesAsync();
        }
    }

    private async Task<Guid> SeedSubmissionAsync(string idempotencyKey)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(_tenantId);

        var caseId = Guid.NewGuid();
        db.Cases.Add(new InspectionCase
        {
            Id = caseId, LocationId = _locationId,
            SubjectType = CaseSubjectType.Container,
            SubjectIdentifier = "C-" + idempotencyKey,
            SubjectPayloadJson = "{}",
            State = InspectionWorkflowState.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = _tenantId
        });

        var subId = Guid.NewGuid();
        db.OutboundSubmissions.Add(new OutboundSubmission
        {
            Id = subId,
            CaseId = caseId,
            ExternalSystemInstanceId = _externalSystemInstanceId,
            PayloadJson = "{}",
            IdempotencyKey = idempotencyKey,
            Status = "pending",
            SubmittedAt = _clock.GetUtcNow(),
            TenantId = _tenantId
        });
        await db.SaveChangesAsync();
        return subId;
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
/// Minimal time provider whose UtcNow can be set imperatively. Avoids
/// the Microsoft.Extensions.TimeProvider.Testing dependency for a
/// single-test fixture.
/// </summary>
internal sealed class RetryFakeTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

/// <summary>
/// Single-plugin registry resolving only the test ExternalSystem stub.
/// </summary>
internal sealed class RetrySinglePluginRegistry : IPluginRegistry
{
    private readonly RecordingExternalSystemAdapter _es;
    public RetrySinglePluginRegistry(RecordingExternalSystemAdapter es) => _es = es;

    public IReadOnlyList<RegisteredPlugin> All { get; } = Array.Empty<RegisteredPlugin>();
    public IReadOnlyList<RegisteredPlugin> ForContract(Type contractType) => Array.Empty<RegisteredPlugin>();
    public RegisteredPlugin? FindByTypeCode(string module, string typeCode) => null;

    public T Resolve<T>(string module, string typeCode, IServiceProvider services) where T : class
    {
        if (typeof(T) == typeof(IExternalSystemAdapter)
            && string.Equals(typeCode, "test-authority", StringComparison.OrdinalIgnoreCase))
        {
            return (T)(object)_es;
        }
        throw new KeyNotFoundException($"no plugin '{typeCode}' for type {typeof(T).Name}");
    }
}
