using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Downloads;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 22 / B2.2 — covers the
/// <see cref="IcumsDownloadQueueAdminService"/> filtered list,
/// re-link override (and not-found / no-op paths), pull-cursor lag
/// projection, and the doc-type counts the dashboard summary card uses.
/// </summary>
public sealed class IcumsDownloadQueueAdminServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly long _tenantId = 1;
    private readonly TestClock _clock = new(DateTimeOffset.Parse("2026-05-04T18:00:00Z"));

    public IcumsDownloadQueueAdminServiceTests()
    {
        var dbName = "dlq-" + Guid.NewGuid();
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
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddIcumsDownloadQueueAdmin();
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
            Id = locId, Code = "loc", Name = "Test", TimeZone = "Africa/Accra",
            IsActive = true, TenantId = _tenantId, CreatedAt = DateTimeOffset.UtcNow,
        });
        var esiId = Guid.NewGuid();
        db.ExternalSystemInstances.Add(new ExternalSystemInstance
        {
            Id = esiId, TypeCode = "icums-gh", DisplayName = "ICUMS Tema",
            TenantId = _tenantId, CreatedAt = DateTimeOffset.UtcNow,
        });
        var caseId = Guid.NewGuid();
        db.Cases.Add(new InspectionCase
        {
            Id = caseId, LocationId = locId, SubjectIdentifier = "MSCU1234567",
            SubjectType = CaseSubjectType.Container, State = InspectionWorkflowState.Open,
            TenantId = _tenantId, OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (caseId, esiId);
    }

    private async Task<Guid> SeedDocAsync(
        Guid caseId, Guid esiId, string type, string reference, DateTimeOffset? receivedAt = null)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var id = Guid.NewGuid();
        db.AuthorityDocuments.Add(new AuthorityDocument
        {
            Id = id, CaseId = caseId, ExternalSystemInstanceId = esiId,
            DocumentType = type, ReferenceNumber = reference,
            PayloadJson = "{\"k\":\"v\"}", TenantId = _tenantId,
            ReceivedAt = receivedAt ?? DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task ListAsync_FiltersByDocumentType()
    {
        var (caseId, esiId) = await SeedFixtureAsync();
        var boe = await SeedDocAsync(caseId, esiId, "BOE", "BOE-001");
        await SeedDocAsync(caseId, esiId, "CMR", "CMR-001");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsDownloadQueueAdminService>();
        var page = await svc.ListAsync(new DownloadQueueFilter { DocumentTypes = new[] { "BOE" } });
        Assert.Single(page.Rows);
        Assert.Equal(boe, page.Rows[0].Id);
        Assert.True(page.Rows[0].IsMatched);
    }

    [Fact]
    public async Task ListAsync_FiltersByMatchStatus_Unmatched()
    {
        var (caseId, esiId) = await SeedFixtureAsync();
        await SeedDocAsync(caseId, esiId, "BOE", "BOE-001");
        var unmatched = await SeedDocAsync(Guid.Empty, esiId, "BOE", "BOE-002");

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        // Sanity: both rows should be persisted by the InMemory provider
        // even though CaseId = Guid.Empty has no matching case row (FK
        // is not enforced in-memory).
        var allDocs = await db.AuthorityDocuments.ToListAsync();
        Assert.Equal(2, allDocs.Count);
        var emptyCount = allDocs.Count(d => d.CaseId == Guid.Empty);
        Assert.Equal(1, emptyCount);

        // Sanity 2: confirm the Where(d => d.CaseId == Guid.Empty)
        // resolves under EF's IQueryable evaluation. (EF in-memory has
        // bitten on Guid.Empty defaults vs. unset Guids before.)
        var unmatchedRows = await db.AuthorityDocuments
            .Where(d => d.CaseId == Guid.Empty)
            .Select(d => d.Id)
            .ToListAsync();
        Assert.Single(unmatchedRows);
        Assert.Equal(unmatched, unmatchedRows[0]);

        var svc = scope.ServiceProvider.GetRequiredService<IcumsDownloadQueueAdminService>();
        var page = await svc.ListAsync(new DownloadQueueFilter { MatchStatus = DownloadMatchStatus.Unmatched });
        Assert.Single(page.Rows);
        Assert.Equal(unmatched, page.Rows[0].Id);
        Assert.False(page.Rows[0].IsMatched);
    }

    [Fact]
    public async Task ListAsync_SearchByReferenceNumber_FindsRow()
    {
        var (caseId, esiId) = await SeedFixtureAsync();
        await SeedDocAsync(caseId, esiId, "BOE", "BOE-2026-001");
        await SeedDocAsync(caseId, esiId, "BOE", "BOE-2026-002");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsDownloadQueueAdminService>();
        var page = await svc.ListAsync(new DownloadQueueFilter { SearchText = "001" });
        Assert.Single(page.Rows);
        Assert.Equal("BOE-2026-001", page.Rows[0].ReferenceNumber);
    }

    [Fact]
    public async Task RelinkAsync_NotFound_ReturnsFalse()
    {
        await SeedFixtureAsync();
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsDownloadQueueAdminService>();
        var result = await svc.RelinkAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.False(result.Success);
    }

    [Fact]
    public async Task RelinkAsync_HappyPath_FlipsCaseId()
    {
        var (caseId, esiId) = await SeedFixtureAsync();
        var docId = await SeedDocAsync(Guid.Empty, esiId, "BOE", "BOE-001");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsDownloadQueueAdminService>();
        var result = await svc.RelinkAsync(docId, caseId, Guid.NewGuid());
        Assert.True(result.Success);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var doc = await db.AuthorityDocuments.FirstAsync(d => d.Id == docId);
        Assert.Equal(caseId, doc.CaseId);
    }

    [Fact]
    public async Task GetPullCursorsAsync_ProjectsLagSeconds()
    {
        var (caseId, esiId) = await SeedFixtureAsync();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            // Last successful pull was 7 hours ago -> warning band per the page (>6h, ≤24h).
            db.OutcomePullCursors.Add(new OutcomePullCursor
            {
                ExternalSystemInstanceId = esiId,
                LastSuccessfulPullAt = _clock.GetUtcNow().AddHours(-7),
                LastPullWindowUntil = _clock.GetUtcNow().AddHours(-7),
                ConsecutiveFailures = 2,
                TenantId = _tenantId,
            });
            await db.SaveChangesAsync();
        }

        using var scope2 = _sp.CreateScope();
        var svc = scope2.ServiceProvider.GetRequiredService<IcumsDownloadQueueAdminService>();
        var cursors = await svc.GetPullCursorsAsync();
        var cursor = Assert.Single(cursors);
        Assert.Equal(2, cursor.ConsecutiveFailures);
        Assert.InRange(cursor.LagSeconds, 7 * 3600 - 5, 7 * 3600 + 5);
    }

    [Fact]
    public async Task GetDocumentTypeCountsAsync_BucketsByType()
    {
        var (caseId, esiId) = await SeedFixtureAsync();
        await SeedDocAsync(caseId, esiId, "BOE", "BOE-001");
        await SeedDocAsync(caseId, esiId, "BOE", "BOE-002");
        await SeedDocAsync(caseId, esiId, "CMR", "CMR-001");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsDownloadQueueAdminService>();
        var counts = await svc.GetDocumentTypeCountsAsync();
        Assert.Equal(2, counts["BOE"]);
        Assert.Equal(1, counts["CMR"]);
    }

    /// <summary>Frozen-clock test double for deterministic lag-seconds assertions.</summary>
    private sealed class TestClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public TestClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
