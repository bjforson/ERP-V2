using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.AnalysisServices;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 14 / VP6 Phase D — tests for
/// <see cref="AnalysisServiceLocationAutoJoinInterceptor"/>. EF in-memory
/// suffices because the interceptor logic is C#-only (no RLS / triggers
/// involved); FORCE ROW LEVEL SECURITY is exercised by the Postgres E2E.
/// </summary>
public sealed class AnalysisServiceLocationAutoJoinInterceptorTests : IDisposable
{
    private readonly ServiceProvider _sp;

    public AnalysisServiceLocationAutoJoinInterceptorTests()
    {
        var dbName = "auto-join-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddAnalysisServiceLocationAutoJoinInterceptor();
        services.AddDbContext<InspectionDbContext>((sp, o) =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
             .AddInterceptors(sp.GetRequiredService<AnalysisServiceLocationAutoJoinInterceptor>()));
        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    private async Task SeedAllLocationsServiceAsync(long tenantId, Guid serviceId)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        db.AnalysisServices.Add(new AnalysisService
        {
            Id = serviceId,
            Name = "All Locations",
            IsBuiltInAllLocations = true,
            TenantId = tenantId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task LocationInsert_AutoJoinsAllLocationsService()
    {
        var serviceId = Guid.NewGuid();
        await SeedAllLocationsServiceAsync(1, serviceId);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var loc = new Location
        {
            Id = Guid.NewGuid(),
            Code = "tema",
            Name = "Tema",
            TimeZone = "Africa/Accra",
            IsActive = true,
            TenantId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Locations.Add(loc);
        await db.SaveChangesAsync();

        using var verify = _sp.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var joins = await db2.AnalysisServiceLocations.AsNoTracking().ToListAsync();
        Assert.Single(joins);
        Assert.Equal(serviceId, joins[0].AnalysisServiceId);
        Assert.Equal(loc.Id, joins[0].LocationId);
        Assert.Equal(1, joins[0].TenantId);
    }

    [Fact]
    public async Task NoAllLocationsService_LogsWarningAndSkips()
    {
        // No service seeded — interceptor must NOT throw, just skip.
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        db.Locations.Add(new Location
        {
            Id = Guid.NewGuid(),
            Code = "kotoka",
            Name = "Kotoka",
            TimeZone = "Africa/Accra",
            IsActive = true,
            TenantId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        using var verify = _sp.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<InspectionDbContext>();
        Assert.Empty(await db2.AnalysisServiceLocations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SameSaveChanges_BootstrapPlusLocation_ResolvesViaChangeTracker()
    {
        // The interceptor must find the in-flight AnalysisService entity
        // via ChangeTracker when both rows are added in the same
        // SaveChanges. The resulting junction has TenantId stamped.
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var serviceId = Guid.NewGuid();
        var locId = Guid.NewGuid();
        db.AnalysisServices.Add(new AnalysisService
        {
            Id = serviceId,
            Name = "All Locations",
            IsBuiltInAllLocations = true,
            TenantId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.Locations.Add(new Location
        {
            Id = locId,
            Code = "tema",
            Name = "Tema",
            TimeZone = "Africa/Accra",
            IsActive = true,
            TenantId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        using var verify = _sp.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var joins = await db2.AnalysisServiceLocations.AsNoTracking().ToListAsync();
        Assert.Single(joins);
        Assert.Equal(serviceId, joins[0].AnalysisServiceId);
        Assert.Equal(locId, joins[0].LocationId);
    }

    [Fact]
    public async Task IdempotentForUpdates_OnlyAddedLocationsAreJoined()
    {
        // Insert one location; verify junction. Then update the location
        // (e.g., rename); verify NO additional junction was added.
        var serviceId = Guid.NewGuid();
        await SeedAllLocationsServiceAsync(1, serviceId);

        Guid locId;
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var loc = new Location
            {
                Id = Guid.NewGuid(),
                Code = "elubo",
                Name = "Elubo",
                TimeZone = "Africa/Accra",
                IsActive = true,
                TenantId = 1,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Locations.Add(loc);
            await db.SaveChangesAsync();
            locId = loc.Id;
        }

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var loc = await db.Locations.FirstAsync(l => l.Id == locId);
            loc.Name = "Elubo Border";
            await db.SaveChangesAsync();
        }

        using var verify = _sp.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var joins = await db2.AnalysisServiceLocations.AsNoTracking().ToListAsync();
        Assert.Single(joins); // Still exactly one — the update did not double-up.
    }
}
