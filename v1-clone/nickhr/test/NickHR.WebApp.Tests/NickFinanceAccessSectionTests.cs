using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Identity;
using NickHR.WebApp.Components.Shared;
using NickHR.WebApp.Services;
using RoleNames = NickHR.WebApp.Identity.RoleNames;
using Xunit;

namespace NickHR.WebApp.Tests;

/// <summary>
/// Behavioural tests for the post-2026-04-30 single-grade
/// <c>NickFinanceAccessSection</c>. The component's UI is thin glue
/// around the <c>NickFinanceAccessSectionState</c> snapshot record and
/// the provisioning service; these tests pin both via reflection,
/// bypassing the Blazor render lifecycle entirely.
/// </summary>
[Collection("IdentityProvisioning")]
public sealed class NickFinanceAccessSectionTests
{
    private readonly IdentityProvisioningFixture _fx;
    private const long Tenant = 1L;

    public NickFinanceAccessSectionTests(IdentityProvisioningFixture fx) { _fx = fx; }

    private static async Task SeedRolesAsync(IdentityDbContext db)
    {
        // Idempotent insert of the 10 grade rows. Mirrors the bootstrap
        // CLI's SeedGradesFromCatalogAsync step. Ids start at 21 to leave
        // room for the legacy 1..6 + interim 7..20 ranges.
        var existing = await db.Roles.Select(r => r.Name).ToListAsync();
        short nextId = 21;
        foreach (var name in RoleNames.All)
        {
            if (existing.Contains(name)) { nextId++; continue; }
            db.Roles.Add(new Role
            {
                RoleId = nextId++,
                Name = name,
                Description = RoleNames.Descriptions.TryGetValue(name, out var d) ? d : null,
            });
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync();
    }

    private async Task<(IdentityDbContext db, IdentityProvisioningService prov)> NewProvisioningAsync()
    {
        var db = _fx.CreateIdentity();
        await SeedRolesAsync(db);
        var prov = new IdentityProvisioningService(
            db, NullLogger<IdentityProvisioningService>.Instance, TimeProvider.System);
        return (db, prov);
    }

    private sealed class StateHolder
    {
        public NickFinanceAccessSectionState? State { get; set; }
        public Task Receive(NickFinanceAccessSectionState s) { State = s; return Task.CompletedTask; }
    }

    private static async Task<(NickFinanceAccessSection section, StateHolder holder)>
        BuildSectionAsync(
            IIdentityProvisioningService prov,
            string email,
            string displayName,
            Guid? userId)
    {
        var section = new NickFinanceAccessSection
        {
            Email = email,
            DisplayName = displayName,
            UserId = userId,
            TenantId = Tenant,
        };

        SetInject(section, "Provisioning", prov);

        var holder = new StateHolder();
        section.OnStateChanged = Microsoft.AspNetCore.Components.EventCallback.Factory
            .Create<NickFinanceAccessSectionState>(holder, holder.Receive);

        await InvokeAsync(section, "OnInitializedAsync");

        return (section, holder);
    }

    private static void SetInject(object instance, string memberName, object value)
    {
        var prop = instance.GetType().GetProperty(memberName,
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
        prop.SetValue(instance, value);
    }

    private static async Task InvokeAsync(object instance, string method)
    {
        var m = instance.GetType().GetMethod(method,
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
        await (Task)m.Invoke(instance, null)!;
    }

    private static void SetField<T>(object instance, string fieldName, T value)
    {
        var f = instance.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        f.SetValue(instance, value);
    }

    private static T GetField<T>(object instance, string fieldName)
    {
        var f = instance.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (T)f.GetValue(instance)!;
    }

    private static async Task<NickFinanceAccessSectionState> EmitAsync(NickFinanceAccessSection section)
    {
        var holder = new StateHolder();
        section.OnStateChanged = Microsoft.AspNetCore.Components.EventCallback.Factory
            .Create<NickFinanceAccessSectionState>(holder, holder.Receive);
        await InvokeAsync(section, "EmitState");
        return holder.State!;
    }

    // ==================================================================
    // Lifecycle / load-existing
    // ==================================================================

    [Fact]
    public async Task OnInit_LoadsExistingOpsGrade()
    {
        var (db, prov) = await NewProvisioningAsync();
        await using var _ = db;

        var userId = await prov.ProvisionEmployeeAsync("user1@nickscan.com", "User One", Tenant);
        var grantedBy = await prov.ProvisionEmployeeAsync("admin@nickscan.com", "Admin", Tenant);
        await prov.GrantRoleAsync(userId, RoleNames.Bookkeeper, siteId: null, grantedBy, expiresAt: null);

        var (section, holder) = await BuildSectionAsync(prov, "user1@nickscan.com", "User One", userId);

        Assert.True(GetField<bool>(section, "_grantAccess"));
        Assert.Equal(RoleNames.Bookkeeper, GetField<string>(section, "_opsGrade"));
        Assert.Equal(string.Empty, GetField<string>(section, "_auditRole"));
        Assert.NotNull(holder.State);
        Assert.True(holder.State!.IsValid);
    }

    [Fact]
    public async Task OnInit_LoadsExistingAuditRole()
    {
        var (db, prov) = await NewProvisioningAsync();
        await using var _ = db;

        var userId = await prov.ProvisionEmployeeAsync("auditor@nickscan.com", "Auditor", Tenant);
        var grantedBy = await prov.ProvisionEmployeeAsync("admin2@nickscan.com", "Admin", Tenant);
        await prov.GrantRoleAsync(userId, RoleNames.InternalAuditor, siteId: null, grantedBy, expiresAt: null);

        var (section, _) = await BuildSectionAsync(prov, "auditor@nickscan.com", "Auditor", userId);

        Assert.Equal(string.Empty, GetField<string>(section, "_opsGrade"));
        Assert.Equal(RoleNames.InternalAuditor, GetField<string>(section, "_auditRole"));
    }

    [Fact]
    public async Task OnInit_NoExistingGrants_StartsEmpty()
    {
        var (db, prov) = await NewProvisioningAsync();
        await using var _ = db;

        var userId = await prov.ProvisionEmployeeAsync("blank@nickscan.com", "Blank", Tenant);

        var (section, _) = await BuildSectionAsync(prov, "blank@nickscan.com", "Blank", userId);

        Assert.False(GetField<bool>(section, "_grantAccess"));
        Assert.Equal(string.Empty, GetField<string>(section, "_opsGrade"));
        Assert.Equal(string.Empty, GetField<string>(section, "_auditRole"));
    }

    // ==================================================================
    // Validation: site-scoped grade
    // ==================================================================

    [Fact]
    public async Task SiteCashier_WithoutSite_IsInvalid()
    {
        var (db, prov) = await NewProvisioningAsync();
        await using var _ = db;
        var userId = await prov.ProvisionEmployeeAsync("u@nickscan.com", "U", Tenant);

        var (section, _) = await BuildSectionAsync(prov, "u@nickscan.com", "U", userId);
        SetField(section, "_grantAccess", true);
        await InvokeOpsGradeChanged(section, RoleNames.SiteCashier);

        var state = await EmitAsync(section);
        Assert.False(state.IsValid);
    }

    [Fact]
    public async Task SiteCashier_WithSite_IsValid()
    {
        var (db, prov) = await NewProvisioningAsync();
        await using var _ = db;
        var userId = await prov.ProvisionEmployeeAsync("u@nickscan.com", "U", Tenant);

        var (section, _) = await BuildSectionAsync(prov, "u@nickscan.com", "U", userId);
        SetField(section, "_grantAccess", true);
        await InvokeOpsGradeChanged(section, RoleNames.SiteCashier);
        SetField<Guid?>(section, "_siteId", new Guid("11111111-1111-1111-1111-000000000002"));

        var state = await EmitAsync(section);
        Assert.True(state.IsValid);
        Assert.Single(state.Grants);
        Assert.Equal(RoleNames.SiteCashier, state.Grants[0].RoleName);
    }

    // ==================================================================
    // Validation: ExternalAuditor expiry + firm
    // ==================================================================

    [Fact]
    public async Task ExternalAuditor_MissingExpiry_IsInvalid()
    {
        var (db, prov) = await NewProvisioningAsync();
        await using var _ = db;
        var userId = await prov.ProvisionEmployeeAsync("ea@nickscan.com", "EA", Tenant);

        var (section, _) = await BuildSectionAsync(prov, "ea@nickscan.com", "EA", userId);
        SetField(section, "_grantAccess", true);
        await InvokeAuditRoleChanged(section, RoleNames.ExternalAuditor);
        SetField(section, "_auditFirm", "PwC Ghana");

        var state = await EmitAsync(section);
        Assert.False(state.IsValid);
    }

    [Fact]
    public async Task ExternalAuditor_MissingFirm_IsInvalid()
    {
        var (db, prov) = await NewProvisioningAsync();
        await using var _ = db;
        var userId = await prov.ProvisionEmployeeAsync("ea@nickscan.com", "EA", Tenant);

        var (section, _) = await BuildSectionAsync(prov, "ea@nickscan.com", "EA", userId);
        SetField(section, "_grantAccess", true);
        await InvokeAuditRoleChanged(section, RoleNames.ExternalAuditor);
        SetField<DateTime?>(section, "_expiresAtDate", DateTime.Today.AddMonths(6));

        var state = await EmitAsync(section);
        Assert.False(state.IsValid);
    }

    [Fact]
    public async Task ExternalAuditor_AllFields_IsValid()
    {
        var (db, prov) = await NewProvisioningAsync();
        await using var _ = db;
        var userId = await prov.ProvisionEmployeeAsync("ea@nickscan.com", "EA", Tenant);

        var (section, _) = await BuildSectionAsync(prov, "ea@nickscan.com", "EA", userId);
        SetField(section, "_grantAccess", true);
        await InvokeAuditRoleChanged(section, RoleNames.ExternalAuditor);
        SetField<DateTime?>(section, "_expiresAtDate", DateTime.Today.AddMonths(6));
        SetField(section, "_auditFirm", "PwC Ghana");

        var state = await EmitAsync(section);
        Assert.True(state.IsValid);
        Assert.Single(state.Grants);
        Assert.Equal("PwC Ghana", state.Grants[0].AuditFirm);
        Assert.NotNull(state.Grants[0].ExpiresAt);
    }

    // ==================================================================
    // Mutual exclusion: ops vs audit
    // ==================================================================

    [Fact]
    public async Task PickingOpsGrade_ClearsAuditRole()
    {
        var (db, prov) = await NewProvisioningAsync();
        await using var _ = db;
        var userId = await prov.ProvisionEmployeeAsync("u@nickscan.com", "U", Tenant);

        var (section, _) = await BuildSectionAsync(prov, "u@nickscan.com", "U", userId);
        SetField(section, "_grantAccess", true);
        await InvokeAuditRoleChanged(section, RoleNames.InternalAuditor);
        Assert.Equal(RoleNames.InternalAuditor, GetField<string>(section, "_auditRole"));

        await InvokeOpsGradeChanged(section, RoleNames.Bookkeeper);

        Assert.Equal(RoleNames.Bookkeeper, GetField<string>(section, "_opsGrade"));
        Assert.Equal(string.Empty, GetField<string>(section, "_auditRole"));
    }

    [Fact]
    public async Task PickingAuditRole_ClearsOpsGrade()
    {
        var (db, prov) = await NewProvisioningAsync();
        await using var _ = db;
        var userId = await prov.ProvisionEmployeeAsync("u@nickscan.com", "U", Tenant);

        var (section, _) = await BuildSectionAsync(prov, "u@nickscan.com", "U", userId);
        SetField(section, "_grantAccess", true);
        await InvokeOpsGradeChanged(section, RoleNames.Bookkeeper);

        await InvokeAuditRoleChanged(section, RoleNames.InternalAuditor);

        Assert.Equal(string.Empty, GetField<string>(section, "_opsGrade"));
        Assert.Equal(RoleNames.InternalAuditor, GetField<string>(section, "_auditRole"));
    }

    // ==================================================================
    // ApplyAsync: grant + revoke
    // ==================================================================

    [Fact]
    public async Task Apply_SwapsOpsGrade_RevokingPrior()
    {
        var (db, prov) = await NewProvisioningAsync();
        await using var _ = db;
        var userId = await prov.ProvisionEmployeeAsync("swap@nickscan.com", "Swap", Tenant);
        var grantedBy = await prov.ProvisionEmployeeAsync("admin3@nickscan.com", "Admin", Tenant);
        await prov.GrantRoleAsync(userId, RoleNames.Bookkeeper, siteId: null, grantedBy, expiresAt: null);

        var (section, _) = await BuildSectionAsync(prov, "swap@nickscan.com", "Swap", userId);
        SetField(section, "_grantAccess", true);
        await InvokeOpsGradeChanged(section, RoleNames.Accountant);

        var ok = await section.ApplyAsync(userId, grantedBy);
        Assert.True(ok);

        var grants = await prov.ListRolesAsync(userId);
        Assert.Single(grants);
        Assert.Equal(RoleNames.Accountant, grants[0].RoleName);
    }

    [Fact]
    public async Task Apply_TogglingOff_RevokesEverything()
    {
        var (db, prov) = await NewProvisioningAsync();
        await using var _ = db;
        var userId = await prov.ProvisionEmployeeAsync("off@nickscan.com", "Off", Tenant);
        var grantedBy = await prov.ProvisionEmployeeAsync("admin4@nickscan.com", "Admin", Tenant);
        await prov.GrantRoleAsync(userId, RoleNames.Accountant, siteId: null, grantedBy, expiresAt: null);

        var (section, _) = await BuildSectionAsync(prov, "off@nickscan.com", "Off", userId);
        SetField(section, "_grantAccess", false);

        var ok = await section.ApplyAsync(userId, grantedBy);
        Assert.True(ok);

        var grants = await prov.ListRolesAsync(userId);
        Assert.Empty(grants);
    }

    // ==================================================================
    // Helpers — invoke private async methods on the section.
    // ==================================================================

    private static Task InvokeOpsGradeChanged(NickFinanceAccessSection section, string grade)
    {
        var m = typeof(NickFinanceAccessSection).GetMethod(
            "OnOpsGradeChangedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)m.Invoke(section, new object?[] { grade })!;
    }

    private static Task InvokeAuditRoleChanged(NickFinanceAccessSection section, string role)
    {
        var m = typeof(NickFinanceAccessSection).GetMethod(
            "OnAuditRoleChangedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)m.Invoke(section, new object?[] { role })!;
    }
}
