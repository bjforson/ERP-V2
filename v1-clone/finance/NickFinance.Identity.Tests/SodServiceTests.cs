using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;
using NickFinance.WebApp.Identity;
using Xunit;

// NickFinance.WebApp.Identity also ships a RoleNames type — alias to it
// so the tests address the new 10-grade catalogue (8 ops + 2 audit), NOT
// the legacy 6-name <see cref="NickERP.Platform.Identity.RoleNames"/>.
using RoleNames = NickFinance.WebApp.Identity.RoleNames;

namespace NickFinance.Identity.Tests;

/// <summary>
/// Tests for the post-2026-04-30 shrunk <see cref="SodService"/>. After
/// the concentric-grade overhaul the service only enforces three rules
/// (the 5 hard-pair / 7 warn-pair catalogue from the flat-15-role model
/// is gone — composition makes those concerns moot):
/// <list type="number">
///   <item><description>Site-scoped grade requires <c>siteId</c>.</description></item>
///   <item><description>ExternalAuditor requires future <c>ExpiresAt</c> + <c>auditFirm</c>.</description></item>
///   <item><description>Audit-vs-ops exclusion (both directions).</description></item>
/// </list>
/// </summary>
[Collection("Sod")]
public class SodServiceTests
{
    private readonly SodFixture _fx;
    public SodServiceTests(SodFixture fx) => _fx = fx;

    // ==================================================================
    // 1) Site-scoped grade siteId rule
    // ==================================================================

    [Fact]
    public async Task Forbid_SiteCashier_WithoutSiteId()
    {
        await using var db = _fx.NewIdentity();
        await EnsureGradesSeededAsync(db);
        var sod = new SodService(db);

        var ex = await Assert.ThrowsAsync<SodViolationException>(() =>
            sod.ValidateGrantAsync(Guid.NewGuid(), RoleNames.SiteCashier, siteId: null));
        Assert.Contains("site-scoped grade", ex.Message);
    }

    [Fact]
    public async Task Forbid_SiteSupervisor_WithoutSiteId()
    {
        await using var db = _fx.NewIdentity();
        await EnsureGradesSeededAsync(db);
        var sod = new SodService(db);

        var ex = await Assert.ThrowsAsync<SodViolationException>(() =>
            sod.ValidateGrantAsync(Guid.NewGuid(), RoleNames.SiteSupervisor, siteId: null));
        Assert.Contains("site-scoped grade", ex.Message);
    }

    [Fact]
    public async Task Allow_SiteCashier_WithSiteId()
    {
        await using var db = _fx.NewIdentity();
        await EnsureGradesSeededAsync(db);
        var sod = new SodService(db);

        // Should not throw.
        await sod.ValidateGrantAsync(Guid.NewGuid(), RoleNames.SiteCashier, siteId: Guid.NewGuid());
    }

    [Fact]
    public async Task Allow_HQGrade_WithoutSiteId()
    {
        await using var db = _fx.NewIdentity();
        await EnsureGradesSeededAsync(db);
        var sod = new SodService(db);

        // Bookkeeper is HQ — no siteId required.
        await sod.ValidateGrantAsync(Guid.NewGuid(), RoleNames.Bookkeeper, siteId: null);
    }

    // ==================================================================
    // 2) ExternalAuditor expiry / firm rules
    // ==================================================================

    [Fact]
    public async Task Forbid_ExternalAuditor_WithoutExpiresAt()
    {
        await using var db = _fx.NewIdentity();
        await EnsureGradesSeededAsync(db);
        var sod = new SodService(db);

        var ex = await Assert.ThrowsAsync<SodViolationException>(() =>
            sod.ValidateGrantAsync(Guid.NewGuid(), RoleNames.ExternalAuditor,
                siteId: null, expiresAt: null, auditFirm: "PwC Ghana"));
        Assert.Contains("non-null ExpiresAt", ex.Message);
    }

    [Fact]
    public async Task Forbid_ExternalAuditor_WithPastExpiresAt()
    {
        await using var db = _fx.NewIdentity();
        await EnsureGradesSeededAsync(db);
        var sod = new SodService(db);

        var ex = await Assert.ThrowsAsync<SodViolationException>(() =>
            sod.ValidateGrantAsync(Guid.NewGuid(), RoleNames.ExternalAuditor,
                siteId: null, expiresAt: DateTimeOffset.UtcNow.AddDays(-1), auditFirm: "PwC Ghana"));
        Assert.Contains("must be in the future", ex.Message);
    }

    [Fact]
    public async Task Forbid_ExternalAuditor_WithoutAuditFirm()
    {
        await using var db = _fx.NewIdentity();
        await EnsureGradesSeededAsync(db);
        var sod = new SodService(db);

        var ex = await Assert.ThrowsAsync<SodViolationException>(() =>
            sod.ValidateGrantAsync(Guid.NewGuid(), RoleNames.ExternalAuditor,
                siteId: null, expiresAt: DateTimeOffset.UtcNow.AddMonths(3), auditFirm: ""));
        Assert.Contains("audit firm name", ex.Message);
    }

    [Fact]
    public async Task Allow_ExternalAuditor_WithAllRequiredFields()
    {
        await using var db = _fx.NewIdentity();
        await EnsureGradesSeededAsync(db);
        var sod = new SodService(db);

        // Should not throw.
        await sod.ValidateGrantAsync(Guid.NewGuid(), RoleNames.ExternalAuditor,
            siteId: null,
            expiresAt: DateTimeOffset.UtcNow.AddMonths(6),
            auditFirm: "PwC Ghana");
    }

    // ==================================================================
    // 3) Audit-vs-ops exclusion
    // ==================================================================

    [Fact]
    public async Task Forbid_GrantingAuditRole_ToUserWithOpsGrade()
    {
        await using var db = _fx.NewIdentity();
        await EnsureGradesSeededAsync(db);
        var sod = new SodService(db);

        var userId = Guid.NewGuid();
        await GrantAsync(db, userId, RoleNames.Bookkeeper, siteId: null);

        var ex = await Assert.ThrowsAsync<SodViolationException>(() =>
            sod.ValidateGrantAsync(userId, RoleNames.InternalAuditor, siteId: null));
        Assert.Contains("Auditors must not operate the system", ex.Message);
        Assert.Contains(RoleNames.Bookkeeper, ex.Message);
    }

    [Fact]
    public async Task Forbid_GrantingOpsGrade_ToUserWithAuditRole()
    {
        await using var db = _fx.NewIdentity();
        await EnsureGradesSeededAsync(db);
        var sod = new SodService(db);

        var userId = Guid.NewGuid();
        await GrantAsync(db, userId, RoleNames.InternalAuditor, siteId: null);

        var ex = await Assert.ThrowsAsync<SodViolationException>(() =>
            sod.ValidateGrantAsync(userId, RoleNames.Accountant, siteId: null));
        Assert.Contains("Auditors must not operate the system", ex.Message);
        Assert.Contains(RoleNames.InternalAuditor, ex.Message);
    }

    [Fact]
    public async Task Allow_OpsGradeReplacement_OnSameUser()
    {
        // Composition makes "user already holds another ops grade" structurally
        // a UI swap, not an SoD violation — the service-level guard fires only
        // for the audit-vs-ops cross. (HR's UserDialog runs the swap as a
        // single transactional revoke + grant.)
        await using var db = _fx.NewIdentity();
        await EnsureGradesSeededAsync(db);
        var sod = new SodService(db);

        var userId = Guid.NewGuid();
        await GrantAsync(db, userId, RoleNames.Bookkeeper, siteId: null);

        // Should not throw.
        await sod.ValidateGrantAsync(userId, RoleNames.Accountant, siteId: null);
    }

    // ==================================================================
    // 4) Legacy / unknown role names bypass SoD (fail-open at grant time
    //    because the role bundle is empty — they grant zero permissions).
    // ==================================================================

    [Fact]
    public async Task Allow_LegacyRoleName_BypassesSod()
    {
        await using var db = _fx.NewIdentity();
        await EnsureGradesSeededAsync(db);
        var sod = new SodService(db);

        // "Custodian" is a legacy 6-role name not in the new catalogue.
        // Should not throw.
        await sod.ValidateGrantAsync(Guid.NewGuid(), "Custodian", siteId: null);
    }

    // ==================================================================
    // 5) GetWarningsAsync returns empty in the v2 model
    // ==================================================================

    [Fact]
    public async Task Warnings_ReturnsEmpty_ForEveryGrade()
    {
        await using var db = _fx.NewIdentity();
        await EnsureGradesSeededAsync(db);
        var sod = new SodService(db);

        var warnings = await sod.GetWarningsAsync(Guid.NewGuid(), RoleNames.SuperAdmin, siteId: null);
        Assert.Empty(warnings);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static async Task EnsureGradesSeededAsync(IdentityDbContext db)
    {
        // The migration seeds the legacy 6 roles. The test code seeds the
        // 10 new grades (matching the ids the bootstrap CLI uses) so the
        // FK on user_roles.role_id resolves.
        var existing = await db.Roles.Select(r => r.Name).ToListAsync();
        var have = new HashSet<string>(existing, StringComparer.Ordinal);
        short id = 21; // legacy 6 occupy 1..6; new grades start at 21 by convention.
        foreach (var name in RoleNames.All)
        {
            if (!have.Contains(name))
            {
                db.Roles.Add(new Role
                {
                    RoleId = id,
                    Name = name,
                    Description = RoleNames.Descriptions[name],
                });
            }
            id++;
        }
        await db.SaveChangesAsync();
    }

    private static async Task GrantAsync(IdentityDbContext db, Guid userId, string roleName, Guid? siteId)
    {
        var role = await db.Roles.FirstAsync(r => r.Name == roleName);
        db.UserRoles.Add(new UserRole
        {
            UserId = userId,
            RoleId = role.RoleId,
            SiteId = siteId,
            GrantedByUserId = Guid.NewGuid(),
            GrantedAt = DateTimeOffset.UtcNow,
            ExpiresAt = null,
        });
        await db.SaveChangesAsync();
    }
}
