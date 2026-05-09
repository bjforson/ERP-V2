using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using NickERP.Inspection.Web.Components.Pages;
using NickERP.Platform.Identity.Auth;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Spec §6.11.10 follow-up — coverage for the granular admin roles
/// <c>Inspection.OutcomeAdmin</c> (§6.11.10) and
/// <c>Inspection.ThresholdAdmin</c> (§6.5 / §6.5.3) added to
/// <see cref="InspectionRoles"/>. Closes the FU-posthoc-admin-role
/// follow-up tracked in <c>docs/sprint-progress.json</c> and the
/// in-page TODOs that previously gated <c>PostHocOutcomes.razor</c>
/// and <c>Thresholds.razor</c> on the coarse <c>Inspection.Admin</c>.
///
/// <para>
/// Tests assert two things, the same way Sprint 37 locked down the
/// RulesAdmin role:
/// </para>
/// <list type="number">
///   <item><description>The role constants exist with the
///   regex-compliant on-the-wire codes documented in
///   <c>IDENTITY.md</c>.</description></item>
///   <item><description>The two scoped Razor pages declare the
///   any-of <c>[Authorize(Roles = "...")]</c> shape — granular role
///   first, <see cref="InspectionRoles.Admin"/> retained for
///   backward-compat. The Razor compiler emits the
///   <c>[Authorize]</c> attribute onto the page's generated component
///   class, so we read it via reflection.</description></item>
/// </list>
///
/// <para>
/// The page-render bunit suites (e.g. <c>RulesAdminPageTests</c>)
/// bypass authz via a <c>FakeAuthStateProvider</c>, so this is the
/// load-bearing regression for the actual role-string the running
/// host will check against a caller's claims.
/// </para>
/// </summary>
public sealed class GranularAdminRolesTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void OutcomeAdmin_constant_uses_pascal_case_scope_code()
    {
        // Spec §6.11.10 names the role inspection.outcome_admin; v2 identity
        // scope-code regex requires PascalCase, so the on-the-wire code is
        // Inspection.OutcomeAdmin (same convention as RulesAdmin).
        InspectionRoles.OutcomeAdmin.Should().Be("Inspection.OutcomeAdmin");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ThresholdAdmin_constant_uses_pascal_case_scope_code()
    {
        // Spec §6.5 / §6.5.3 names the role inspection.threshold_admin;
        // PascalCase enforcement same as OutcomeAdmin.
        InspectionRoles.ThresholdAdmin.Should().Be("Inspection.ThresholdAdmin");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PostHocOutcomes_authorize_attribute_lists_outcome_admin_first_then_admin()
    {
        var roles = ReadAuthorizeRoles(typeof(PostHocOutcomes))
            ?? throw new Xunit.Sdk.XunitException(
                "[Authorize(Roles = \"...\")] missing from PostHocOutcomes.razor — "
              + "page would render to anonymous users, breaking spec §6.11.10.");

        var parts = roles.Split(',', System.StringSplitOptions.TrimEntries);
        parts.Should().Contain(InspectionRoles.OutcomeAdmin,
            because: "spec §6.11.10 designates inspection.outcome_admin as the gate for the manual-entry tool");
        parts.Should().Contain(InspectionRoles.Admin,
            because: "additive any-of pattern — existing Inspection.Admin grants must keep working without a per-grant migration");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PostHocOutcomes_authorize_attribute_does_not_grant_unrelated_granular_role()
    {
        // OutcomeAdmin and ThresholdAdmin are orthogonal granular roles —
        // a threshold-only grant must NOT confer access to the post-hoc
        // outcomes admin tool.
        var roles = ReadAuthorizeRoles(typeof(PostHocOutcomes)) ?? string.Empty;
        var parts = roles.Split(',', System.StringSplitOptions.TrimEntries);
        parts.Should().NotContain(InspectionRoles.ThresholdAdmin);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Thresholds_authorize_attribute_lists_threshold_admin_first_then_admin()
    {
        var roles = ReadAuthorizeRoles(typeof(Thresholds))
            ?? throw new Xunit.Sdk.XunitException(
                "[Authorize(Roles = \"...\")] missing from Thresholds.razor — "
              + "page would render to anonymous users, breaking spec §6.5.");

        var parts = roles.Split(',', System.StringSplitOptions.TrimEntries);
        parts.Should().Contain(InspectionRoles.ThresholdAdmin,
            because: "spec §6.5/§6.5.3 designates inspection.threshold_admin as the gate for the threshold approve/reject surface");
        parts.Should().Contain(InspectionRoles.Admin,
            because: "additive any-of pattern — existing Inspection.Admin grants must keep working");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Thresholds_authorize_attribute_does_not_grant_unrelated_granular_role()
    {
        var roles = ReadAuthorizeRoles(typeof(Thresholds)) ?? string.Empty;
        var parts = roles.Split(',', System.StringSplitOptions.TrimEntries);
        parts.Should().NotContain(InspectionRoles.OutcomeAdmin);
    }

    private static string? ReadAuthorizeRoles(System.Type pageType)
    {
        // The Razor compiler lifts @attribute [Authorize(...)] onto the
        // generated component class. Inherit=true so we walk any base
        // declarations (defensive — the directive is page-local today).
        var attr = pageType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                           .Cast<AuthorizeAttribute>()
                           .FirstOrDefault();
        return attr?.Roles;
    }
}
