using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Identity;
using NickHR.WebApp.Services;
using Xunit;

namespace NickHR.WebApp.Tests;

[Collection("IdentityProvisioning")]
public sealed class IdentityProvisioningServiceTests
{
    private readonly IdentityProvisioningFixture _fx;
    private const long Tenant = 1L;

    public IdentityProvisioningServiceTests(IdentityProvisioningFixture fx) { _fx = fx; }

    private IdentityProvisioningService CreateSut(IdentityDbContext db) =>
        new(db, NullLogger<IdentityProvisioningService>.Instance, TimeProvider.System);

    [Fact]
    public async Task ProvisionEmployee_CreatesNewRow()
    {
        await using var db = _fx.CreateIdentity();
        var sut = CreateSut(db);

        var id = await sut.ProvisionEmployeeAsync("Akosua.Mensah@nickscan.com", "Akosua Mensah", Tenant);

        Assert.NotEqual(Guid.Empty, id);
        var row = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.InternalUserId == id);
        // Email is stored lower-cased so the cross-app lookup stays case-insensitive.
        Assert.Equal("akosua.mensah@nickscan.com", row.Email);
        Assert.Equal("Akosua Mensah", row.DisplayName);
        Assert.Equal(Tenant, row.TenantId);
        Assert.Null(row.CfAccessSub); // populated only on first CF Access login
    }

    [Fact]
    public async Task ProvisionEmployee_IdempotentOnEmail()
    {
        await using var db = _fx.CreateIdentity();
        var sut = CreateSut(db);

        var id1 = await sut.ProvisionEmployeeAsync("Yaw.Osei@nickscan.com", "Yaw Osei", Tenant);
        var id2 = await sut.ProvisionEmployeeAsync("YAW.OSEI@NICKSCAN.COM", "Yaw Osei", Tenant);

        Assert.Equal(id1, id2);
        var rows = await db.Users.IgnoreQueryFilters().Where(u => u.Email == "yaw.osei@nickscan.com").ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task ProvisionEmployee_RefreshesDisplayName()
    {
        await using var db = _fx.CreateIdentity();
        var sut = CreateSut(db);

        var id = await sut.ProvisionEmployeeAsync("kofi@nickscan.com", "Kofi A.", Tenant);
        await sut.ProvisionEmployeeAsync("kofi@nickscan.com", "Kofi Annan", Tenant);

        var row = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.InternalUserId == id);
        Assert.Equal("Kofi Annan", row.DisplayName);
    }

    [Fact]
    public async Task GrantRole_AddsUserRole()
    {
        await using var db = _fx.CreateIdentity();
        var sut = CreateSut(db);

        var userId = await sut.ProvisionEmployeeAsync("ama@nickscan.com", "Ama", Tenant);
        var grantedBy = await sut.ProvisionEmployeeAsync("admin@nickscan.com", "Admin", Tenant);

        await sut.GrantRoleAsync(userId, RoleNames.Custodian, siteId: null, grantedBy, expiresAt: null);

        var grants = await sut.ListRolesAsync(userId);
        Assert.Single(grants);
        Assert.Equal(RoleNames.Custodian, grants[0].RoleName);
        Assert.Null(grants[0].SiteId);
    }

    [Fact]
    public async Task GrantRole_IdempotentOnTriple()
    {
        await using var db = _fx.CreateIdentity();
        var sut = CreateSut(db);

        var userId = await sut.ProvisionEmployeeAsync("kwame@nickscan.com", "Kwame", Tenant);
        var grantedBy = await sut.ProvisionEmployeeAsync("admin2@nickscan.com", "Admin", Tenant);
        var siteId = Guid.NewGuid();

        await sut.GrantRoleAsync(userId, RoleNames.Approver, siteId, grantedBy, expiresAt: null);
        await sut.GrantRoleAsync(userId, RoleNames.Approver, siteId, grantedBy, expiresAt: null);

        var rows = await db.UserRoles.IgnoreQueryFilters()
            .Where(ur => ur.UserId == userId).ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task GrantRole_DistinctSiteIdsAreSeparateRows()
    {
        await using var db = _fx.CreateIdentity();
        var sut = CreateSut(db);

        var userId = await sut.ProvisionEmployeeAsync("efua@nickscan.com", "Efua", Tenant);
        var grantedBy = await sut.ProvisionEmployeeAsync("admin3@nickscan.com", "Admin", Tenant);
        var siteA = Guid.NewGuid();
        var siteB = Guid.NewGuid();

        await sut.GrantRoleAsync(userId, RoleNames.Approver, siteA, grantedBy, expiresAt: null);
        await sut.GrantRoleAsync(userId, RoleNames.Approver, siteB, grantedBy, expiresAt: null);
        // Tenant-wide too — distinct from both site-scoped grants.
        await sut.GrantRoleAsync(userId, RoleNames.Approver, siteId: null, grantedBy, expiresAt: null);

        var rows = await db.UserRoles.IgnoreQueryFilters()
            .Where(ur => ur.UserId == userId).ToListAsync();
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task RevokeRole_DeletesUserRole()
    {
        await using var db = _fx.CreateIdentity();
        var sut = CreateSut(db);

        var userId = await sut.ProvisionEmployeeAsync("gifty@nickscan.com", "Gifty", Tenant);
        var grantedBy = await sut.ProvisionEmployeeAsync("admin4@nickscan.com", "Admin", Tenant);
        await sut.GrantRoleAsync(userId, RoleNames.Auditor, siteId: null, grantedBy, expiresAt: null);

        await sut.RevokeRoleAsync(userId, RoleNames.Auditor, siteId: null);

        var grants = await sut.ListRolesAsync(userId);
        Assert.Empty(grants);
    }

    [Fact]
    public async Task RevokeRole_NoOpWhenNotGranted()
    {
        await using var db = _fx.CreateIdentity();
        var sut = CreateSut(db);

        var userId = await sut.ProvisionEmployeeAsync("nana@nickscan.com", "Nana", Tenant);
        // Should not throw.
        await sut.RevokeRoleAsync(userId, RoleNames.Admin, siteId: null);
        var grants = await sut.ListRolesAsync(userId);
        Assert.Empty(grants);
    }

    [Fact]
    public async Task SetPrimaryPhone_UpsertsRow()
    {
        await using var db = _fx.CreateIdentity();
        var sut = CreateSut(db);

        var userId = await sut.ProvisionEmployeeAsync("kojo@nickscan.com", "Kojo", Tenant);

        await sut.SetPrimaryPhoneAsync(userId, "+233244000111");
        Assert.Equal("+233244000111", await sut.GetPrimaryPhoneAsync(userId));

        // Replace, not stack.
        await sut.SetPrimaryPhoneAsync(userId, "+233244000222");
        Assert.Equal("+233244000222", await sut.GetPrimaryPhoneAsync(userId));
        var rows = await db.UserPhones.IgnoreQueryFilters()
            .Where(p => p.UserId == userId).ToListAsync();
        Assert.Single(rows);

        // Empty clears entirely.
        await sut.SetPrimaryPhoneAsync(userId, "");
        Assert.Null(await sut.GetPrimaryPhoneAsync(userId));
    }

    [Fact]
    public async Task SetPrimaryPhone_RejectsInvalidE164()
    {
        await using var db = _fx.CreateIdentity();
        var sut = CreateSut(db);

        var userId = await sut.ProvisionEmployeeAsync("abena@nickscan.com", "Abena", Tenant);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.SetPrimaryPhoneAsync(userId, "024-not-e164"));
    }

    [Fact]
    public async Task GetUserIdByEmail_ReturnsNullWhenNotProvisioned()
    {
        await using var db = _fx.CreateIdentity();
        var sut = CreateSut(db);
        var id = await sut.GetUserIdByEmailAsync("ghost@nickscan.com", Tenant);
        Assert.Null(id);
    }
}
