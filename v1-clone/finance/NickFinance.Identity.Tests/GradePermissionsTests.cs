using NickFinance.WebApp.Identity;
using Xunit;

namespace NickFinance.Identity.Tests;

/// <summary>
/// Pure (no DB) tests for the concentric grade hierarchy in
/// <see cref="GradePermissions"/>. Each test verifies one of three
/// invariants:
/// <list type="bullet">
///   <item><description><b>Strict containment.</b> Every grade's bundle is a
///     superset of the previous grade's bundle.</description></item>
///   <item><description><b>Permission count.</b> The plan-of-record numbers
///     line up with what <see cref="RoleNames.PermissionCount"/> reports.</description></item>
///   <item><description><b>SuperAdmin == ALL.</b> The apex grade carries
///     every permission in the catalogue.</description></item>
///   <item><description><b>Audit ring is read-only.</b> No write-verb
///     permission appears in either auditor bundle.</description></item>
/// </list>
/// </summary>
public class GradePermissionsTests
{
    private static readonly string[] _writeVerbs =
    {
        ".manage", ".run", ".post", ".issue", ".submit", ".approve",
        ".disburse", ".create", ".close", ".lock", ".register",
        ".depreciate", ".reject", ".record", ".import", ".email",
        ".draft", ".void", ".enter", ".export"
    };

    [Fact]
    public void Viewer_HasOnly_HomeView()
    {
        var p = GradePermissions.GetViewerPermissions();
        Assert.Equal(new[] { Permissions.HomeView }, p);
    }

    [Fact]
    public void SiteCashier_StrictlyContains_Viewer()
    {
        var parent = GradePermissions.GetViewerPermissions();
        var child = GradePermissions.GetSiteCashierPermissions();
        Assert.All(parent, x => Assert.Contains(x, child));
        Assert.True(child.Count > parent.Count);
    }

    [Fact]
    public void SiteSupervisor_StrictlyContains_SiteCashier()
    {
        var parent = GradePermissions.GetSiteCashierPermissions();
        var child = GradePermissions.GetSiteSupervisorPermissions();
        Assert.All(parent, x => Assert.Contains(x, child));
        Assert.True(child.Count > parent.Count);
    }

    [Fact]
    public void Bookkeeper_StrictlyContains_SiteSupervisor()
    {
        var parent = GradePermissions.GetSiteSupervisorPermissions();
        var child = GradePermissions.GetBookkeeperPermissions();
        Assert.All(parent, x => Assert.Contains(x, child));
        Assert.True(child.Count > parent.Count);
    }

    [Fact]
    public void Accountant_StrictlyContains_Bookkeeper()
    {
        var parent = GradePermissions.GetBookkeeperPermissions();
        var child = GradePermissions.GetAccountantPermissions();
        Assert.All(parent, x => Assert.Contains(x, child));
        Assert.True(child.Count > parent.Count);
    }

    [Fact]
    public void SeniorAccountant_StrictlyContains_Accountant()
    {
        var parent = GradePermissions.GetAccountantPermissions();
        var child = GradePermissions.GetSeniorAccountantPermissions();
        Assert.All(parent, x => Assert.Contains(x, child));
        Assert.True(child.Count > parent.Count);
    }

    [Fact]
    public void FinancialController_StrictlyContains_SeniorAccountant()
    {
        var parent = GradePermissions.GetSeniorAccountantPermissions();
        var child = GradePermissions.GetFinancialControllerPermissions();
        Assert.All(parent, x => Assert.Contains(x, child));
        Assert.True(child.Count > parent.Count);
    }

    [Fact]
    public void SuperAdmin_StrictlyContains_FinancialController()
    {
        var parent = GradePermissions.GetFinancialControllerPermissions();
        var child = GradePermissions.GetSuperAdminPermissions();
        Assert.All(parent, x => Assert.Contains(x, child));
        Assert.True(child.Count > parent.Count);
    }

    [Fact]
    public void SuperAdmin_Equals_AllPermissions()
    {
        var bundle = GradePermissions.GetSuperAdminPermissions();
        var all = Permissions.All;
        Assert.Equal(all.Count, bundle.Count);
        Assert.All(all, x => Assert.Contains(x, bundle));
    }

    [Fact]
    public void InternalAuditor_HasNoWriteVerbs()
    {
        var bundle = GradePermissions.GetInternalAuditorPermissions();
        foreach (var perm in bundle)
        {
            // ReportsExport is the one read-side "export" we deliberately allow.
            if (perm == Permissions.ReportsExport) continue;

            foreach (var verb in _writeVerbs)
            {
                Assert.False(perm.EndsWith(verb, StringComparison.Ordinal),
                    $"Auditor bundle contains write verb '{verb}' via '{perm}'.");
            }
        }
    }

    [Fact]
    public void ExternalAuditor_BundleEquals_InternalAuditor()
    {
        var ext = GradePermissions.GetExternalAuditorPermissions();
        var intl = GradePermissions.GetInternalAuditorPermissions();
        Assert.Equal(intl, ext);
    }

    [Theory]
    [InlineData(RoleNames.Viewer,              1)]
    [InlineData(RoleNames.SiteCashier,         6)]
    [InlineData(RoleNames.SiteSupervisor,      10)]
    [InlineData(RoleNames.Bookkeeper,          18)]
    [InlineData(RoleNames.Accountant,          26)]
    [InlineData(RoleNames.SeniorAccountant,    38)]
    [InlineData(RoleNames.FinancialController, 49)]
    [InlineData(RoleNames.SuperAdmin,          52)]
    [InlineData(RoleNames.InternalAuditor,     22)]
    [InlineData(RoleNames.ExternalAuditor,     22)]
    public void PermissionCounts_MatchPlanOfRecord(string roleName, int expected)
    {
        Assert.Equal(expected, GradePermissions.ForGrade(roleName).Count);
        Assert.Equal(expected, RoleNames.PermissionCount(roleName));
    }

    [Fact]
    public void ForGrade_UnknownRoleName_ReturnsEmpty()
    {
        // Legacy / mistyped names resolve to ZERO permissions — fail-closed.
        Assert.Empty(GradePermissions.ForGrade("Custodian"));
        Assert.Empty(GradePermissions.ForGrade("ApClerk"));
        Assert.Empty(GradePermissions.ForGrade(""));
    }

    [Fact]
    public void OpsLadder_Has_8_Grades_InOrder()
    {
        Assert.Equal(8, RoleNames.OpsLadder.Count);
        Assert.Equal(RoleNames.Viewer,              RoleNames.OpsLadder[0]);
        Assert.Equal(RoleNames.SuperAdmin,          RoleNames.OpsLadder[7]);
    }

    [Fact]
    public void AuditRing_Has_2_Grades()
    {
        Assert.Equal(2, RoleNames.AuditRing.Count);
        Assert.Contains(RoleNames.InternalAuditor, RoleNames.AuditRing);
        Assert.Contains(RoleNames.ExternalAuditor, RoleNames.AuditRing);
    }

    [Fact]
    public void All_Equals_OpsLadder_Plus_AuditRing()
    {
        Assert.Equal(10, RoleNames.All.Count);
        Assert.All(RoleNames.OpsLadder, x => Assert.Contains(x, RoleNames.All));
        Assert.All(RoleNames.AuditRing, x => Assert.Contains(x, RoleNames.All));
    }

    [Fact]
    public void RequiresSite_OnlyTrue_ForCashier_AndSupervisor()
    {
        Assert.True(RoleNames.RequiresSite(RoleNames.SiteCashier));
        Assert.True(RoleNames.RequiresSite(RoleNames.SiteSupervisor));
        Assert.False(RoleNames.RequiresSite(RoleNames.Viewer));
        Assert.False(RoleNames.RequiresSite(RoleNames.Bookkeeper));
        Assert.False(RoleNames.RequiresSite(RoleNames.SuperAdmin));
        Assert.False(RoleNames.RequiresSite(RoleNames.InternalAuditor));
        Assert.False(RoleNames.RequiresSite(RoleNames.ExternalAuditor));
    }

    [Fact]
    public void IsOpsRole_And_IsAuditRole_AreMutuallyExclusive()
    {
        foreach (var n in RoleNames.All)
        {
            // Every catalogue name is exactly one of (ops, audit).
            Assert.True(RoleNames.IsOpsRole(n) ^ RoleNames.IsAuditRole(n));
        }
    }
}
