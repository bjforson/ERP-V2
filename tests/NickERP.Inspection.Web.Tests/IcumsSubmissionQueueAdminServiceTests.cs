using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Submissions;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 22 / B2.1 — covers the
/// <see cref="IcumsSubmissionQueueAdminService"/> filtered list, single
/// requeue, bulk requeue (with safety cap + status filter required), and
/// the payload-fetch shape consumed by the
/// <c>/admin/icums/submission-queue</c> Razor page.
/// </summary>
public sealed class IcumsSubmissionQueueAdminServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly long _tenantId = 1;
    private readonly RecordingEventPublisher _events = new();

    public IcumsSubmissionQueueAdminServiceTests()
    {
        var dbName = "subq-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });
        services.AddSingleton<IEventPublisher>(_events);
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddIcumsSubmissionQueueAdmin();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    private async Task<(Guid caseId, Guid esiId)> SeedFixtureAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var locId = Guid.NewGuid();
        db.Locations.Add(new Location
        {
            Id = locId,
            Code = "loc",
            Name = "Test",
            TimeZone = "Africa/Accra",
            IsActive = true,
            TenantId = _tenantId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var esiId = Guid.NewGuid();
        db.ExternalSystemInstances.Add(new ExternalSystemInstance
        {
            Id = esiId,
            TypeCode = "icums-gh",
            DisplayName = "ICUMS Tema",
            TenantId = _tenantId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var caseId = Guid.NewGuid();
        db.Cases.Add(new InspectionCase
        {
            Id = caseId,
            LocationId = locId,
            SubjectIdentifier = "MSCU1234567",
            SubjectType = CaseSubjectType.Container,
            State = InspectionWorkflowState.Open,
            TenantId = _tenantId,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (caseId, esiId);
    }

    private async Task<Guid> SeedSubmissionAsync(
        Guid caseId, Guid esiId, string status, int priority = 0,
        DateTimeOffset? submittedAt = null,
        string? errorMessage = null,
        string idempotencyKey = "")
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var id = Guid.NewGuid();
        db.OutboundSubmissions.Add(new OutboundSubmission
        {
            Id = id,
            CaseId = caseId,
            ExternalSystemInstanceId = esiId,
            Status = status,
            Priority = priority,
            SubmittedAt = submittedAt ?? DateTimeOffset.UtcNow,
            IdempotencyKey = string.IsNullOrEmpty(idempotencyKey) ? "key-" + id : idempotencyKey,
            PayloadJson = "{\"verdict\":\"clear\"}",
            ErrorMessage = errorMessage,
            TenantId = _tenantId,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task ListAsync_NoFilter_ReturnsAllRowsOrderedByPriorityThenTime()
    {
        var (caseId, esiId) = await SeedFixtureAsync();
        var older = DateTimeOffset.UtcNow.AddHours(-2);
        var newer = DateTimeOffset.UtcNow.AddMinutes(-5);
        var lowOld = await SeedSubmissionAsync(caseId, esiId, "pending", priority: 0, submittedAt: older);
        var highNew = await SeedSubmissionAsync(caseId, esiId, "pending", priority: 5, submittedAt: newer);
        var midOld = await SeedSubmissionAsync(caseId, esiId, "pending", priority: 1, submittedAt: older);

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsSubmissionQueueAdminService>();
        var page = await svc.ListAsync(filters: null, page: 1, pageSize: 50);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(highNew, page.Rows[0].Id);
        Assert.Equal(midOld, page.Rows[1].Id);
        Assert.Equal(lowOld, page.Rows[2].Id);
    }

    [Fact]
    public async Task ListAsync_StatusFilter_NarrowsRows()
    {
        var (caseId, esiId) = await SeedFixtureAsync();
        await SeedSubmissionAsync(caseId, esiId, "pending");
        var errId = await SeedSubmissionAsync(caseId, esiId, "error", errorMessage: "boom");
        await SeedSubmissionAsync(caseId, esiId, "accepted");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsSubmissionQueueAdminService>();
        var page = await svc.ListAsync(new SubmissionQueueFilter
        {
            Statuses = new[] { "error" }
        });
        Assert.Single(page.Rows);
        Assert.Equal(errId, page.Rows[0].Id);
        Assert.Equal("boom", page.Rows[0].ErrorMessage);
    }

    [Fact]
    public async Task ListAsync_SearchByIdempotencyKey_FindsRow()
    {
        var (caseId, esiId) = await SeedFixtureAsync();
        await SeedSubmissionAsync(caseId, esiId, "pending", idempotencyKey: "ICUMS-AB12-001");
        await SeedSubmissionAsync(caseId, esiId, "pending", idempotencyKey: "ICUMS-CD34-002");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsSubmissionQueueAdminService>();
        var page = await svc.ListAsync(new SubmissionQueueFilter
        {
            SearchText = "AB12"
        });
        Assert.Single(page.Rows);
        Assert.Equal("ICUMS-AB12-001", page.Rows[0].IdempotencyKey);
    }

    [Fact]
    public async Task RequeueAsync_FlipsErrorToPending_AndEmitsEvent()
    {
        var (caseId, esiId) = await SeedFixtureAsync();
        var id = await SeedSubmissionAsync(caseId, esiId, "error", errorMessage: "kaboom");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsSubmissionQueueAdminService>();
        var result = await svc.RequeueAsync(id, Guid.NewGuid());

        Assert.True(result.Success);
        Assert.Equal(1, result.RowsAffected);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var row = await db.OutboundSubmissions.FirstAsync(s => s.Id == id);
        Assert.Equal("pending", row.Status);
        Assert.Null(row.ErrorMessage);
        Assert.NotNull(row.LastAttemptAt);

        Assert.Single(_events.Published, e => e.EventType == "inspection.icums.submission_requeued");
    }

    [Fact]
    public async Task RequeueAsync_AlreadyPending_NoOp()
    {
        var (caseId, esiId) = await SeedFixtureAsync();
        var id = await SeedSubmissionAsync(caseId, esiId, "pending");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsSubmissionQueueAdminService>();
        var result = await svc.RequeueAsync(id, Guid.NewGuid());

        Assert.True(result.Success);
        Assert.Equal(0, result.RowsAffected);
        // Idempotent: no event for an already-pending row.
        Assert.DoesNotContain(_events.Published, e => e.EventType == "inspection.icums.submission_requeued");
    }

    [Fact]
    public async Task RequeueBulkAsync_RequiresStatusFilter()
    {
        await SeedFixtureAsync();
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsSubmissionQueueAdminService>();
        var result = await svc.RequeueBulkAsync(new SubmissionQueueFilter(), Guid.NewGuid());
        Assert.False(result.Success);
        Assert.Contains("status filter", result.Notice ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequeueBulkAsync_FlipsAllErrorRowsAndEmitsSingleEvent()
    {
        var (caseId, esiId) = await SeedFixtureAsync();
        await SeedSubmissionAsync(caseId, esiId, "error", errorMessage: "e1");
        await SeedSubmissionAsync(caseId, esiId, "error", errorMessage: "e2");
        await SeedSubmissionAsync(caseId, esiId, "rejected");
        await SeedSubmissionAsync(caseId, esiId, "pending");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsSubmissionQueueAdminService>();
        var result = await svc.RequeueBulkAsync(new SubmissionQueueFilter
        {
            Statuses = new[] { "error" }
        }, Guid.NewGuid());

        Assert.True(result.Success);
        Assert.Equal(2, result.RowsAffected);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        Assert.Equal(3, await db.OutboundSubmissions.CountAsync(s => s.Status == "pending"));

        var bulk = Assert.Single(
            _events.Published, e => e.EventType == "inspection.icums.submission_bulk_requeued");
        Assert.Equal(2, bulk.Payload.GetProperty("row_count").GetInt32());
    }

    [Fact]
    public async Task GetPayloadAsync_ReturnsRawJson()
    {
        var (caseId, esiId) = await SeedFixtureAsync();
        var id = await SeedSubmissionAsync(caseId, esiId, "accepted");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsSubmissionQueueAdminService>();
        var payload = await svc.GetPayloadAsync(id);
        Assert.NotNull(payload);
        Assert.Equal("accepted", payload!.Status);
        Assert.Contains("verdict", payload.PayloadJson);
    }

    [Fact]
    public async Task GetStatusCountsAsync_BucketsByStatus()
    {
        var (caseId, esiId) = await SeedFixtureAsync();
        await SeedSubmissionAsync(caseId, esiId, "pending");
        await SeedSubmissionAsync(caseId, esiId, "pending");
        await SeedSubmissionAsync(caseId, esiId, "error");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsSubmissionQueueAdminService>();
        var counts = await svc.GetStatusCountsAsync();
        Assert.Equal(2, counts["pending"]);
        Assert.Equal(1, counts["error"]);
    }

    /// <summary>Test double — captures every <see cref="DomainEvent"/> the service emits.</summary>
    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<DomainEvent> Published { get; } = new();

        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default)
        {
            Published.Add(evt);
            return Task.FromResult(evt);
        }

        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(
            IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
        {
            foreach (var e in events) Published.Add(e);
            return Task.FromResult<IReadOnlyList<DomainEvent>>(events);
        }
    }
}
