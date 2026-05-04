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
/// Sprint 14 / VP6 Phase D — service-layer tests for
/// <see cref="CaseClaimService"/>. Idempotency + race + release
/// authorization are C# logic; the unique partial index
/// <c>ux_case_claims_active_per_case</c> enforcement is exercised by the
/// Postgres E2E test (here we exercise the optimistic fast-path).
/// </summary>
public sealed class CaseClaimServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly long _tenantId = 1;

    public CaseClaimServiceTests()
    {
        var dbName = "claim-" + Guid.NewGuid();
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
        services.AddCaseClaimAndVisibility();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    private async Task<Guid> SeedServiceAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var id = Guid.NewGuid();
        db.AnalysisServices.Add(new AnalysisService
        {
            Id = id,
            Name = "svc-" + id.ToString("N")[..6],
            TenantId = _tenantId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Acquire_FreshCase_ReturnsClaimId()
    {
        var serviceId = await SeedServiceAsync();
        var caseId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
        var claimId = await svc.AcquireClaimAsync(caseId, serviceId, userId);
        Assert.NotEqual(Guid.Empty, claimId);
    }

    [Fact]
    public async Task Acquire_AlreadyClaimedByOtherUser_Throws()
    {
        var serviceId = await SeedServiceAsync();
        var caseId = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            await svc.AcquireClaimAsync(caseId, serviceId, userA);
        }
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            var ex = await Assert.ThrowsAsync<CaseAlreadyClaimedException>(
                () => svc.AcquireClaimAsync(caseId, serviceId, userB));
            Assert.Equal(userA, ex.ExistingClaimedByUserId);
            Assert.Equal(serviceId, ex.ExistingAnalysisServiceId);
        }
    }

    [Fact]
    public async Task Acquire_SameUserSameServiceTwice_Idempotent()
    {
        var serviceId = await SeedServiceAsync();
        var caseId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        Guid first;
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            first = await svc.AcquireClaimAsync(caseId, serviceId, userId);
        }
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            var second = await svc.AcquireClaimAsync(caseId, serviceId, userId);
            Assert.Equal(first, second); // Same claim id returned.
        }
    }

    [Fact]
    public async Task GetActiveClaim_ReturnsRowWhenActive()
    {
        var serviceId = await SeedServiceAsync();
        var caseId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            await svc.AcquireClaimAsync(caseId, serviceId, userId);
        }
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            var active = await svc.GetActiveClaimAsync(caseId);
            Assert.NotNull(active);
            Assert.Equal(userId, active!.ClaimedByUserId);
            Assert.Null(active.ReleasedAt);
        }
    }

    [Fact]
    public async Task Release_AsOwner_Succeeds()
    {
        var serviceId = await SeedServiceAsync();
        var caseId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        Guid claimId;
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            claimId = await svc.AcquireClaimAsync(caseId, serviceId, userId);
        }
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            await svc.ReleaseClaimAsync(claimId, userId, isAdmin: false);
        }
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            Assert.Null(await svc.GetActiveClaimAsync(caseId));
        }
    }

    [Fact]
    public async Task Release_AsNonOwnerNonAdmin_Throws()
    {
        var serviceId = await SeedServiceAsync();
        var caseId = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        Guid claimId;
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            claimId = await svc.AcquireClaimAsync(caseId, serviceId, userA);
        }
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => svc.ReleaseClaimAsync(claimId, userB, isAdmin: false));
        }
    }

    [Fact]
    public async Task Release_AsAdmin_Succeeds()
    {
        var serviceId = await SeedServiceAsync();
        var caseId = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        Guid claimId;
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            claimId = await svc.AcquireClaimAsync(caseId, serviceId, userA);
        }
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            await svc.ReleaseClaimAsync(claimId, userB, isAdmin: true); // Admin override.
        }
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            Assert.Null(await svc.GetActiveClaimAsync(caseId));
        }
    }

    [Fact]
    public async Task AfterRelease_AnotherUserCanReacquire()
    {
        var serviceId = await SeedServiceAsync();
        var caseId = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        Guid claimAId;
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            claimAId = await svc.AcquireClaimAsync(caseId, serviceId, userA);
            await svc.ReleaseClaimAsync(claimAId, userA, isAdmin: false);
        }
        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            var claimBId = await svc.AcquireClaimAsync(caseId, serviceId, userB);
            Assert.NotEqual(claimAId, claimBId);

            var active = await svc.GetActiveClaimAsync(caseId);
            Assert.NotNull(active);
            Assert.Equal(userB, active!.ClaimedByUserId);
        }
    }
}
