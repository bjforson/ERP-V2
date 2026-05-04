using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.ExternalSystems;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 16 / LA1 extension — tests for
/// <see cref="ExternalSystemAdminService"/>. Covers the three-scope
/// validation rules + the lookup helper used by routing/case visibility.
/// </summary>
public sealed class ExternalSystemAdminServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly long _tenantId = 1;

    public ExternalSystemAdminServiceTests()
    {
        var dbName = "extsys-" + Guid.NewGuid();
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
        services.AddExternalSystemAdmin();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    private async Task<Guid> SeedLocationAsync(string suffix)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var loc = new Location
        {
            Id = Guid.NewGuid(),
            Code = "loc-" + suffix,
            Name = "Loc " + suffix,
            TimeZone = "Africa/Accra",
            TenantId = _tenantId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Locations.Add(loc);
        await db.SaveChangesAsync();
        return loc.Id;
    }

    // ---------- Scope validation -------------------------------------------

    [Fact]
    public void Validation_PerLocation_requires_exactly_one_binding()
    {
        Assert.Null(ExternalSystemBindingScopeValidation.Validate(
            ExternalSystemBindingScope.PerLocation, 1));
        Assert.Contains("exactly 1",
            ExternalSystemBindingScopeValidation.Validate(
                ExternalSystemBindingScope.PerLocation, 0));
        Assert.Contains("exactly 1",
            ExternalSystemBindingScopeValidation.Validate(
                ExternalSystemBindingScope.PerLocation, 2));
    }

    [Fact]
    public void Validation_SubsetOfLocations_requires_two_or_more_bindings()
    {
        Assert.Null(ExternalSystemBindingScopeValidation.Validate(
            ExternalSystemBindingScope.SubsetOfLocations, 2));
        Assert.Null(ExternalSystemBindingScopeValidation.Validate(
            ExternalSystemBindingScope.SubsetOfLocations, 5));

        Assert.Contains("at least 2",
            ExternalSystemBindingScopeValidation.Validate(
                ExternalSystemBindingScope.SubsetOfLocations, 0));
        Assert.Contains("at least 2",
            ExternalSystemBindingScopeValidation.Validate(
                ExternalSystemBindingScope.SubsetOfLocations, 1));
    }

    [Fact]
    public void Validation_Shared_requires_zero_bindings()
    {
        Assert.Null(ExternalSystemBindingScopeValidation.Validate(
            ExternalSystemBindingScope.Shared, 0));
        Assert.Contains("requires 0",
            ExternalSystemBindingScopeValidation.Validate(
                ExternalSystemBindingScope.Shared, 1));
    }

    [Fact]
    public void Validation_NegativeCount_is_rejected()
    {
        Assert.Contains("cannot be negative",
            ExternalSystemBindingScopeValidation.Validate(
                ExternalSystemBindingScope.PerLocation, -1));
    }

    // ---------- RegisterAsync ----------------------------------------------

    [Fact]
    public async Task RegisterAsync_PerLocation_writes_one_binding()
    {
        var locId = await SeedLocationAsync("a");

        Guid id;
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ExternalSystemAdminService>();
            id = await svc.RegisterAsync(
                "icums-gh", "ICUMS Tema", null,
                ExternalSystemBindingScope.PerLocation,
                new[] { locId });
        }

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var instance = await db.ExternalSystemInstances
                .Include(e => e.Bindings).FirstAsync(e => e.Id == id);
            Assert.Equal(ExternalSystemBindingScope.PerLocation, instance.Scope);
            Assert.Single(instance.Bindings);
            Assert.Equal(locId, instance.Bindings[0].LocationId);
        }
    }

    [Fact]
    public async Task RegisterAsync_SubsetOfLocations_writes_multiple_bindings()
    {
        var loc1 = await SeedLocationAsync("b1");
        var loc2 = await SeedLocationAsync("b2");
        var loc3 = await SeedLocationAsync("b3");

        Guid id;
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ExternalSystemAdminService>();
            id = await svc.RegisterAsync(
                "icums-gh", "ICUMS multi", null,
                ExternalSystemBindingScope.SubsetOfLocations,
                new[] { loc1, loc2, loc3 });
        }

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var bindings = await db.ExternalSystemBindings
                .Where(b => b.ExternalSystemInstanceId == id)
                .Select(b => b.LocationId)
                .ToListAsync();
            Assert.Equal(3, bindings.Count);
            Assert.Contains(loc1, bindings);
            Assert.Contains(loc2, bindings);
            Assert.Contains(loc3, bindings);
        }
    }

    [Fact]
    public async Task RegisterAsync_Shared_writes_zero_bindings()
    {
        Guid id;
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ExternalSystemAdminService>();
            id = await svc.RegisterAsync(
                "icums-gh", "ICUMS national", null,
                ExternalSystemBindingScope.Shared,
                Array.Empty<Guid>());
        }

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var count = await db.ExternalSystemBindings
                .CountAsync(b => b.ExternalSystemInstanceId == id);
            Assert.Equal(0, count);
        }
    }

    [Fact]
    public async Task RegisterAsync_PerLocation_with_two_bindings_is_rejected()
    {
        var loc1 = await SeedLocationAsync("c1");
        var loc2 = await SeedLocationAsync("c2");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ExternalSystemAdminService>();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.RegisterAsync(
                "icums-gh", "bad", null,
                ExternalSystemBindingScope.PerLocation,
                new[] { loc1, loc2 }));
        Assert.Contains("PerLocation scope requires exactly 1", ex.Message);
    }

    [Fact]
    public async Task RegisterAsync_SubsetOfLocations_with_one_binding_is_rejected()
    {
        var loc1 = await SeedLocationAsync("d1");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ExternalSystemAdminService>();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.RegisterAsync(
                "icums-gh", "bad subset", null,
                ExternalSystemBindingScope.SubsetOfLocations,
                new[] { loc1 }));
        Assert.Contains("at least 2", ex.Message);
    }

    [Fact]
    public async Task RegisterAsync_Shared_with_a_binding_is_rejected()
    {
        var loc1 = await SeedLocationAsync("e1");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ExternalSystemAdminService>();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.RegisterAsync(
                "icums-gh", "bad shared", null,
                ExternalSystemBindingScope.Shared,
                new[] { loc1 }));
        Assert.Contains("Shared scope requires 0", ex.Message);
    }

    [Fact]
    public async Task RegisterAsync_with_unknown_location_is_rejected()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ExternalSystemAdminService>();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.RegisterAsync(
                "icums-gh", "bad ref", null,
                ExternalSystemBindingScope.PerLocation,
                new[] { Guid.NewGuid() }));
        Assert.Contains("location ids do not exist", ex.Message);
    }

    [Fact]
    public async Task RegisterAsync_duplicate_location_ids_are_rejected()
    {
        var loc1 = await SeedLocationAsync("f1");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ExternalSystemAdminService>();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.RegisterAsync(
                "icums-gh", "dup", null,
                ExternalSystemBindingScope.SubsetOfLocations,
                new[] { loc1, loc1, loc1 }));
        Assert.Contains("Duplicate location ids", ex.Message);
    }

    // ---------- ResolveServingInstancesAsync -------------------------------

    [Fact]
    public async Task ResolveServingInstancesAsync_returns_PerLocation_match_only_for_its_location()
    {
        var loc1 = await SeedLocationAsync("g1");
        var loc2 = await SeedLocationAsync("g2");

        Guid perLoc1;
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ExternalSystemAdminService>();
            perLoc1 = await svc.RegisterAsync(
                "icums-gh", "for loc1 only", null,
                ExternalSystemBindingScope.PerLocation,
                new[] { loc1 });
        }

        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ExternalSystemAdminService>();
            var loc1Resolves = await svc.ResolveServingInstancesAsync(loc1);
            var loc2Resolves = await svc.ResolveServingInstancesAsync(loc2);

            Assert.Contains(perLoc1, loc1Resolves);
            Assert.DoesNotContain(perLoc1, loc2Resolves);
        }
    }

    [Fact]
    public async Task ResolveServingInstancesAsync_returns_SubsetOfLocations_match_for_member_locations()
    {
        var loc1 = await SeedLocationAsync("h1");
        var loc2 = await SeedLocationAsync("h2");
        var loc3 = await SeedLocationAsync("h3");

        Guid subsetInst;
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ExternalSystemAdminService>();
            subsetInst = await svc.RegisterAsync(
                "icums-gh", "for loc1+loc2 only", null,
                ExternalSystemBindingScope.SubsetOfLocations,
                new[] { loc1, loc2 });
        }

        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ExternalSystemAdminService>();
            Assert.Contains(subsetInst, await svc.ResolveServingInstancesAsync(loc1));
            Assert.Contains(subsetInst, await svc.ResolveServingInstancesAsync(loc2));
            Assert.DoesNotContain(subsetInst, await svc.ResolveServingInstancesAsync(loc3));
        }
    }

    [Fact]
    public async Task ResolveServingInstancesAsync_returns_Shared_for_every_location()
    {
        var loc1 = await SeedLocationAsync("i1");
        var loc2 = await SeedLocationAsync("i2");

        Guid sharedInst;
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ExternalSystemAdminService>();
            sharedInst = await svc.RegisterAsync(
                "icums-gh", "national", null,
                ExternalSystemBindingScope.Shared,
                Array.Empty<Guid>());
        }

        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ExternalSystemAdminService>();
            Assert.Contains(sharedInst, await svc.ResolveServingInstancesAsync(loc1));
            Assert.Contains(sharedInst, await svc.ResolveServingInstancesAsync(loc2));
        }
    }

    [Fact]
    public async Task ResolveServingInstancesAsync_excludes_inactive_instances()
    {
        var loc1 = await SeedLocationAsync("j1");

        Guid inactiveInst;
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ExternalSystemAdminService>();
            inactiveInst = await svc.RegisterAsync(
                "icums-gh", "to deactivate", null,
                ExternalSystemBindingScope.PerLocation,
                new[] { loc1 });
        }

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var instance = await db.ExternalSystemInstances.FirstAsync(e => e.Id == inactiveInst);
            instance.IsActive = false;
            await db.SaveChangesAsync();
        }

        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ExternalSystemAdminService>();
            var resolves = await svc.ResolveServingInstancesAsync(loc1);
            Assert.DoesNotContain(inactiveInst, resolves);
        }
    }
}
