using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Icums;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 22 / B2.3 — covers
/// <see cref="IcumsDashboardService"/>,
/// <see cref="IcumsLooseCargoService"/>, and
/// <see cref="IcumsBoeLookupService"/>. The manual-pull service has its
/// own integration test (depends on IPluginRegistry; covered by the
/// existing OutcomePullWorker tests' fixture pattern, so no separate
/// in-memory test here).
/// </summary>
public sealed class IcumsDashboardSuiteTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly long _tenantId = 1;
    private readonly TestClock _clock = new(DateTimeOffset.Parse("2026-05-04T18:00:00Z"));

    public IcumsDashboardSuiteTests()
    {
        var dbName = "dashboard-" + Guid.NewGuid();
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
        services.AddIcumsDashboardSuite();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    private async Task<(Guid locId, Guid esiId)> SeedFixtureAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var locId = Guid.NewGuid();
        db.Locations.Add(new Location
        {
            Id = locId, Code = "loc", Name = "Tema",
            TimeZone = "Africa/Accra", IsActive = true,
            TenantId = _tenantId, CreatedAt = DateTimeOffset.UtcNow,
        });
        var esiId = Guid.NewGuid();
        db.ExternalSystemInstances.Add(new ExternalSystemInstance
        {
            Id = esiId, TypeCode = "icums-gh", DisplayName = "ICUMS Tema",
            TenantId = _tenantId, CreatedAt = DateTimeOffset.UtcNow, IsActive = true,
        });
        await db.SaveChangesAsync();
        return (locId, esiId);
    }

    private async Task<Guid> SeedCaseAsync(Guid locId, string subject, DateTimeOffset openedAt)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var id = Guid.NewGuid();
        db.Cases.Add(new InspectionCase
        {
            Id = id, LocationId = locId, SubjectIdentifier = subject,
            SubjectType = CaseSubjectType.Container, State = InspectionWorkflowState.Open,
            TenantId = _tenantId, OpenedAt = openedAt, StateEnteredAt = openedAt,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task SeedDocAsync(Guid caseId, Guid esiId, string type, string reference)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        db.AuthorityDocuments.Add(new AuthorityDocument
        {
            Id = Guid.NewGuid(), CaseId = caseId, ExternalSystemInstanceId = esiId,
            DocumentType = type, ReferenceNumber = reference, PayloadJson = "{}",
            TenantId = _tenantId, ReceivedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedSubmissionAsync(Guid esiId, string status)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        db.OutboundSubmissions.Add(new OutboundSubmission
        {
            Id = Guid.NewGuid(), CaseId = Guid.NewGuid(),
            ExternalSystemInstanceId = esiId, Status = status,
            IdempotencyKey = "k-" + Guid.NewGuid(),
            PayloadJson = "{}", TenantId = _tenantId,
            SubmittedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Dashboard_SummariesEverything()
    {
        var (locId, esiId) = await SeedFixtureAsync();
        var caseId = await SeedCaseAsync(locId, "MSCU111", DateTimeOffset.UtcNow);
        await SeedDocAsync(caseId, esiId, "BOE", "BOE-1");
        await SeedDocAsync(Guid.Empty, esiId, "BOE", "BOE-2");
        await SeedSubmissionAsync(esiId, "pending");
        await SeedSubmissionAsync(esiId, "error");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsDashboardService>();
        var s = await svc.GetSummaryAsync();

        Assert.Equal(1, s.SubmissionCountsByStatus["pending"]);
        Assert.Equal(1, s.SubmissionCountsByStatus["error"]);
        Assert.Equal(2, s.DownloadCountsByType["BOE"]);
        Assert.Single(s.ExternalSystems);
        Assert.Equal("ICUMS Tema", s.ExternalSystems[0].DisplayName);
        Assert.Equal(1, s.UnmatchedDocumentCount);
    }

    [Fact]
    public async Task LooseCargo_SurfacesAgedCasesWithNoDocs()
    {
        var (locId, esiId) = await SeedFixtureAsync();
        var oldCase = await SeedCaseAsync(locId, "OLD-LOOSE", _clock.GetUtcNow().AddHours(-12));
        var oldMatched = await SeedCaseAsync(locId, "OLD-OK", _clock.GetUtcNow().AddHours(-12));
        await SeedDocAsync(oldMatched, esiId, "BOE", "BOE-A");
        var freshLoose = await SeedCaseAsync(locId, "FRESH", _clock.GetUtcNow().AddMinutes(-30));

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsLooseCargoService>();
        var rows = await svc.ListAsync(TimeSpan.FromHours(4));

        // Aged case with no docs -> loose. Aged case with docs -> NOT loose.
        // Fresh case (younger than grace) -> NOT loose.
        Assert.Single(rows);
        Assert.Equal(oldCase, rows[0].CaseId);
        Assert.Equal("Tema", rows[0].LocationName);
    }

    [Fact]
    public async Task BoeLookup_FiltersByDefaultBoeTypes()
    {
        var (locId, esiId) = await SeedFixtureAsync();
        var caseId = await SeedCaseAsync(locId, "MSCU222", DateTimeOffset.UtcNow);
        await SeedDocAsync(caseId, esiId, "BOE", "BOE-X-1");
        await SeedDocAsync(caseId, esiId, "CMR", "CMR-1");
        await SeedDocAsync(caseId, esiId, "BOE", "BOE-X-2");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsBoeLookupService>();
        var rows = await svc.SearchAsync(query: null);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("BOE", r.DocumentType));
    }

    [Fact]
    public async Task BoeLookup_QueryNarrowsToReferenceMatches()
    {
        var (locId, esiId) = await SeedFixtureAsync();
        var caseId = await SeedCaseAsync(locId, "MSCU333", DateTimeOffset.UtcNow);
        await SeedDocAsync(caseId, esiId, "BOE", "BOE-A-001");
        await SeedDocAsync(caseId, esiId, "BOE", "BOE-B-001");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsBoeLookupService>();
        var rows = await svc.SearchAsync("A-");
        Assert.Single(rows);
        Assert.Equal("BOE-A-001", rows[0].ReferenceNumber);
    }

    /// <summary>Frozen-clock test double.</summary>
    private sealed class TestClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public TestClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
