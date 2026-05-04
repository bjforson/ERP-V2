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
/// Sprint 14 / VP6 Phase D — service-layer guard tests for
/// <see cref="AnalysisServiceAdminService"/>. The DB-side trigger that
/// rejects DELETE on <c>IsBuiltInAllLocations = TRUE</c> is exercised by
/// the Postgres E2E; here we cover the C# guards (rename + delete +
/// multi-membership reject + remove-location-from-built-in).
/// </summary>
public sealed class AnalysisServiceAdminServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly long _tenantId = 1;

    public AnalysisServiceAdminServiceTests()
    {
        var dbName = "admin-" + Guid.NewGuid();
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
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddAnalysisServiceAdmin();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    private async Task<Guid> SeedAllLocationsAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var id = Guid.NewGuid();
        db.AnalysisServices.Add(new AnalysisService
        {
            Id = id,
            Name = "All Locations",
            IsBuiltInAllLocations = true,
            TenantId = _tenantId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedRegularServiceAsync(string name = "tema team")
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AnalysisServiceAdminService>();
        return await svc.CreateAsync(name, "test description", Guid.NewGuid());
    }

    private async Task<Guid> SeedLocationAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var loc = new Location
        {
            Id = Guid.NewGuid(),
            Code = "test-loc-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Test",
            TimeZone = "Africa/Accra",
            IsActive = true,
            TenantId = _tenantId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Locations.Add(loc);
        await db.SaveChangesAsync();
        return loc.Id;
    }

    [Fact]
    public async Task Create_HappyPath_ReturnsNewServiceId()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AnalysisServiceAdminService>();
        var id = await svc.CreateAsync("kotoka cargo", "air freight only", Guid.NewGuid());
        Assert.NotEqual(Guid.Empty, id);
    }

    [Fact]
    public async Task Create_RejectsAllLocationsName()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AnalysisServiceAdminService>();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("All Locations", null, Guid.NewGuid()));
        // Case-insensitive too.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("ALL LOCATIONS", null, Guid.NewGuid()));
    }

    [Fact]
    public async Task Rename_BuiltIn_Rejects()
    {
        var builtIn = await SeedAllLocationsAsync();
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AnalysisServiceAdminService>();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RenameAsync(builtIn, "Renamed"));
    }

    [Fact]
    public async Task Rename_Regular_Succeeds()
    {
        var id = await SeedRegularServiceAsync("old name");
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AnalysisServiceAdminService>();
        await svc.RenameAsync(id, "new name");

        using var verify = _sp.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var row = await db.AnalysisServices.AsNoTracking().FirstAsync(s => s.Id == id);
        Assert.Equal("new name", row.Name);
    }

    [Fact]
    public async Task Delete_BuiltIn_Rejects()
    {
        var builtIn = await SeedAllLocationsAsync();
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AnalysisServiceAdminService>();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DeleteAsync(builtIn));
    }

    [Fact]
    public async Task Delete_Regular_Succeeds()
    {
        var id = await SeedRegularServiceAsync();
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AnalysisServiceAdminService>();
        await svc.DeleteAsync(id);

        using var verify = _sp.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<InspectionDbContext>();
        Assert.Null(await db.AnalysisServices.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id));
    }

    [Fact]
    public async Task RemoveLocation_FromBuiltIn_Rejects()
    {
        var builtIn = await SeedAllLocationsAsync();
        var locId = await SeedLocationAsync();
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<AnalysisServiceAdminService>();
            await svc.AddLocationAsync(builtIn, locId);
        }
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<AnalysisServiceAdminService>();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.RemoveLocationAsync(builtIn, locId));
        }
    }

    [Fact]
    public async Task AddRemoveLocation_Regular_RoundTrips()
    {
        var serviceId = await SeedRegularServiceAsync("kotoka");
        var locId = await SeedLocationAsync();

        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<AnalysisServiceAdminService>();
            await svc.AddLocationAsync(serviceId, locId);
        }
        using (var verify = _sp.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<InspectionDbContext>();
            Assert.Single(await db.AnalysisServiceLocations.AsNoTracking()
                .Where(asl => asl.AnalysisServiceId == serviceId).ToListAsync());
        }
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<AnalysisServiceAdminService>();
            await svc.RemoveLocationAsync(serviceId, locId);
        }
        using (var verify = _sp.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<InspectionDbContext>();
            Assert.Empty(await db.AnalysisServiceLocations.AsNoTracking()
                .Where(asl => asl.AnalysisServiceId == serviceId).ToListAsync());
        }
    }

    [Fact]
    public async Task AddUser_MultiMembershipDisabled_RejectsSecondAssignment()
    {
        var svcA = await SeedRegularServiceAsync("svc-A");
        var svcB = await SeedRegularServiceAsync("svc-B");
        var userId = Guid.NewGuid();

        using (var scope = _sp.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<AnalysisServiceAdminService>();
            await admin.AddUserAsync(svcA, userId, null, allowMultiServiceMembership: false);
        }
        using (var scope = _sp.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<AnalysisServiceAdminService>();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => admin.AddUserAsync(svcB, userId, null, allowMultiServiceMembership: false));
        }
    }

    [Fact]
    public async Task AddUser_MultiMembershipEnabled_AllowsBoth()
    {
        var svcA = await SeedRegularServiceAsync("svc-A2");
        var svcB = await SeedRegularServiceAsync("svc-B2");
        var userId = Guid.NewGuid();

        using (var scope = _sp.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<AnalysisServiceAdminService>();
            await admin.AddUserAsync(svcA, userId, null, allowMultiServiceMembership: true);
            await admin.AddUserAsync(svcB, userId, null, allowMultiServiceMembership: true);
        }
        using var verify = _sp.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<InspectionDbContext>();
        Assert.Equal(2, await db.AnalysisServiceUsers.AsNoTracking()
            .Where(u => u.UserId == userId).CountAsync());
    }

    [Fact]
    public async Task AddUser_BuiltIn_ExemptFromMultiMembershipCheck()
    {
        // User assigned to a regular service first; adding them to the
        // built-in "All Locations" must succeed even when the tenant
        // has multi-membership disabled — the universal-access service
        // is exempt.
        var builtIn = await SeedAllLocationsAsync();
        var regular = await SeedRegularServiceAsync("regular");
        var userId = Guid.NewGuid();

        using (var scope = _sp.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<AnalysisServiceAdminService>();
            await admin.AddUserAsync(regular, userId, null, allowMultiServiceMembership: false);
            // Should NOT throw — built-in is exempt.
            await admin.AddUserAsync(builtIn, userId, null, allowMultiServiceMembership: false);
        }
    }
}
