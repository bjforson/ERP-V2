namespace NickHR.WebApp.Identity;

/// <summary>
/// Mirror of the canonical NickFinance 10-grade catalogue (8 ops + 2
/// audit). The authoritative definition lives in
/// <c>finance/NickFinance.WebApp/Identity/RoleNames.cs</c>; this copy
/// is duplicated into NickHR purely to avoid coupling
/// <c>NickHR.WebApp.csproj</c> to <c>NickFinance.WebApp.csproj</c>.
/// </summary>
/// <remarks>
/// <para>
/// 2026-04-30 — Phase 2 of the role-overhaul wave. Replaced the 15
/// functional role mirror with the new concentric grade catalogue. The
/// strings MUST match the NickFinance copy verbatim — they are written
/// to <c>identity.roles.name</c> by the NickFinance bootstrap CLI and
/// any mismatch silently fails the grant lookup.
/// </para>
/// <para>
/// One grade per user. A user is on EITHER the ops ladder OR the audit
/// ring, never both — the access-section UI enforces the exclusion.
/// </para>
/// <para>
/// Follow-up: when the canonical catalogue moves to
/// <c>platform/NickERP.Platform.Identity</c>, this file should be
/// deleted and both NickHR and NickFinance.WebApp should reference the
/// platform copy directly.
/// </para>
/// </remarks>
public static class RoleNames
{
    // ==================== OPERATIONS LADDER (8) ====================
    // Each grade strictly contains every permission of the grade above
    // it (NSCIM concentric pattern).

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

    /// <summary>FinancialController + manage NickFinance access + view security audit log. Break-glass; two named individuals max.</summary>
    public const string SuperAdmin = "SuperAdmin";

    // ==================== AUDIT SIDE-CHANNEL (2) ====================
    // NOT in the ops ladder. A user is on EITHER the ops ladder OR the
    // audit ring — never both. The HR access-section enforces this at
    // the UI; SodService.ValidateGrantAsync enforces it again on save
    // as defence-in-depth.

    /// <summary>Read-only across journals + audit log + every operational page. No write verbs whatsoever.</summary>
    public const string InternalAuditor = "InternalAuditor";

    /// <summary>
    /// InternalAuditor + required <c>ExpiresAt</c> + required
    /// <c>audit_firm</c>. Time-boxed read-only access for external
    /// audit firms; auto-revokes on the expiry date.
    /// </summary>
    public const string ExternalAuditor = "ExternalAuditor";

    // ==================== ENUMERATIONS ====================

    /// <summary>The 8 ops-ladder grades, ordered junior → senior. Drives the dropdown ordering.</summary>
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

    /// <summary>Every grade name (ops + audit).</summary>
    public static IReadOnlyList<string> All { get; } = OpsLadder.Concat(AuditRing).ToArray();

    // ==================== HELPERS ====================

    /// <summary>True if the role is one of the 8 ops-ladder grades.</summary>
    public static bool IsOpsRole(string roleName) => OpsLadder.Contains(roleName);

    /// <summary>True if the role is one of the 2 audit grades.</summary>
    public static bool IsAuditRole(string roleName) =>
        roleName == InternalAuditor || roleName == ExternalAuditor;

    /// <summary>
    /// True if the grade requires a non-null <c>SiteId</c> on every
    /// grant. Only <see cref="SiteCashier"/> and <see cref="SiteSupervisor"/>
    /// are site-scoped — every other grade is HQ-only or read-only-everywhere.
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

    /// <summary>One-line descriptions surfaced by the section's permission preview block.</summary>
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
    /// Approximate permission count per grade — used by the section's
    /// "this grade grants N permissions" preview. Sum of the grade's
    /// inherited bundle.
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
