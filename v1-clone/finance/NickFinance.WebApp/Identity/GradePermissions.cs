namespace NickFinance.WebApp.Identity;

/// <summary>
/// Per-grade permission bundles. Each ops-ladder grade's
/// <c>GetXPermissions()</c> method calls the parent's method then
/// <c>AddRange</c>s its delta — the NSCIM <c>PermissionSeeder</c> pattern.
/// The bundles seed into <c>identity.role_permissions</c> via the
/// bootstrap CLI's permission-grant step.
/// </summary>
/// <remarks>
/// <para>
/// The composition pattern means a higher grade ALWAYS holds every
/// permission a lower grade does. <see cref="RoleNames.SuperAdmin"/>'s
/// bundle == every permission in <see cref="Permissions.All"/>.
/// </para>
/// <para>
/// Audit grades (<see cref="RoleNames.InternalAuditor"/> /
/// <see cref="RoleNames.ExternalAuditor"/>) do NOT inherit from the ops
/// ladder. They get their own bundle of read-only-everywhere permissions
/// — every <c>*.view</c> permission across the system plus
/// <see cref="Permissions.ReportsView"/> /
/// <see cref="Permissions.ReportsExport"/> /
/// <see cref="Permissions.AuditView"/>. No write verbs at all.
/// </para>
/// </remarks>
public static class GradePermissions
{
    // ==================== OPS LADDER (concentric) ====================

    /// <summary>1 permission. The base of the ops ladder.</summary>
    public static IReadOnlyList<string> GetViewerPermissions() => new List<string>
    {
        Permissions.HomeView,
    };

    /// <summary>6 permissions = Viewer (1) + 5 site-cashier permissions.</summary>
    public static IReadOnlyList<string> GetSiteCashierPermissions()
    {
        var p = new List<string>(GetViewerPermissions());
        p.AddRange(new[]
        {
            Permissions.PettyVoucherView,
            Permissions.PettyVoucherSubmit,
            Permissions.PettyFloatView,
            Permissions.PettyCashcountView,
            Permissions.PettyCashcountRun,
        });
        return p;
    }

    /// <summary>10 permissions = SiteCashier (6) + 4 site-supervisor permissions.</summary>
    public static IReadOnlyList<string> GetSiteSupervisorPermissions()
    {
        var p = new List<string>(GetSiteCashierPermissions());
        p.AddRange(new[]
        {
            Permissions.PettyVoucherApprove,
            Permissions.PettyVoucherReject,
            Permissions.ArStatementView,
            Permissions.ReportsView,
        });
        return p;
    }

    /// <summary>18 permissions = SiteSupervisor (10) + 8 bookkeeper permissions.</summary>
    public static IReadOnlyList<string> GetBookkeeperPermissions()
    {
        var p = new List<string>(GetSiteSupervisorPermissions());
        p.AddRange(new[]
        {
            Permissions.ApBillView,
            Permissions.ApBillEnter,
            Permissions.ArInvoiceView,
            Permissions.ArInvoiceDraft,
            Permissions.ApVendorView,
            Permissions.ArCustomerView,
            Permissions.ArReceiptView,
            Permissions.ApWhtView,
        });
        return p;
    }

    /// <summary>26 permissions = Bookkeeper (18) + 8 accountant permissions.</summary>
    public static IReadOnlyList<string> GetAccountantPermissions()
    {
        var p = new List<string>(GetBookkeeperPermissions());
        p.AddRange(new[]
        {
            Permissions.ArReceiptRecord,
            Permissions.PettyVoucherDisburse,
            Permissions.PettyDelegationView,
            Permissions.PettyDelegationManage,
            Permissions.AssetsView,
            Permissions.AssetsDepreciate,
            Permissions.BankingFxrateView,
            Permissions.ReportsExport,
        });
        return p;
    }

    /// <summary>38 permissions = Accountant (26) + 12 senior-accountant permissions.</summary>
    public static IReadOnlyList<string> GetSeniorAccountantPermissions()
    {
        var p = new List<string>(GetAccountantPermissions());
        p.AddRange(new[]
        {
            Permissions.ArInvoiceIssue,
            Permissions.ArInvoiceVoid,
            Permissions.ApBillVoid,
            Permissions.ApPaymentRun,
            Permissions.ApVendorManage,
            Permissions.ArCustomerManage,
            Permissions.ApWhtIssue,
            Permissions.ArDunningRun,
            Permissions.ArStatementEmail,
            Permissions.BankingStatementImport,
            Permissions.BankingReconRun,
            Permissions.BankingFxrateManage,
        });
        return p;
    }

    /// <summary>49 permissions = SeniorAccountant (38) + 11 controller permissions.</summary>
    public static IReadOnlyList<string> GetFinancialControllerPermissions()
    {
        var p = new List<string>(GetSeniorAccountantPermissions());
        p.AddRange(new[]
        {
            Permissions.JournalView,
            Permissions.JournalPost,
            Permissions.PeriodView,
            Permissions.PeriodClose,
            Permissions.BankingFxrevalRun,
            Permissions.BudgetView,
            Permissions.BudgetManage,
            Permissions.BudgetLock,
            Permissions.AssetsRegister,
            Permissions.ItapsExport,
            Permissions.PettyFloatCreate,
            Permissions.PettyFloatClose,
            Permissions.PettyRecurringView,
            Permissions.PettyRecurringManage,
        });
        return p;
    }

    /// <summary>52 permissions = FinancialController (49) + 3 admin permissions = ALL.</summary>
    public static IReadOnlyList<string> GetSuperAdminPermissions()
    {
        var p = new List<string>(GetFinancialControllerPermissions());
        p.AddRange(new[]
        {
            Permissions.UsersView,
            Permissions.UsersManage,
            Permissions.AuditView,
        });
        return p;
    }

    // ==================== AUDIT SIDE-CHANNEL ====================

    /// <summary>
    /// 22 read-only permissions: every <c>*.view</c> permission plus
    /// <see cref="Permissions.ReportsView"/>, <see cref="Permissions.ReportsExport"/>,
    /// <see cref="Permissions.AuditView"/>. Deliberately excludes every
    /// write verb (<c>*.manage</c>, <c>*.run</c>, <c>*.post</c>,
    /// <c>*.issue</c>, <c>*.submit</c>, <c>*.approve</c>, <c>*.disburse</c>,
    /// <c>*.create</c>, <c>*.close</c>, <c>*.lock</c>) so an auditor cannot
    /// touch any data even by accident.
    /// </summary>
    public static IReadOnlyList<string> GetInternalAuditorPermissions() => new List<string>
    {
        Permissions.HomeView,
        Permissions.PettyVoucherView,
        Permissions.PettyFloatView,
        Permissions.PettyCashcountView,
        Permissions.PettyDelegationView,
        Permissions.PettyRecurringView,
        Permissions.ArCustomerView,
        Permissions.ArInvoiceView,
        Permissions.ArReceiptView,
        Permissions.ArStatementView,
        Permissions.ApVendorView,
        Permissions.ApBillView,
        Permissions.ApWhtView,
        Permissions.BankingFxrateView,
        Permissions.AssetsView,
        Permissions.BudgetView,
        Permissions.JournalView,
        Permissions.PeriodView,
        Permissions.ReportsView,
        Permissions.ReportsExport,
        Permissions.AuditView,
        Permissions.UsersView,
    };

    /// <summary>
    /// Same permission bundle as <see cref="GetInternalAuditorPermissions"/>;
    /// the difference is at the <c>UserRole</c> grant level (the row carries
    /// non-null <c>ExpiresAt</c> + non-null <c>audit_firm</c>, validated by
    /// <see cref="ISodService"/>).
    /// </summary>
    public static IReadOnlyList<string> GetExternalAuditorPermissions() =>
        GetInternalAuditorPermissions();

    // ==================== DISPATCH ====================

    /// <summary>
    /// Resolve a grade name to its permission bundle. Unknown / legacy
    /// names return an empty list — accidentally-still-granted legacy
    /// roles resolve to ZERO permissions, which is the right fail-closed
    /// default during the migration window.
    /// </summary>
    public static IReadOnlyList<string> ForGrade(string roleName) => roleName switch
    {
        RoleNames.Viewer              => GetViewerPermissions(),
        RoleNames.SiteCashier         => GetSiteCashierPermissions(),
        RoleNames.SiteSupervisor      => GetSiteSupervisorPermissions(),
        RoleNames.Bookkeeper          => GetBookkeeperPermissions(),
        RoleNames.Accountant          => GetAccountantPermissions(),
        RoleNames.SeniorAccountant    => GetSeniorAccountantPermissions(),
        RoleNames.FinancialController => GetFinancialControllerPermissions(),
        RoleNames.SuperAdmin          => GetSuperAdminPermissions(),
        RoleNames.InternalAuditor     => GetInternalAuditorPermissions(),
        RoleNames.ExternalAuditor     => GetExternalAuditorPermissions(),
        _                             => Array.Empty<string>(),
    };
}
