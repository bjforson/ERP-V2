namespace NickFinance.WebApp.Identity;

/// <summary>
/// The full granular permission catalogue for NickFinance — 52 permission
/// strings across 11 categories. Stored as <c>const string</c>s so call
/// sites reference the exact name the DB seeded.
/// </summary>
/// <remarks>
/// <para>
/// 2026-04-30 — replaces the policy-keys-with-role-lists model
/// (<c>Policies.cs</c>) with the NSCIM-shaped concentric grade hierarchy
/// + permission-claim-based authorization. Pages now gate via
/// <c>[Authorize(Policy = Permissions.X)]</c>; the
/// <see cref="DynamicAuthorizationPolicyProvider"/> resolves each policy
/// name to a single-requirement permission policy at runtime, and
/// <see cref="PermissionAuthorizationHandler"/> looks up the user's
/// permissions via <see cref="IPermissionService"/> (backed by
/// <c>identity.role_permissions</c>).
/// </para>
/// <para>
/// Per-grade permission bundles live in <see cref="GradePermissions"/>;
/// each grade's <c>GetXPermissions()</c> method calls its parent's method
/// then <c>AddRange</c>s its delta — the NSCIM <c>PermissionSeeder</c>
/// pattern. The bundles seed into <c>identity.role_permissions</c> via
/// the bootstrap CLI's permission-seed step.
/// </para>
/// </remarks>
public static class Permissions
{
    // ==================== HOME / DASHBOARD ====================

    /// <summary>Read-only access to the home / dashboard page.</summary>
    public const string HomeView = "home.view";

    // ==================== PETTY CASH ====================

    public const string PettyVoucherView      = "petty.voucher.view";
    public const string PettyVoucherSubmit    = "petty.voucher.submit";
    public const string PettyVoucherApprove   = "petty.voucher.approve";
    public const string PettyVoucherReject    = "petty.voucher.reject";
    public const string PettyVoucherDisburse  = "petty.voucher.disburse";
    public const string PettyFloatView        = "petty.float.view";
    public const string PettyFloatCreate      = "petty.float.create";
    public const string PettyFloatClose       = "petty.float.close";
    public const string PettyCashcountView    = "petty.cashcount.view";
    public const string PettyCashcountRun     = "petty.cashcount.run";
    public const string PettyDelegationView   = "petty.delegation.view";
    public const string PettyDelegationManage = "petty.delegation.manage";
    public const string PettyRecurringView    = "petty.recurring.view";
    public const string PettyRecurringManage  = "petty.recurring.manage";

    // ==================== ACCOUNTS RECEIVABLE ====================

    public const string ArCustomerView   = "ar.customer.view";
    public const string ArCustomerManage = "ar.customer.manage";
    public const string ArInvoiceView    = "ar.invoice.view";
    public const string ArInvoiceDraft   = "ar.invoice.draft";
    public const string ArInvoiceIssue   = "ar.invoice.issue";
    public const string ArInvoiceVoid    = "ar.invoice.void";
    public const string ArReceiptView    = "ar.receipt.view";
    public const string ArReceiptRecord  = "ar.receipt.record";
    public const string ArDunningRun     = "ar.dunning.run";
    public const string ArStatementView  = "ar.statement.view";
    public const string ArStatementEmail = "ar.statement.email";

    // ==================== ACCOUNTS PAYABLE ====================

    public const string ApVendorView   = "ap.vendor.view";
    public const string ApVendorManage = "ap.vendor.manage";
    public const string ApBillView     = "ap.bill.view";
    public const string ApBillEnter    = "ap.bill.enter";
    public const string ApBillVoid     = "ap.bill.void";
    public const string ApPaymentRun   = "ap.payment.run";
    public const string ApWhtView      = "ap.wht.view";
    public const string ApWhtIssue     = "ap.wht.issue";

    // ==================== BANKING ====================

    public const string BankingStatementImport = "banking.statement.import";
    public const string BankingReconRun        = "banking.recon.run";
    public const string BankingFxrateView      = "banking.fxrate.view";
    public const string BankingFxrateManage    = "banking.fxrate.manage";
    public const string BankingFxrevalRun      = "banking.fxreval.run";

    // ==================== FIXED ASSETS ====================

    public const string AssetsView       = "assets.view";
    public const string AssetsRegister   = "assets.register";
    public const string AssetsDepreciate = "assets.depreciate";

    // ==================== BUDGETING ====================

    public const string BudgetView   = "budget.view";
    public const string BudgetManage = "budget.manage";
    public const string BudgetLock   = "budget.lock";

    // ==================== LEDGER / PERIOD ====================

    public const string JournalView = "journal.view";
    public const string JournalPost = "journal.post";
    public const string PeriodView  = "period.view";
    public const string PeriodClose = "period.close";

    // ==================== REPORTS ====================

    public const string ReportsView   = "reports.view";
    public const string ReportsExport = "reports.export";

    // ==================== iTaPS ====================

    public const string ItapsExport = "itaps.export";

    // ==================== IDENTITY / ADMIN ====================

    public const string UsersView   = "users.view";
    public const string UsersManage = "users.manage";
    public const string AuditView   = "audit.view";

    /// <summary>The full ordered list — used by the bootstrap CLI to seed <c>identity.permissions</c>.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        HomeView,
        PettyVoucherView, PettyVoucherSubmit, PettyVoucherApprove, PettyVoucherReject, PettyVoucherDisburse,
        PettyFloatView, PettyFloatCreate, PettyFloatClose,
        PettyCashcountView, PettyCashcountRun,
        PettyDelegationView, PettyDelegationManage,
        PettyRecurringView, PettyRecurringManage,
        ArCustomerView, ArCustomerManage,
        ArInvoiceView, ArInvoiceDraft, ArInvoiceIssue, ArInvoiceVoid,
        ArReceiptView, ArReceiptRecord,
        ArDunningRun, ArStatementView, ArStatementEmail,
        ApVendorView, ApVendorManage,
        ApBillView, ApBillEnter, ApBillVoid, ApPaymentRun,
        ApWhtView, ApWhtIssue,
        BankingStatementImport, BankingReconRun,
        BankingFxrateView, BankingFxrateManage, BankingFxrevalRun,
        AssetsView, AssetsRegister, AssetsDepreciate,
        BudgetView, BudgetManage, BudgetLock,
        JournalView, JournalPost,
        PeriodView, PeriodClose,
        ReportsView, ReportsExport,
        ItapsExport,
        UsersView, UsersManage, AuditView,
    };

    /// <summary>
    /// One-line description per permission, used by the bootstrap CLI when
    /// it seeds <c>identity.permissions</c> rows. HR / audit teams see
    /// these via the permission-preview block in the role-grant UI.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [HomeView]                = "Read-only access to the home page.",
        [PettyVoucherView]        = "View petty-cash vouchers.",
        [PettyVoucherSubmit]      = "Submit a draft petty-cash voucher.",
        [PettyVoucherApprove]     = "Approve a submitted voucher.",
        [PettyVoucherReject]      = "Reject a submitted voucher.",
        [PettyVoucherDisburse]    = "Disburse cash for an approved voucher.",
        [PettyFloatView]          = "View petty-cash floats.",
        [PettyFloatCreate]        = "Provision a new petty-cash float.",
        [PettyFloatClose]         = "Close an existing float.",
        [PettyCashcountView]      = "View daily cash-count reconciliations.",
        [PettyCashcountRun]       = "Record a physical cash count against a float.",
        [PettyDelegationView]     = "View approval delegations.",
        [PettyDelegationManage]   = "Create / revoke an approval delegation.",
        [PettyRecurringView]      = "View recurring voucher templates.",
        [PettyRecurringManage]    = "Manage recurring voucher templates.",
        [ArCustomerView]          = "View customer records.",
        [ArCustomerManage]        = "Create / edit / disable a customer.",
        [ArInvoiceView]           = "View AR invoices.",
        [ArInvoiceDraft]          = "Draft an AR invoice (does NOT issue or mint a GRA IRN).",
        [ArInvoiceIssue]          = "Issue a real GRA-IRN-bearing AR invoice — irreversible.",
        [ArInvoiceVoid]           = "Void an issued invoice.",
        [ArReceiptView]           = "View customer receipts.",
        [ArReceiptRecord]         = "Record a customer receipt against an invoice.",
        [ArDunningRun]            = "Run a dunning cycle against overdue customer balances.",
        [ArStatementView]         = "View customer statements.",
        [ArStatementEmail]        = "Email a customer statement to the customer's contact address.",
        [ApVendorView]            = "View vendor records.",
        [ApVendorManage]          = "Create / edit / disable a vendor.",
        [ApBillView]              = "View vendor bills.",
        [ApBillEnter]             = "Capture a bill against a vendor.",
        [ApBillVoid]              = "Void a captured bill.",
        [ApPaymentRun]            = "Authorise a payment run (cheque / transfer / momo).",
        [ApWhtView]               = "View WHT certificates.",
        [ApWhtIssue]              = "Issue a WHT certificate to a supplier (GRA filing artefact).",
        [BankingStatementImport]  = "Import a bank statement file (CSV / OFX / MT940).",
        [BankingReconRun]         = "Reconcile bank statement lines against ledger postings.",
        [BankingFxrateView]       = "View FX rate history.",
        [BankingFxrateManage]     = "Maintain FX rates (manual entry or BoG sync).",
        [BankingFxrevalRun]       = "Run period-end FX revaluation.",
        [AssetsView]              = "View the fixed-asset register.",
        [AssetsRegister]          = "Register or retire a fixed asset.",
        [AssetsDepreciate]        = "Run period depreciation against the asset register.",
        [BudgetView]              = "View budgets.",
        [BudgetManage]            = "Create / edit budget headers and lines.",
        [BudgetLock]              = "Lock a budget — no further edits allowed.",
        [JournalView]             = "Read-only view of the manual journal list and details.",
        [JournalPost]             = "Post a manual journal directly to the ledger.",
        [PeriodView]              = "View accounting period status.",
        [PeriodClose]             = "Soft- and hard-close a finance period.",
        [ReportsView]             = "Read-only access to financial reports (TB, BS, P&L, GL detail, etc.).",
        [ReportsExport]           = "Export reports to CSV / Excel.",
        [ItapsExport]             = "Export an iTaPS file to GRA.",
        [UsersView]               = "View NickFinance role assignments.",
        [UsersManage]             = "Grant / revoke NickFinance roles via the HR admin panel.",
        [AuditView]               = "Read-only access to the security audit log.",
    };
}
