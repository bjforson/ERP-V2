using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.AnalysisServices;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 14 / VP6 Phase D — tests for <see cref="CaseVisibilityService"/>:
/// shared mode, exclusive mode (most-specific-service-wins), accessible
/// location helpers. EF in-memory holds both InspectionDbContext + a
/// dedicated TenancyDbContext so we can flip CaseVisibilityModel
/// per-test.
/// </summary>
public sealed class CaseVisibilityServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly long _tenantId = 1;

    public CaseVisibilityServiceTests()
    {
        // Two separate in-memory DBs so the inspection + tenancy contexts
        // don't share entity tracking. Same instance name, different db name.
        var inspectionDb = "vis-inspection-" + Guid.NewGuid();
        var tenancyDb = "vis-tenancy-" + Guid.NewGuid();

        var services = new ServiceCollection();
        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(inspectionDb)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddDbContext<TenancyDbContext>(o =>
            o.UseInMemoryDatabase(tenancyDb)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddCaseClaimAndVisibility();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    private async Task SeedTenantWithVisibilityAsync(CaseVisibilityModel model, bool allowMulti = true)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var existing = await db.Tenants.FirstOrDefaultAsync(t => t.Id == _tenantId);
        if (existing is null)
        {
            db.Tenants.Add(new Tenant
            {
                Id = _tenantId,
                Code = "test",
                Name = "Test",
                CaseVisibilityModel = model,
                AllowMultiServiceMembership = allowMulti,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        else
        {
            existing.CaseVisibilityModel = model;
            existing.AllowMultiServiceMembership = allowMulti;
            await db.SaveChangesAsync();
        }
    }

    private async Task<TestTopology> SeedTopologyAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var locTema = new Location { Id = Guid.NewGuid(), Code = "tema", Name = "Tema", TimeZone = "Africa/Accra", IsActive = true, TenantId = _tenantId, CreatedAt = DateTimeOffset.UtcNow };
        var locKotoka = new Location { Id = Guid.NewGuid(), Code = "kotoka", Name = "Kotoka", TimeZone = "Africa/Accra", IsActive = true, TenantId = _tenantId, CreatedAt = DateTimeOffset.UtcNow };
        db.Locations.AddRange(locTema, locKotoka);

        // All Locations service — covers BOTH locations (created earlier so it wins on the tie-break).
        var allLocs = new AnalysisService { Id = Guid.NewGuid(), Name = "All Locations", IsBuiltInAllLocations = true, TenantId = _tenantId, CreatedAt = DateTimeOffset.UtcNow.AddDays(-10), UpdatedAt = DateTimeOffset.UtcNow };
        // Tema-specific service — covers only Tema (more specific).
        var temaSvc = new AnalysisService { Id = Guid.NewGuid(), Name = "Tema team", IsBuiltInAllLocations = false, TenantId = _tenantId, CreatedAt = DateTimeOffset.UtcNow.AddDays(-5), UpdatedAt = DateTimeOffset.UtcNow };
        db.AnalysisServices.AddRange(allLocs, temaSvc);

        db.AnalysisServiceLocations.AddRange(
            new AnalysisServiceLocation { AnalysisServiceId = allLocs.Id, LocationId = locTema.Id, TenantId = _tenantId, AddedAt = DateTimeOffset.UtcNow },
            new AnalysisServiceLocation { AnalysisServiceId = allLocs.Id, LocationId = locKotoka.Id, TenantId = _tenantId, AddedAt = DateTimeOffset.UtcNow },
            new AnalysisServiceLocation { AnalysisServiceId = temaSvc.Id, LocationId = locTema.Id, TenantId = _tenantId, AddedAt = DateTimeOffset.UtcNow });

        // Two cases: one in Tema, one in Kotoka.
        var caseTema = new InspectionCase
        {
            Id = Guid.NewGuid(),
            LocationId = locTema.Id,
            SubjectType = CaseSubjectType.Container,
            SubjectIdentifier = "TEMA-1",
            State = InspectionWorkflowState.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = _tenantId,
        };
        var caseKotoka = new InspectionCase
        {
            Id = Guid.NewGuid(),
            LocationId = locKotoka.Id,
            SubjectType = CaseSubjectType.Container,
            SubjectIdentifier = "KOTOKA-1",
            State = InspectionWorkflowState.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = _tenantId,
        };
        db.Cases.AddRange(caseTema, caseKotoka);

        await db.SaveChangesAsync();

        return new TestTopology(locTema.Id, locKotoka.Id, allLocs.Id, temaSvc.Id, caseTema.Id, caseKotoka.Id);
    }

    private async Task AddMembershipAsync(Guid serviceId, Guid userId)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        db.AnalysisServiceUsers.Add(new AnalysisServiceUser
        {
            AnalysisServiceId = serviceId,
            UserId = userId,
            TenantId = _tenantId,
            AssignedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SharedMode_AllLocationsMembership_SeesBothCases()
    {
        await SeedTenantWithVisibilityAsync(CaseVisibilityModel.Shared);
        var topo = await SeedTopologyAsync();
        var userId = Guid.NewGuid();
        await AddMembershipAsync(topo.AllLocsServiceId, userId);

        using var scope = _sp.CreateScope();
        var vis = scope.ServiceProvider.GetRequiredService<CaseVisibilityService>();
        var cases = await vis.GetVisibleCasesAsync(userId);

        Assert.Equal(2, cases.Count);
        Assert.Contains(cases, c => c.Id == topo.CaseTemaId);
        Assert.Contains(cases, c => c.Id == topo.CaseKotokaId);
    }

    [Fact]
    public async Task SharedMode_TemaOnlyMembership_SeesOnlyTema()
    {
        await SeedTenantWithVisibilityAsync(CaseVisibilityModel.Shared);
        var topo = await SeedTopologyAsync();
        var userId = Guid.NewGuid();
        await AddMembershipAsync(topo.TemaServiceId, userId);

        using var scope = _sp.CreateScope();
        var vis = scope.ServiceProvider.GetRequiredService<CaseVisibilityService>();
        var cases = await vis.GetVisibleCasesAsync(userId);

        Assert.Single(cases);
        Assert.Equal(topo.CaseTemaId, cases[0].Id);
    }

    [Fact]
    public async Task SharedMode_NoMembership_SeesNothing()
    {
        await SeedTenantWithVisibilityAsync(CaseVisibilityModel.Shared);
        var topo = await SeedTopologyAsync();
        var userId = Guid.NewGuid();
        // No membership granted.

        using var scope = _sp.CreateScope();
        var vis = scope.ServiceProvider.GetRequiredService<CaseVisibilityService>();
        var cases = await vis.GetVisibleCasesAsync(userId);

        Assert.Empty(cases);
    }

    [Fact]
    public async Task ExclusiveMode_TemaAnalystSeesTemaCase_ButAllLocsAnalystDoesNot()
    {
        await SeedTenantWithVisibilityAsync(CaseVisibilityModel.Exclusive);
        var topo = await SeedTopologyAsync();

        // Most-specific wins: Tema service (1 location) beats All Locations (2 locations) for Tema.
        // For Kotoka, only All Locations qualifies, so it wins by default.
        var temaUser = Guid.NewGuid();
        var allLocsUser = Guid.NewGuid();
        await AddMembershipAsync(topo.TemaServiceId, temaUser);
        await AddMembershipAsync(topo.AllLocsServiceId, allLocsUser);

        using var scope = _sp.CreateScope();
        var vis = scope.ServiceProvider.GetRequiredService<CaseVisibilityService>();

        var temaCases = await vis.GetVisibleCasesAsync(temaUser);
        Assert.Single(temaCases);
        Assert.Equal(topo.CaseTemaId, temaCases[0].Id);

        // The all-locs analyst loses Tema (specialised team won) but still
        // gets Kotoka because no specialised service exists for it.
        var allLocsCases = await vis.GetVisibleCasesAsync(allLocsUser);
        Assert.Single(allLocsCases);
        Assert.Equal(topo.CaseKotokaId, allLocsCases[0].Id);
    }

    [Fact]
    public async Task ComputeExclusiveWinners_TemaResolvesToSpecialised_KotokaResolvesToAllLocs()
    {
        await SeedTenantWithVisibilityAsync(CaseVisibilityModel.Exclusive);
        var topo = await SeedTopologyAsync();

        using var scope = _sp.CreateScope();
        var vis = scope.ServiceProvider.GetRequiredService<CaseVisibilityService>();
        var winners = await vis.ComputeExclusiveWinnersAsync(new[] { topo.LocTemaId, topo.LocKotokaId });

        Assert.Equal(2, winners.Count);
        Assert.Equal(topo.TemaServiceId, winners[topo.LocTemaId]);
        Assert.Equal(topo.AllLocsServiceId, winners[topo.LocKotokaId]);
    }

    [Fact]
    public async Task GetAccessibleLocationIds_UnionsMembershipScopes()
    {
        await SeedTenantWithVisibilityAsync(CaseVisibilityModel.Shared, allowMulti: true);
        var topo = await SeedTopologyAsync();
        var userId = Guid.NewGuid();
        // Member of BOTH services — accessible locations are the union (Tema + Kotoka).
        await AddMembershipAsync(topo.AllLocsServiceId, userId);
        await AddMembershipAsync(topo.TemaServiceId, userId);

        using var scope = _sp.CreateScope();
        var vis = scope.ServiceProvider.GetRequiredService<CaseVisibilityService>();
        var locIds = await vis.GetAccessibleLocationIdsAsync(userId);

        Assert.Equal(2, locIds.Count);
        Assert.Contains(topo.LocTemaId, locIds);
        Assert.Contains(topo.LocKotokaId, locIds);
    }

    private sealed record TestTopology(
        Guid LocTemaId,
        Guid LocKotokaId,
        Guid AllLocsServiceId,
        Guid TemaServiceId,
        Guid CaseTemaId,
        Guid CaseKotokaId);
}
