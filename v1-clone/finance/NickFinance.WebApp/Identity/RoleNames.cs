namespace NickFinance.WebApp.Identity;

/// <summary>
/// Canonical NickFinance grade catalogue — 8 concentric operations grades
/// (each strictly extending the previous) plus 2 side-channel audit
/// grades. **One grade per user.** Replaces the 2026-04-29 flat 15-role
/// model with the NSCIM-shaped concentric hierarchy on 2026-04-30.
/// </summary>
/// <remarks>
/// <para>
/// The full design rationale lives in
/// <c>C:\Users\Administrator\.claude\plans\lovely-sleeping-metcalfe.md</c>.
/// TL;DR:
/// <list type="bullet">
///   <item><description>Operations ladder: Viewer → SiteCashier → SiteSupervisor →
///   Bookkeeper → Accountant → SeniorAccountant → FinancialController →
///   SuperAdmin. Each grade inherits all permissions of the grade below.</description></item>
///   <item><description>Audit side-channel: InternalAuditor + ExternalAuditor.
///   Mutually exclusive with the ops ladder so audit independence holds.</description></item>
///   <item><description>Grade-level SoD evaporates because composition makes the
///   forbidden pairs structurally impossible (one grade slot per user).
///   The voucher-level action checks (approver≠submitter, approver≠disburser)
///   stay in <c>PettyCashService</c> as the second layer of defence.</description></item>
/// </list>
/// </para>
/// <para>
/// Per-grade permission bundles live in <see cref="GradePermissions"/>.
/// HR sees a "this grade grants N permissions" preview at grant time so
/// the implications are visible.
/// </para>
/// <para>
/// The legacy 6 names (<see cref="NickERP.Platform.Identity.RoleNames"/>:
/// <c>Custodian / Approver / SiteManager / FinanceLead / Auditor / Admin</c>)
/// and the interim 15 functional-role names (<c>SiteCustodian / ApClerk / …</c>)
/// remain in <c>identity.roles</c> for back-compat with any pre-existing
/// grants, but they are NOT in <c>identity.role_permissions</c> — so an
/// accidentally-still-granted legacy role resolves to ZERO permissions
/// and the user effectively has no NickFinance access. HR migrates each
/// grant via the inline UserDialog when convenient.
/// </para>
/// </remarks>
public static class RoleNames
{
    // ==================== OPERATIONS LADDER (8) ====================
    // Each grade inherits all permissions of the grade above it.

    /// <summary>Read-only access to the home / dashboard. The base of the ops ladder.</summary>
    public const string Viewer = "Viewer";

    /// <summary>Site-scoped: submit petty-cash vouchers + run cash counts. Inherits Viewer.</summary>
    public const string SiteCashier = "SiteCashier";

    /// <summary>Site-scoped: SiteCashier + approve site vouchers + view site reports. Inherits SiteCashier.</summary>
    public const string SiteSupervisor = "SiteSupervisor";

    /// <summary>HQ: SiteSupervisor + capture bills + draft invoices + master-data read. Inherits SiteSupervisor.</summary>
    public const string Bookkeeper = "Bookkeeper";

    /// <summary>Bookkeeper + record receipts + run depreciation + full reports + petty disburse. Inherits Bookkeeper.</summary>
    public const string Accountant = "Accountant";

    /// <summary>Accountant + issue/void invoices + payment runs + FX rates + bank rec + WHT + dunning + master-data write. Inherits Accountant.</summary>
    public const string SeniorAccountant = "SeniorAccountant";

    /// <summary>SeniorAccountant + manual journals + period close + FX revaluation + budget lock + iTaPS + recurring vouchers. Inherits SeniorAccountant.</summary>
    public const string FinancialController = "FinancialController";

    /// <summary>FinancialController + manage NickFinance access + view security audit log. Break-glass apex; two named individuals max.</summary>
    public const string SuperAdmin = "SuperAdmin";

    // ==================== AUDIT SIDE-CHANNEL (2) ====================
    // NOT in the ops ladder. A user is on EITHER the ops ladder OR the
    // audit ring — never both. Audit-vs-ops exclusion is enforced at grant
    // time by SodService.

    /// <summary>Read-only across journals + audit log + every operational page. No write verbs whatsoever.</summary>
    public const string InternalAuditor = "InternalAuditor";

    /// <summary>
    /// InternalAuditor + required <c>ExpiresAt</c> + required
    /// <c>audit_firm</c>. Time-boxed read-only access for external audit
    /// firms; auto-revokes on the expiry date.
    /// </summary>
    public const string ExternalAuditor = "ExternalAuditor";

    // ==================== ENUMERATIONS ====================

    /// <summary>The 8 ops-ladder grades, ordered junior → senior. Used by the UI dropdown ordering and the seniority comparison.</summary>
    public static IReadOnlyList<string> OpsLadder { get; } = new[]
    {
        Viewer, SiteCashier, SiteSupervisor, Bookkeeper,
        Accountant, SeniorAccountant, FinancialController, SuperAdmin,
    };

    /// <summary>The 2 audit-ring grades.</summary>
    public static IReadOnlyList<string> AuditRing { get; } = new[]
    {
        InternalAuditor, ExternalAuditor,
    };

    /// <summary>Every grade name (ops + audit) — used by the role-seed step in the bootstrap CLI.</summary>
    public static IReadOnlyList<string> All { get; } = OpsLadder.Concat(AuditRing).ToArray();

    // ==================== HELPERS ====================

    /// <summary>True if the role is one of the 8 ops-ladder grades.</summary>
    public static bool IsOpsRole(string roleName) => OpsLadder.Contains(roleName);

    /// <summary>True if the role is one of the 2 audit grades.</summary>
    public static bool IsAuditRole(string roleName) =>
        roleName == InternalAuditor || roleName == ExternalAuditor;

    /// <summary>
    /// True if the grade requires a non-null <c>SiteId</c> on every grant.
    /// Only <see cref="SiteCashier"/> and <see cref="SiteSupervisor"/> are
    /// site-scoped — every other grade is HQ-only or read-only-everywhere.
    /// </summary>
    public static bool RequiresSite(string roleName) =>
        roleName == SiteCashier || roleName == SiteSupervisor;

    /// <summary>
    /// Junior → senior rank for ops grades (Viewer = 0, SuperAdmin = 7).
    /// Returns -1 for audit grades and unknown names.
    /// </summary>
    public static int OpsRank(string roleName)
    {
        for (var i = 0; i < OpsLadder.Count; i++)
        {
            if (OpsLadder[i] == roleName) return i;
        }
        return -1;
    }

    // ==================== METADATA ====================

    /// <summary>Descriptions surfaced by the bootstrap CLI seed step + the HR grant UI's permission preview.</summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Viewer]              = "Read-only access to the home page.",
        [SiteCashier]         = "Site-scoped: submit petty-cash vouchers and run cash counts.",
        [SiteSupervisor]      = "Site-scoped: SiteCashier + approve site vouchers and view site reports.",
        [Bookkeeper]          = "HQ-side bookkeeper: SiteSupervisor + capture bills, draft invoices, master-data read.",
        [Accountant]          = "Bookkeeper + record receipts, run depreciation, full reports, petty-cash disburse.",
        [SeniorAccountant]    = "Accountant + issue/void invoices, payment runs, FX rates, bank reconciliation, WHT certificates, dunning, master-data write.",
        [FinancialController] = "SeniorAccountant + manual journals, period close, FX revaluation, budget lock, iTaPS export, recurring vouchers.",
        [SuperAdmin]          = "FinancialController + manage NickFinance access, view security audit log. Break-glass; two named individuals max.",
        [InternalAuditor]     = "Read-only across journals, audit log, and every operational page. No write verbs.",
        [ExternalAuditor]     = "Time-boxed read-only access for external audit firms (ExpiresAt + audit_firm required).",
    };

    /// <summary>
    /// Approximate permission count per grade — used by the HR grant UI's
    /// "this grade grants N permissions" preview. Sum of the grade's
    /// inherited bundle (see <see cref="GradePermissions"/>).
    /// </summary>
    public static int PermissionCount(string roleName) => roleName switch
    {
        Viewer              => 1,
        SiteCashier         => 6,
        SiteSupervisor      => 10,
        Bookkeeper          => 18,
        Accountant          => 26,
        SeniorAccountant    => 38,
        FinancialController => 49,
        SuperAdmin          => 52,
        InternalAuditor     => 22,
        ExternalAuditor     => 22,
        _                   => 0,
    };
}
