namespace NickFinance.Coa;

/// <summary>
/// The default chart of accounts for a Ghana SME running NickERP. Numeric
/// codes follow the convention <c>1xxx assets, 2xxx liabilities, 3xxx equity,
/// 4xxx income, 5xxx cost of services, 6xxx operating expenses, 7xxx finance
/// costs, 8xxx tax expense, 9xxx clearing</c>. Site, project and cost-centre
/// stay as <em>dimensions</em> on each ledger line — they never appear in
/// account codes, so the chart stays under ~70 rows even with six sites.
/// </summary>
/// <remarks>
/// <para>
/// Ghana-specific accounts (NHIL, GETFund, COVID, WHT, SSNIT, PAYE) carry
/// codes in the <c>21xx</c> range and are flagged <see cref="Account.IsControl"/>
/// so the UI can warn when a free-form journal tries to hit them — the tax
/// engine and payroll integration own those legs.
/// </para>
/// <para>
/// This list is intended as a <em>baseline</em>. Each tenant adapts it
/// (adds bank accounts, splits expense categories) via the chart-of-accounts
/// admin UI; the seed runs once at install time and is idempotent on
/// <see cref="Account.Code"/>.
/// </para>
/// </remarks>
public static class GhanaStandardChart
{
    /// <summary>The 60+ account baseline. Pure data; no DI, no DB.</summary>
    public static IReadOnlyList<Account> Default { get; } = Build();

    private static List<Account> Build()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new List<(string Code, string Name, AccountType Type, string? Parent, bool IsControl, string? Description, string Currency)>
        {
            // -- ASSETS (1xxx) -----------------------------------------------------
            ("1000", "Current assets", AccountType.Asset, null, false, "Roll-up — has no postings of its own.", "*"),
            ("1010", "Cash on hand", AccountType.Asset, "1000", false, "Site cash boxes; site_id dimension required.", "GHS"),
            ("1020", "MoMo wallet — MTN", AccountType.Asset, "1000", false, "MTN MoMo merchant float.", "GHS"),
            ("1021", "MoMo wallet — Telecel (Vodafone)", AccountType.Asset, "1000", false, "Telecel MoMo merchant float.", "GHS"),
            ("1022", "MoMo wallet — AirtelTigo", AccountType.Asset, "1000", false, "AT MoMo merchant float.", "GHS"),
            ("1030", "Bank — GCB Operations", AccountType.Asset, "1000", false, "Primary cedi bank account.", "GHS"),
            ("1031", "Bank — Ecobank Operations", AccountType.Asset, "1000", false, "Secondary cedi bank account.", "GHS"),
            ("1032", "Bank — Stanbic Operations", AccountType.Asset, "1000", false, "Tertiary cedi bank account.", "GHS"),
            ("1040", "Bank — USD account", AccountType.Asset, "1000", false, "Cross-border float for Aflao/Elubo.", "USD"),
            ("1060", "Petty cash float", AccountType.Asset, "1000", true, "Per-custodian floats; managed by the petty-cash module.", "GHS"),
            ("1100", "Trade receivables — control", AccountType.Asset, "1000", true, "Control account; AR module owns postings.", "GHS"),
            ("1110", "Other receivables", AccountType.Asset, "1000", false, "Non-trade staff/intercompany receivables.", "GHS"),
            ("1200", "Inventory — consumables", AccountType.Asset, "1000", false, "Cleaning, stationery, scanner consumables.", "GHS"),
            ("1300", "Prepayments", AccountType.Asset, "1000", false, "Insurance, software subs paid forward.", "GHS"),
            ("1410", "VAT input recoverable", AccountType.Asset, "1000", true, "Input VAT recoverable from GRA — tax engine.", "GHS"),
            ("1420", "WHT receivable (suffered)", AccountType.Asset, "1000", true, "WHT customers withheld from us; tax engine.", "GHS"),
            ("1500", "Property, plant & equipment — at cost", AccountType.Asset, null, false, "Scanners, vehicles, IT, fittings — at cost.", "GHS"),
            ("1510", "Accumulated depreciation", AccountType.Asset, "1500", false, "Contra asset; carried as a debit-normal account with negative balance.", "GHS"),
            ("1900", "Suspense — debit clearing", AccountType.Asset, null, true, "Temporary parking for unmatched bank receipts; banking module sweeps.", "GHS"),

            // -- LIABILITIES (2xxx) ------------------------------------------------
            ("2000", "Trade payables — control", AccountType.Liability, null, true, "Control account; AP module owns postings.", "GHS"),
            ("2010", "Accrued expenses", AccountType.Liability, null, false, "Period-end accruals (utilities, professional fees).", "GHS"),
            ("2020", "MoMo settlement clearing", AccountType.Liability, null, true, "Hubtel-cleared but not yet auto-matched receipts; banking module sweeps.", "GHS"),
            ("2110", "VAT output payable", AccountType.Liability, null, true, "Output VAT due to GRA; tax engine.", "GHS"),
            ("2120", "NHIL payable", AccountType.Liability, null, true, "National Health Insurance Levy (2.5%); tax engine.", "GHS"),
            ("2130", "GETFund Levy payable", AccountType.Liability, null, true, "Ghana Education Trust Fund Levy (2.5%); tax engine.", "GHS"),
            ("2140", "COVID-19 Health Recovery Levy payable", AccountType.Liability, null, true, "COVID-19 levy (1%); tax engine.", "GHS"),
            ("2150", "WHT payable to GRA", AccountType.Liability, null, true, "Withholding tax we deducted at AP payment.", "GHS"),
            ("2160", "PAYE payable to GRA", AccountType.Liability, null, true, "Pay-As-You-Earn from payroll.", "GHS"),
            ("2170", "SSNIT Tier 1 payable (13.5%)", AccountType.Liability, null, true, "Employer + employee Tier 1 contribution.", "GHS"),
            ("2171", "SSNIT Tier 2 payable (5%)", AccountType.Liability, null, true, "Mandatory Tier 2 contribution.", "GHS"),
            ("2180", "Corporate income tax payable", AccountType.Liability, null, false, "Quarterly self-assessment.", "GHS"),
            ("2190", "FIC reportable transactions queue", AccountType.Liability, null, true, "Memo account for AML reporting (>GHS 50K cash).", "GHS"),
            ("2300", "Loans payable — short-term", AccountType.Liability, null, false, "Bank overdrafts, short-term facilities.", "GHS"),
            ("2310", "Loans payable — long-term", AccountType.Liability, null, false, "Term facilities >12 months.", "GHS"),
            ("2900", "Suspense — credit clearing", AccountType.Liability, null, true, "Temporary parking for unmatched bank payments.", "GHS"),

            // -- EQUITY (3xxx) -----------------------------------------------------
            ("3000", "Share capital", AccountType.Equity, null, false, "Issued ordinary shares.", "GHS"),
            ("3100", "Retained earnings", AccountType.Equity, null, false, "Accumulated post-tax earnings.", "GHS"),
            ("3900", "Current-year P&L summary", AccountType.Equity, null, true, "Year-end close clearing — receives the net of P&L accounts.", "GHS"),

            // -- INCOME (4xxx) -----------------------------------------------------
            ("4010", "Scan service fee revenue", AccountType.Income, null, false, "Customs scan fees per declaration.", "GHS"),
            ("4020", "Re-scan fee revenue", AccountType.Income, null, false, "Repeat-scan fees on customer dispute.", "GHS"),
            ("4030", "Storage fee revenue", AccountType.Income, null, false, "Container parking past 24h.", "GHS"),
            ("4040", "Other operational revenue", AccountType.Income, null, false, "Misc. service fees.", "GHS"),
            ("4100", "Interest income", AccountType.Income, null, false, "Bank deposits, treasury bills.", "GHS"),
            ("4200", "FX gain", AccountType.Income, null, false, "Realised + unrealised FX gains.", "GHS"),
            ("4900", "Other income", AccountType.Income, null, false, "Catch-all; review monthly.", "GHS"),

            // -- COST OF SERVICES (5xxx) ------------------------------------------
            ("5010", "Direct staff cost — operations", AccountType.Expense, null, false, "Operations team payroll allocation.", "GHS"),
            ("5020", "Equipment maintenance — scanners", AccountType.Expense, null, false, "Scanner OEM service contracts + parts.", "GHS"),
            ("5030", "Site running cost", AccountType.Expense, null, false, "Per-site utilities, security, cleaning.", "GHS"),
            ("5040", "Sub-contractor fees", AccountType.Expense, null, false, "Outsourced operations services.", "GHS"),

            // -- OPERATING EXPENSES (6xxx) ----------------------------------------
            ("6000", "Salaries & wages", AccountType.Expense, null, true, "Payroll module posts.", "GHS"),
            ("6010", "Bonuses & allowances", AccountType.Expense, null, true, "Payroll module posts.", "GHS"),
            ("6020", "Employer SSNIT contribution", AccountType.Expense, null, true, "Employer side of Tier 1 + Tier 2.", "GHS"),
            ("6100", "Rent — premises", AccountType.Expense, null, false, "Office and site leases.", "GHS"),
            ("6110", "Utilities", AccountType.Expense, null, false, "Electricity, water (head office).", "GHS"),
            ("6200", "IT software subscriptions", AccountType.Expense, null, false, "SaaS subscriptions.", "GHS"),
            ("6210", "Internet & data", AccountType.Expense, null, false, "ISP, mobile data.", "GHS"),
            ("6300", "Travel", AccountType.Expense, null, false, "Per-diem, transport.", "GHS"),
            ("6310", "Vehicle running", AccountType.Expense, null, false, "Fuel, maintenance, insurance.", "GHS"),
            ("6400", "Petty cash expense — general", AccountType.Expense, null, true, "Petty cash module posts; sub-categorised by dimension.", "GHS"),
            ("6410", "Office supplies", AccountType.Expense, null, false, "Stationery, consumables.", "GHS"),
            ("6500", "Professional fees", AccountType.Expense, null, false, "Consultants, advisers.", "GHS"),
            ("6510", "Audit fees", AccountType.Expense, null, false, "Statutory + internal audit.", "GHS"),
            ("6520", "Legal fees", AccountType.Expense, null, false, "Outside counsel.", "GHS"),
            ("6600", "Repairs & maintenance", AccountType.Expense, null, false, "Non-scanner equipment repairs.", "GHS"),
            ("6700", "Depreciation expense", AccountType.Expense, null, false, "Driven by fixed-assets module.", "GHS"),
            ("6800", "Bank charges", AccountType.Expense, null, false, "Wire fees, account maintenance.", "GHS"),
            ("6810", "FX loss", AccountType.Expense, null, false, "Realised + unrealised FX losses.", "GHS"),
            ("6900", "Other operating expense", AccountType.Expense, null, false, "Catch-all; review monthly.", "GHS"),

            // -- FINANCE COSTS (7xxx) ---------------------------------------------
            ("7000", "Interest expense", AccountType.Expense, null, false, "Loan interest.", "GHS"),
            ("7010", "Loan fees", AccountType.Expense, null, false, "Facility fees, arrangement fees.", "GHS"),
            // Wave 3A FX Phase 2 — period-end revaluation lands here. Distinct
            // from 4200 / 6810 (realised FX on settled transactions) so the
            // P&L breaks the two flavours apart for the auditor.
            ("7100", "FX revaluation gain", AccountType.Income, null, true, "Unrealised gain from period-end FX revaluation; FxRevaluationService posts.", "GHS"),
            ("7110", "FX revaluation loss", AccountType.Expense, null, true, "Unrealised loss from period-end FX revaluation; FxRevaluationService posts.", "GHS"),

            // -- TAX EXPENSE (8xxx) -----------------------------------------------
            ("8000", "Corporate income tax expense", AccountType.Expense, null, false, "Current-year corporate tax.", "GHS"),
            ("8010", "Deferred tax expense", AccountType.Expense, null, false, "IFRS deferred tax movements.", "GHS"),

            // -- CLEARING / SUSPENSE (9xxx) ---------------------------------------
            ("9000", "Year-end close clearing", AccountType.Equity, null, true, "Reciprocal of 3900 during close.", "GHS"),
            ("9100", "Inter-module suspense", AccountType.Asset, null, true, "Cross-module hand-off; modules sweep daily.", "GHS"),
        };

        return rows.Select(r => new Account
        {
            Code = r.Code,
            Name = r.Name,
            Type = r.Type,
            ParentCode = r.Parent,
            IsControl = r.IsControl,
            IsActive = true,
            CurrencyCode = r.Currency,
            Description = r.Description,
            CreatedAt = now,
            UpdatedAt = now,
            TenantId = 1
        }).ToList();
    }
}
