using Microsoft.EntityFrameworkCore;
using NickFinance.AP;
using NickFinance.AR;
using NickFinance.Banking;
using NickFinance.Budgeting;
using NickFinance.Coa;
using NickFinance.FixedAssets;
using NickERP.Platform.Identity;
using NickFinance.Ledger;
using NickFinance.PettyCash;

namespace NickFinance.Database.Bootstrap;

/// <summary>
/// CLI entry point. Applies EF migrations for both the Ledger and Petty
/// Cash schemas, applies the Postgres-level triggers (balance + append-only),
/// and optionally seeds the Ghana standard chart of accounts.
/// </summary>
/// <remarks>
/// Idempotent: safe to re-run. Existing migrations are skipped, existing
/// triggers are dropped + recreated, existing CoA accounts are left
/// untouched.
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var conn = ResolveConnectionString(args);
        if (conn is null)
        {
            Console.Error.WriteLine("usage: NickFinance.Database.Bootstrap --conn <postgres-connection-string> [--seed-coa] [--smoke-test] [--skip-migrations]");
            Console.Error.WriteLine("  or: NICKERP_FINANCE_DB_CONNECTION env var + flags");
            return 1;
        }

        var seedCoa = args.Any(a => string.Equals(a, "--seed-coa", StringComparison.OrdinalIgnoreCase));
        var smokeTest = args.Any(a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase));
        var skipMigrations = args.Any(a => string.Equals(a, "--skip-migrations", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine("NickFinance bootstrap");
        Console.WriteLine($"  Target: {RedactPassword(conn)}");
        Console.WriteLine($"  Seed CoA: {(seedCoa ? "yes" : "no (run again with --seed-coa)")}");
        Console.WriteLine($"  Smoke test: {(smokeTest ? "yes" : "no (run again with --smoke-test)")}");
        if (skipMigrations) Console.WriteLine("  Skip migrations: yes (only running post-migration steps)");
        Console.WriteLine();

        try
        {
            if (!skipMigrations)
            {
                await ApplyLedgerMigrationsAsync(conn);
                await ApplyPettyCashMigrationsAsync(conn);
                await ApplyCoaMigrationsAsync(conn);
                await ApplyArMigrationsAsync(conn);
                await ApplyApMigrationsAsync(conn);
                await ApplyBankingMigrationsAsync(conn);
                await ApplyFixedAssetsMigrationsAsync(conn);
                await ApplyBudgetingMigrationsAsync(conn);
                await ApplyIdentityMigrationsAsync(conn);
                await SeedGradesFromCatalogAsync(conn);
                await SeedPermissionCatalogAsync(conn);
                await SeedRolePermissionGrantsAsync(conn);
                await ApplySchemaTriggersAsync(conn);
                await ApplyRlsPoliciesAsync(conn);
                await SeedInitialFxRatesAsync(conn);
            }
            if (seedCoa) await SeedGhanaChartAsync(conn);
            if (smokeTest)
            {
                var rc = await SmokeTest.RunAsync(conn);
                if (rc != 0) return rc;
            }

            Console.WriteLine();
            Console.WriteLine("Bootstrap complete. All NickFinance schemas are ready.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("BOOTSTRAP FAILED:");
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    // -----------------------------------------------------------------
    // Steps
    // -----------------------------------------------------------------

    private static async Task ApplyLedgerMigrationsAsync(string conn)
    {
        Console.WriteLine("[1/15] Applying Ledger migrations (finance schema)...");
        var opts = new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(conn).Options;
        await using var db = new LedgerDbContext(opts);
        await db.Database.MigrateAsync();
        Console.WriteLine("       Ledger: up to date.");
    }

    private static async Task ApplyPettyCashMigrationsAsync(string conn)
    {
        Console.WriteLine("[2/15] Applying Petty Cash migrations (petty_cash schema)...");
        var opts = new DbContextOptionsBuilder<PettyCashDbContext>().UseNpgsql(conn).Options;
        await using var db = new PettyCashDbContext(opts);
        await db.Database.MigrateAsync();
        Console.WriteLine("       Petty Cash: up to date.");
    }

    private static async Task ApplyCoaMigrationsAsync(string conn)
    {
        Console.WriteLine("[3/15] Applying CoA migrations (coa schema)...");
        var opts = new DbContextOptionsBuilder<CoaDbContext>().UseNpgsql(conn).Options;
        await using var db = new CoaDbContext(opts);
        await db.Database.MigrateAsync();
        Console.WriteLine("       CoA: up to date.");
    }

    private static async Task ApplyArMigrationsAsync(string conn)
    {
        Console.WriteLine("[4/15] Applying AR migrations (ar schema)...");
        var opts = new DbContextOptionsBuilder<ArDbContext>().UseNpgsql(conn).Options;
        await using var db = new ArDbContext(opts);
        await db.Database.MigrateAsync();
        Console.WriteLine("       AR: up to date.");
    }

    private static async Task ApplyApMigrationsAsync(string conn)
    {
        Console.WriteLine("[5/15] Applying AP migrations (ap schema)...");
        var opts = new DbContextOptionsBuilder<ApDbContext>().UseNpgsql(conn).Options;
        await using var db = new ApDbContext(opts);
        await db.Database.MigrateAsync();
        Console.WriteLine("       AP: up to date.");
    }

    private static async Task ApplyBankingMigrationsAsync(string conn)
    {
        Console.WriteLine("[6/15] Applying Banking migrations (banking schema)...");
        var opts = new DbContextOptionsBuilder<BankingDbContext>().UseNpgsql(conn).Options;
        await using var db = new BankingDbContext(opts);
        await db.Database.MigrateAsync();
        Console.WriteLine("       Banking: up to date.");
    }

    private static async Task ApplyFixedAssetsMigrationsAsync(string conn)
    {
        Console.WriteLine("[7/15] Applying Fixed Assets migrations (fixed_assets schema)...");
        var opts = new DbContextOptionsBuilder<FixedAssetsDbContext>().UseNpgsql(conn).Options;
        await using var db = new FixedAssetsDbContext(opts);
        await db.Database.MigrateAsync();
        Console.WriteLine("       Fixed Assets: up to date.");
    }

    private static async Task ApplyBudgetingMigrationsAsync(string conn)
    {
        Console.WriteLine("[8/15] Applying Budgeting migrations (budgeting schema)...");
        var opts = new DbContextOptionsBuilder<BudgetingDbContext>().UseNpgsql(conn).Options;
        await using var db = new BudgetingDbContext(opts);
        await db.Database.MigrateAsync();
        Console.WriteLine("       Budgeting: up to date.");
    }

    private static async Task ApplyIdentityMigrationsAsync(string conn)
    {
        // The Identity context needs an ITenantAccessor only for the
        // runtime query filters. Migrations operate at the model level
        // (no rows queried), so passing null here is safe — the bootstrap
        // CLI sees every row anyway.
        Console.WriteLine("[9/15] Applying Identity migrations (identity schema)...");
        var opts = new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(conn).Options;
        await using var db = new IdentityDbContext(opts);
        await db.Database.MigrateAsync();
        Console.WriteLine("       Identity: up to date (legacy 6 roles seeded by Initial migration).");
    }

    /// <summary>
    /// Phase 2 of the role-overhaul wave (2026-04-30) — idempotently
    /// inserts the 10 grade rows from the new concentric catalogue
    /// (8 ops grades + 2 audit grades) into <c>identity.roles</c>.
    /// The legacy 6 + interim 14 names from previous phases stay in
    /// place but are NOT seeded into <c>identity.role_permissions</c>,
    /// so an accidentally-still-granted legacy role resolves to ZERO
    /// permissions — the right fail-closed default during the migration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Grade ids 21..30 are deterministically assigned here so a re-run
    /// against a partially-migrated DB always lands on the same id. We
    /// pick ids strictly above the legacy 1..20 range so the FK on
    /// <c>user_roles.role_id</c> never collides.
    /// </para>
    /// <para>
    /// The names are inlined here to keep the bootstrap CLI free of any
    /// reference to the WebApp project (which would be a circular dep
    /// once the WebApp consumes any DbContext lib). Keep this list in
    /// sync with <c>NickFinance.WebApp.Identity.RoleNames.All</c>.
    /// </para>
    /// </remarks>
    private static async Task SeedGradesFromCatalogAsync(string conn)
    {
        Console.WriteLine("[10/15] Seeding grade rows from RoleNames (10-grade catalogue)...");

        // (role_id, name, description). Mirror of
        // NickFinance.WebApp.Identity.RoleNames.Descriptions.
        var grades = new (short id, string name, string description)[]
        {
            // ---- Ops ladder (8) ----
            (21, "Viewer",              "Read-only access to the home page."),
            (22, "SiteCashier",         "Site-scoped: submit petty-cash vouchers and run cash counts."),
            (23, "SiteSupervisor",      "Site-scoped: SiteCashier + approve site vouchers and view site reports."),
            (24, "Bookkeeper",          "HQ-side bookkeeper: SiteSupervisor + capture bills, draft invoices, master-data read."),
            (25, "Accountant",          "Bookkeeper + record receipts, run depreciation, full reports, petty-cash disburse."),
            (26, "SeniorAccountant",    "Accountant + issue/void invoices, payment runs, FX rates, bank reconciliation, WHT certificates, dunning, master-data write."),
            (27, "FinancialController", "SeniorAccountant + manual journals, period close, FX revaluation, budget lock, iTaPS export, recurring vouchers."),
            (28, "SuperAdmin",          "FinancialController + manage NickFinance access, view security audit log. Break-glass; two named individuals max."),
            // ---- Audit ring (2) ----
            (29, "InternalAuditor",     "Read-only across journals, audit log, and every operational page. No write verbs."),
            (30, "ExternalAuditor",     "Time-boxed read-only access for external audit firms (ExpiresAt + audit_firm required)."),
        };

        var opts = new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(conn).Options;
        await using var db = new IdentityDbContext(opts);
        var existingNames = await db.Roles.Select(r => r.Name).ToListAsync();
        var existingSet = new HashSet<string>(existingNames, StringComparer.Ordinal);
        var inserted = 0;
        foreach (var (id, name, description) in grades)
        {
            if (existingSet.Contains(name)) continue;
            db.Roles.Add(new Role { RoleId = id, Name = name, Description = description });
            inserted++;
        }
        if (inserted > 0)
        {
            await db.SaveChangesAsync();
        }
        Console.WriteLine($"       Grades: inserted {inserted} (legacy + any pre-existing rows untouched).");
    }

    /// <summary>
    /// Phase 2 of the role-overhaul wave (2026-04-30) — idempotently
    /// inserts the 52-permission catalogue into <c>identity.permissions</c>.
    /// Permission ids 1..52 are deterministically assigned, matching the
    /// order in <c>NickFinance.WebApp.Identity.Permissions.All</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each row carries <c>name</c> (the canonical
    /// <c>{module}.{noun}.{verb}</c> string), <c>description</c> (free-text
    /// for the HR grant UI's permission preview), and <c>category</c> (the
    /// first segment of the name — <c>"petty"</c>, <c>"ar"</c>, etc.).
    /// </para>
    /// <para>
    /// Inlined to keep the CLI WebApp-free. Keep this list in sync with
    /// <c>NickFinance.WebApp.Identity.Permissions.All</c> +
    /// <c>Permissions.Descriptions</c>.
    /// </para>
    /// </remarks>
    private static async Task SeedPermissionCatalogAsync(string conn)
    {
        Console.WriteLine("[11/15] Seeding permission catalogue (52 rows)...");

        // (permission_id, name, description). Order matches
        // NickFinance.WebApp.Identity.Permissions.All.
        var permissions = new (short id, string name, string description)[]
        {
            // Home / dashboard
            (1,  "home.view",                 "Read-only access to the home page."),
            // Petty cash
            (2,  "petty.voucher.view",        "View petty-cash vouchers."),
            (3,  "petty.voucher.submit",      "Submit a draft petty-cash voucher."),
            (4,  "petty.voucher.approve",     "Approve a submitted voucher."),
            (5,  "petty.voucher.reject",      "Reject a submitted voucher."),
            (6,  "petty.voucher.disburse",    "Disburse cash for an approved voucher."),
            (7,  "petty.float.view",          "View petty-cash floats."),
            (8,  "petty.float.create",        "Provision a new petty-cash float."),
            (9,  "petty.float.close",         "Close an existing float."),
            (10, "petty.cashcount.view",      "View daily cash-count reconciliations."),
            (11, "petty.cashcount.run",       "Record a physical cash count against a float."),
            (12, "petty.delegation.view",     "View approval delegations."),
            (13, "petty.delegation.manage",   "Create / revoke an approval delegation."),
            (14, "petty.recurring.view",      "View recurring voucher templates."),
            (15, "petty.recurring.manage",    "Manage recurring voucher templates."),
            // AR
            (16, "ar.customer.view",          "View customer records."),
            (17, "ar.customer.manage",        "Create / edit / disable a customer."),
            (18, "ar.invoice.view",           "View AR invoices."),
            (19, "ar.invoice.draft",          "Draft an AR invoice (does NOT issue or mint a GRA IRN)."),
            (20, "ar.invoice.issue",          "Issue a real GRA-IRN-bearing AR invoice — irreversible."),
            (21, "ar.invoice.void",           "Void an issued invoice."),
            (22, "ar.receipt.view",           "View customer receipts."),
            (23, "ar.receipt.record",         "Record a customer receipt against an invoice."),
            (24, "ar.dunning.run",            "Run a dunning cycle against overdue customer balances."),
            (25, "ar.statement.view",         "View customer statements."),
            (26, "ar.statement.email",        "Email a customer statement to the customer's contact address."),
            // AP
            (27, "ap.vendor.view",            "View vendor records."),
            (28, "ap.vendor.manage",          "Create / edit / disable a vendor."),
            (29, "ap.bill.view",              "View vendor bills."),
            (30, "ap.bill.enter",             "Capture a bill against a vendor."),
            (31, "ap.bill.void",              "Void a captured bill."),
            (32, "ap.payment.run",            "Authorise a payment run (cheque / transfer / momo)."),
            (33, "ap.wht.view",               "View WHT certificates."),
            (34, "ap.wht.issue",              "Issue a WHT certificate to a supplier (GRA filing artefact)."),
            // Banking
            (35, "banking.statement.import",  "Import a bank statement file (CSV / OFX / MT940)."),
            (36, "banking.recon.run",         "Reconcile bank statement lines against ledger postings."),
            (37, "banking.fxrate.view",       "View FX rate history."),
            (38, "banking.fxrate.manage",     "Maintain FX rates (manual entry or BoG sync)."),
            (39, "banking.fxreval.run",       "Run period-end FX revaluation."),
            // Fixed assets
            (40, "assets.view",               "View the fixed-asset register."),
            (41, "assets.register",           "Register or retire a fixed asset."),
            (42, "assets.depreciate",         "Run period depreciation against the asset register."),
            // Budgeting
            (43, "budget.view",               "View budgets."),
            (44, "budget.manage",             "Create / edit budget headers and lines."),
            (45, "budget.lock",               "Lock a budget — no further edits allowed."),
            // Ledger / period
            (46, "journal.view",              "Read-only view of the manual journal list and details."),
            (47, "journal.post",              "Post a manual journal directly to the ledger."),
            (48, "period.view",               "View accounting period status."),
            (49, "period.close",              "Soft- and hard-close a finance period."),
            // Reports
            (50, "reports.view",              "Read-only access to financial reports (TB, BS, P&L, GL detail, etc.)."),
            (51, "reports.export",            "Export reports to CSV / Excel."),
            // iTaPS
            (52, "itaps.export",              "Export an iTaPS file to GRA."),
            // Identity / admin (these get ids 53..55 — outside the 1..52
            // contiguous block but the schema doesn't care; smallint goes
            // up to 32767. Numbering is purely a cosmetic invariant and
            // we keep it deterministic so re-runs are stable.)
            (53, "users.view",                "View NickFinance role assignments."),
            (54, "users.manage",              "Grant / revoke NickFinance roles via the HR admin panel."),
            (55, "audit.view",                "Read-only access to the security audit log."),
        };

        var opts = new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(conn).Options;
        await using var db = new IdentityDbContext(opts);
        var existingNames = await db.Permissions.Select(p => p.Name).ToListAsync();
        var have = new HashSet<string>(existingNames, StringComparer.Ordinal);
        var inserted = 0;
        foreach (var (id, name, description) in permissions)
        {
            if (have.Contains(name)) continue;
            var category = name.Split('.', 2) is { Length: 2 } parts ? parts[0] : null;
            db.Permissions.Add(new Permission
            {
                PermissionId = id,
                Name = name,
                Description = description,
                Category = category,
            });
            inserted++;
        }
        if (inserted > 0) await db.SaveChangesAsync();
        Console.WriteLine($"       Permissions: inserted {inserted} (existing rows left untouched).");
    }

    /// <summary>
    /// Phase 2 of the role-overhaul wave (2026-04-30) — idempotently
    /// joins each grade's bundle into <c>identity.role_permissions</c>.
    /// The bundle definitions mirror
    /// <c>NickFinance.WebApp.Identity.GradePermissions.ForGrade(roleName)</c>;
    /// each grade strictly contains its parent's bundle (NSCIM
    /// <c>PermissionSeeder</c> pattern).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Total rows after a clean seed: 1 (Viewer) + 6 (SiteCashier) + 10
    /// (SiteSupervisor) + 18 (Bookkeeper) + 26 (Accountant) + 38
    /// (SeniorAccountant) + 49 (FinancialController) + 52 (SuperAdmin)
    /// + 22 (InternalAuditor) + 22 (ExternalAuditor) = 244 rows.
    /// </para>
    /// </remarks>
    private static async Task SeedRolePermissionGrantsAsync(string conn)
    {
        Console.WriteLine("[12/15] Seeding role-permission grants from GradePermissions...");

        // Bundle definitions — order matches GradePermissions.GetXPermissions()
        // in NickFinance.WebApp.Identity. Each tuple = (gradeName, permissionNames).
        var viewer = new[] { "home.view" };
        var siteCashier = viewer.Concat(new[]
        {
            "petty.voucher.view", "petty.voucher.submit",
            "petty.float.view",
            "petty.cashcount.view", "petty.cashcount.run",
        }).ToArray();
        var siteSupervisor = siteCashier.Concat(new[]
        {
            "petty.voucher.approve", "petty.voucher.reject",
            "ar.statement.view",
            "reports.view",
        }).ToArray();
        var bookkeeper = siteSupervisor.Concat(new[]
        {
            "ap.bill.view", "ap.bill.enter",
            "ar.invoice.view", "ar.invoice.draft",
            "ap.vendor.view", "ar.customer.view",
            "ar.receipt.view", "ap.wht.view",
        }).ToArray();
        var accountant = bookkeeper.Concat(new[]
        {
            "ar.receipt.record",
            "petty.voucher.disburse",
            "petty.delegation.view", "petty.delegation.manage",
            "assets.view", "assets.depreciate",
            "banking.fxrate.view", "reports.export",
        }).ToArray();
        var seniorAccountant = accountant.Concat(new[]
        {
            "ar.invoice.issue", "ar.invoice.void",
            "ap.bill.void", "ap.payment.run",
            "ap.vendor.manage", "ar.customer.manage",
            "ap.wht.issue", "ar.dunning.run", "ar.statement.email",
            "banking.statement.import", "banking.recon.run",
            "banking.fxrate.manage",
        }).ToArray();
        var financialController = seniorAccountant.Concat(new[]
        {
            "journal.view", "journal.post",
            "period.view", "period.close",
            "banking.fxreval.run",
            "budget.view", "budget.manage", "budget.lock",
            "assets.register", "itaps.export",
            "petty.float.create", "petty.float.close",
            "petty.recurring.view", "petty.recurring.manage",
        }).ToArray();
        var superAdmin = financialController.Concat(new[]
        {
            "users.view", "users.manage", "audit.view",
        }).ToArray();
        var auditor = new[]
        {
            "home.view",
            "petty.voucher.view", "petty.float.view",
            "petty.cashcount.view", "petty.delegation.view",
            "petty.recurring.view",
            "ar.customer.view", "ar.invoice.view", "ar.receipt.view",
            "ar.statement.view",
            "ap.vendor.view", "ap.bill.view", "ap.wht.view",
            "banking.fxrate.view",
            "assets.view", "budget.view",
            "journal.view", "period.view",
            "reports.view", "reports.export", "audit.view",
            "users.view",
        };
        var bundles = new (string grade, string[] permissions)[]
        {
            ("Viewer",              viewer),
            ("SiteCashier",         siteCashier),
            ("SiteSupervisor",      siteSupervisor),
            ("Bookkeeper",          bookkeeper),
            ("Accountant",          accountant),
            ("SeniorAccountant",    seniorAccountant),
            ("FinancialController", financialController),
            ("SuperAdmin",          superAdmin),
            ("InternalAuditor",     auditor),
            ("ExternalAuditor",     auditor),
        };

        var opts = new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(conn).Options;
        await using var db = new IdentityDbContext(opts);

        // Resolve names → ids once.
        var rolesByName = await db.Roles.ToDictionaryAsync(r => r.Name, r => r.RoleId);
        var permissionsByName = await db.Permissions.ToDictionaryAsync(p => p.Name, p => p.PermissionId);
        var existing = new HashSet<(short, short)>(
            (await db.RolePermissions.Select(rp => new { rp.RoleId, rp.PermissionId }).ToListAsync())
                .Select(x => (x.RoleId, x.PermissionId)));

        var now = DateTimeOffset.UtcNow;
        var inserted = 0;
        foreach (var (gradeName, perms) in bundles)
        {
            if (!rolesByName.TryGetValue(gradeName, out var roleId))
            {
                Console.WriteLine($"       WARNING: grade '{gradeName}' not in identity.roles; skipping its bundle.");
                continue;
            }
            foreach (var permName in perms.Distinct(StringComparer.Ordinal))
            {
                if (!permissionsByName.TryGetValue(permName, out var permId))
                {
                    Console.WriteLine($"       WARNING: permission '{permName}' not in identity.permissions; skipping.");
                    continue;
                }
                if (existing.Contains((roleId, permId))) continue;
                db.RolePermissions.Add(new RolePermission
                {
                    RoleId = roleId,
                    PermissionId = permId,
                    GrantedAt = now,
                });
                existing.Add((roleId, permId));
                inserted++;
            }
        }
        if (inserted > 0) await db.SaveChangesAsync();
        Console.WriteLine($"       Role-permission grants: inserted {inserted} new rows.");
    }

    private static async Task ApplySchemaTriggersAsync(string conn)
    {
        Console.WriteLine("[13/15] Applying Postgres triggers (balance invariant + append-only)...");
        var opts = new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(conn).Options;
        await using var db = new LedgerDbContext(opts);
        await SchemaBootstrap.ApplyConstraintsAsync(db);
        Console.WriteLine("       Triggers: applied.");
    }

    private static async Task ApplyRlsPoliciesAsync(string conn)
    {
        Console.WriteLine("[14/15] Applying Row-Level Security policies (tenant isolation, defence-in-depth)...");
        var sqlPath = ResolveRlsScriptPath();
        if (sqlPath is null)
        {
            Console.WriteLine("       RLS: scripts/apply-rls-policies.sql not found; skipping. "
                + "Set NICKERP_RLS_SCRIPT_PATH or run from a checkout that includes the scripts dir.");
            return;
        }
        var sql = await File.ReadAllTextAsync(sqlPath);
        var opts = new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(conn).Options;
        await using var db = new LedgerDbContext(opts);
        await db.Database.ExecuteSqlRawAsync(sql);
        Console.WriteLine($"       RLS: applied from {sqlPath}.");
    }

    private static async Task SeedGhanaChartAsync(string conn)
    {
        Console.WriteLine("[15/15] Seeding Ghana standard chart of accounts...");
        var opts = new DbContextOptionsBuilder<CoaDbContext>().UseNpgsql(conn).Options;
        await using var db = new CoaDbContext(opts);
        var svc = new CoaService(db);
        var inserted = await svc.SeedGhanaStandardChartAsync(tenantId: 1);
        Console.WriteLine($"       CoA seed: inserted {inserted} new accounts (existing rows left untouched).");
    }

    /// <summary>
    /// One-time seed of placeholder April-2026 mid-rates so a fresh
    /// install can demo the read-side FX path before BoG is wired up
    /// or an operator enters real numbers. Idempotent — only inserts
    /// if the table is empty for tenant 1.
    /// </summary>
    private static async Task SeedInitialFxRatesAsync(string conn)
    {
        Console.WriteLine("[+] Seeding initial FX rates (April-2026 placeholders, manual source)...");
        var opts = new DbContextOptionsBuilder<BankingDbContext>().UseNpgsql(conn).Options;
        await using var db = new BankingDbContext(opts);
        var anyForTenant = await db.FxRates.AnyAsync(r => r.TenantId == 1);
        if (anyForTenant)
        {
            Console.WriteLine("       FX rates: tenant 1 already has rows; skipping seed.");
            return;
        }
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var now = DateTimeOffset.UtcNow;
        // Placeholders only — operators should replace with real BoG rates ASAP.
        var seed = new[]
        {
            new FxRate { FromCurrency = "USD", ToCurrency = "GHS", Rate = 16.20m,    AsOfDate = today, Source = "seed-2026-04", RecordedAt = now, TenantId = 1 },
            new FxRate { FromCurrency = "EUR", ToCurrency = "GHS", Rate = 17.40m,    AsOfDate = today, Source = "seed-2026-04", RecordedAt = now, TenantId = 1 },
            new FxRate { FromCurrency = "GBP", ToCurrency = "GHS", Rate = 20.10m,    AsOfDate = today, Source = "seed-2026-04", RecordedAt = now, TenantId = 1 },
            new FxRate { FromCurrency = "NGN", ToCurrency = "GHS", Rate = 0.011m,    AsOfDate = today, Source = "seed-2026-04", RecordedAt = now, TenantId = 1 },
        };
        db.FxRates.AddRange(seed);
        await db.SaveChangesAsync();
        Console.WriteLine($"       FX rates: seeded {seed.Length} pair(s) at {today:yyyy-MM-dd} (replace with real rates via /banking/fx-rates).");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Find <c>scripts/apply-rls-policies.sql</c>. Walk up from the
    /// current working dir and the binary dir looking for a sibling
    /// `scripts` folder so the CLI works whether invoked from the repo
    /// root, the publish dir, or a deployed Windows host.
    /// </summary>
    private static string? ResolveRlsScriptPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("NICKERP_RLS_SCRIPT_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;

        const string Rel = "scripts/apply-rls-policies.sql";
        const string RelWin = @"scripts\apply-rls-policies.sql";

        foreach (var startDir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var d = new DirectoryInfo(startDir);
            while (d is not null)
            {
                var unix = Path.Combine(d.FullName, Rel);
                if (File.Exists(unix)) return unix;
                var win = Path.Combine(d.FullName, RelWin);
                if (File.Exists(win)) return win;
                d = d.Parent;
            }
        }
        return null;
    }

    private static string? ResolveConnectionString(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--conn", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }
        return Environment.GetEnvironmentVariable("NICKERP_FINANCE_DB_CONNECTION");
    }

    private static string RedactPassword(string conn)
    {
        var parts = conn.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(';', parts.Select(p =>
        {
            if (p.StartsWith("Password=", StringComparison.OrdinalIgnoreCase)) return "Password=*****";
            return p;
        }));
    }
}
