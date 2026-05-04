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
/// Sprint 14 / VP6 Phase D — tests for <see cref="AnalysisServiceBootstrap"/>.
/// Uses EF in-memory because the unit-under-test is the C# idempotency
/// + race-condition logic; the storage-layer unique-partial-index
/// enforcement is exercised by the Postgres-backed E2E test fixture.
/// </summary>
public sealed class AnalysisServiceBootstrapTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly long _tenantId = 1;

    public AnalysisServiceBootstrapTests()
    {
        var dbName = "bootstrap-" + Guid.NewGuid();
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
        services.AddAnalysisServiceBootstrap();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    public async Task Ensure_FirstCall_CreatesRow()
    {
        using var scope = _sp.CreateScope();
        var bootstrap = scope.ServiceProvider.GetRequiredService<IAnalysisServiceBootstrap>();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var created = await bootstrap.EnsureAllLocationsServiceAsync(_tenantId, createdByUserId: null);

        Assert.True(created);
        var rows = await db.AnalysisServices.AsNoTracking().ToListAsync();
        Assert.Single(rows);
        Assert.Equal("All Locations", rows[0].Name);
        Assert.True(rows[0].IsBuiltInAllLocations);
        Assert.Equal(_tenantId, rows[0].TenantId);
    }

    [Fact]
    public async Task Ensure_SecondCall_NoOps()
    {
        using var scope = _sp.CreateScope();
        var bootstrap = scope.ServiceProvider.GetRequiredService<IAnalysisServiceBootstrap>();

        var first = await bootstrap.EnsureAllLocationsServiceAsync(_tenantId, null);
        var second = await bootstrap.EnsureAllLocationsServiceAsync(_tenantId, null);

        Assert.True(first);
        Assert.False(second);

        using var scope2 = _sp.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<InspectionDbContext>();
        Assert.Single(await db.AnalysisServices.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Ensure_DifferentTenants_GetTheirOwnRow()
    {
        // Each tenant gets its own scope with the right tenant context.
        var sp = _sp; // use the same in-memory DB across the two scopes.

        using (var scope1 = sp.CreateScope())
        {
            ((TenantContext)scope1.ServiceProvider.GetRequiredService<ITenantContext>()).SetTenant(1);
            var b = scope1.ServiceProvider.GetRequiredService<IAnalysisServiceBootstrap>();
            await b.EnsureAllLocationsServiceAsync(1, null);
        }
        using (var scope2 = sp.CreateScope())
        {
            ((TenantContext)scope2.ServiceProvider.GetRequiredService<ITenantContext>()).SetTenant(2);
            var b = scope2.ServiceProvider.GetRequiredService<IAnalysisServiceBootstrap>();
            await b.EnsureAllLocationsServiceAsync(2, null);
        }
        using var verify = sp.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var rows = await db.AnalysisServices.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.TenantId == 1 && r.IsBuiltInAllLocations);
        Assert.Contains(rows, r => r.TenantId == 2 && r.IsBuiltInAllLocations);
    }

    [Fact]
    public async Task Ensure_RejectsZeroOrNegativeTenant()
    {
        using var scope = _sp.CreateScope();
        var bootstrap = scope.ServiceProvider.GetRequiredService<IAnalysisServiceBootstrap>();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => bootstrap.EnsureAllLocationsServiceAsync(0, null));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => bootstrap.EnsureAllLocationsServiceAsync(-1, null));
    }
}
