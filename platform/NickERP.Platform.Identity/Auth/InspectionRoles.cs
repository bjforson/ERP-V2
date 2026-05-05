namespace NickERP.Platform.Identity.Auth;

/// <summary>
/// Stable role / scope constants emitted by the Inspection module for
/// use in <c>[Authorize(Roles = ...)]</c> attributes and policy checks.
/// Mirrors the pattern in
/// <c>NickERP.NickFinance.Core.Roles.PettyCashRoles</c> (no project
/// reference; symbol named for documentation only) — the constants
/// live in one place so renames are caught at compile time and the
/// audit register can reference them by symbol.
///
/// <para>
/// Per <c>IDENTITY.md</c>, every role string is also a scope code in
/// the <c>identity.app_scopes</c> table. The auth handler mirrors each
/// granted <see cref="Entities.AppScope.Code"/> as a
/// <see cref="System.Security.Claims.ClaimTypes.Role"/> claim, so
/// <c>[Authorize(Roles = InspectionRoles.RulesAdmin)]</c> works without
/// custom policy plumbing.
/// </para>
///
/// <para>
/// Codes follow the platform's scope-naming regex
/// <c>^[A-Z][A-Za-z]+(\.[A-Z][A-Za-z]+)+$</c> — at least two
/// dot-separated PascalCase segments, namespaced under
/// <c>Inspection.</c>. The codes here are NOT seeded automatically —
/// a tenant admin must INSERT a row into <c>identity.app_scopes</c>
/// before the corresponding grant can be issued. Documented in the
/// per-tenant onboarding runbook.
/// </para>
/// </summary>
public static class InspectionRoles
{
    /// <summary>
    /// Generic Inspection administrator. Held by the operator-team
    /// admins who own ICUMS submission queues, post-hoc outcomes,
    /// scanner thresholds, analysis-services config, and other
    /// admin-side surfaces. Coarse-grained — most admin pages gate on
    /// this role today.
    /// </summary>
    public const string Admin = "Inspection.Admin";

    /// <summary>
    /// Sprint 37 / Sprint 28 follow-up FU-rules-admin-role — dedicated
    /// role for the validation-rules admin pages
    /// (<c>RulesAdmin.razor</c> + <c>RuleDetail.razor</c>). Allows a
    /// finer-grained grant than <see cref="Admin"/> so a customs
    /// analyst can be given rules-admin without simultaneously
    /// granting threshold-admin / ICUMS-admin / outcome-admin.
    ///
    /// <para>
    /// The rules-admin Razor pages currently accept BOTH
    /// <see cref="RulesAdmin"/> and <see cref="Admin"/> via the
    /// any-of pattern <c>[Authorize(Roles = "Inspection.RulesAdmin,Inspection.Admin")]</c>.
    /// Existing <see cref="Admin"/> grants therefore continue to work
    /// — this addition is purely additive.
    /// </para>
    /// </summary>
    public const string RulesAdmin = "Inspection.RulesAdmin";
}
