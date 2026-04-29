using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.NickFinance.Database.Migrations
{
    /// <summary>
    /// G2 — apply Row-Level Security policies and the <c>nscim_app</c>
    /// grants for the <c>nickfinance</c> schema. Mirrors the inspection
    /// module's <c>Add_RLS_Policies</c> + <c>Add_NscimAppRole_Grants</c>
    /// pattern, but bundled into one migration since this is a
    /// greenfield module.
    ///
    /// <para>
    /// Tenant-isolation policies (one per tenant-owned table) match the
    /// platform pattern: USING + WITH CHECK on
    /// <c>"TenantId" = current_setting('app.tenant_id'...)::bigint</c>,
    /// fail-closed default <c>'0'</c>. <see cref="Init_NickFinance"/>
    /// already created the tables; this migration ENABLE + FORCE-enables
    /// RLS and installs the policy.
    /// </para>
    ///
    /// <para>
    /// <c>fx_rate</c> opts in to the system-context sentinel
    /// (<c>app.tenant_id = '-1'</c>) — the v0 publish path runs under
    /// <see cref="NickERP.Platform.Tenancy.ITenantContext.SetSystemContext"/>
    /// for suite-wide rates (NULL <c>TenantId</c>). The OR clause is
    /// permissive on read too so any session can SELECT current rates;
    /// the WITH CHECK side restricts cross-tenant inserts to the
    /// system-context path. Registered in
    /// <c>docs/system-context-audit-register.md</c>.
    /// </para>
    ///
    /// <para>
    /// <strong>nscim_app grants:</strong> SELECT/INSERT/UPDATE on every
    /// table in the schema. <strong>NOT DELETE</strong> — the ledger is
    /// append-only (G2 §1.6), and vouchers / boxes mutate via state but
    /// never disappear. Default privileges so future tables inherit
    /// the same grants.
    /// </para>
    /// </summary>
    public partial class Add_RLS_And_Grants : Migration
    {
        /// <summary>
        /// Tenant-owned tables under standard (per-tenant only) RLS.
        /// <c>fx_rate</c> is excluded — handled separately with the
        /// system-context opt-in.
        /// </summary>
        private static readonly string[] TenantedTables = new[]
        {
            "petty_cash_boxes",
            "petty_cash_vouchers",
            "petty_cash_ledger_events",
            "petty_cash_periods"
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ----- Standard tenant-isolation RLS on the four core tables ------
            foreach (var table in TenantedTables)
            {
                migrationBuilder.Sql(
                    $"ALTER TABLE nickfinance.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"ALTER TABLE nickfinance.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"CREATE POLICY tenant_isolation_{table} ON nickfinance.{table} "
                    + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                    + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");
            }

            // ----- fx_rate: system-context opt-in (G2 §1.10) -------------------
            // The TenantId column is NULL for suite-wide rates; the v0 publish
            // path runs under SetSystemContext() so the OR clause admits the
            // NULL-tenant write. Reads are permissive (any tenant can SELECT
            // current rates) since the OR clause is on USING too — that is
            // intentional: every per-tenant ledger write needs to look up the
            // suite-wide rate, and we don't want to require system context
            // for every read.
            migrationBuilder.Sql(
                "ALTER TABLE nickfinance.fx_rate ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE nickfinance.fx_rate FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_fx_rate ON nickfinance.fx_rate "
                + "USING ("
                + "(\"TenantId\" IS NULL) "
                + "OR (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "OR (current_setting('app.tenant_id', true) = '-1')"
                + ") "
                + "WITH CHECK ("
                + "(current_setting('app.tenant_id', true) = '-1' AND \"TenantId\" IS NULL) "
                + "OR (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint)"
                + ");");

            // ----- nscim_app grants -------------------------------------------
            // Cluster-wide role; idempotent CREATE in case another DB's
            // migration didn't win the race yet (mirrors inspection's
            // Add_NscimAppRole_Grants stanza).
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nscim_app') THEN
        CREATE ROLE nscim_app WITH LOGIN NOSUPERUSER NOBYPASSRLS;
    END IF;
END $$;");

            migrationBuilder.Sql("GRANT USAGE ON SCHEMA nickfinance TO nscim_app;");

            // SELECT/INSERT/UPDATE — NOT DELETE. The ledger is append-only;
            // vouchers and boxes mutate via state. If you ever need to remove
            // a row (e.g. tenant offboarding), do it as postgres + a one-off
            // migration, not via the app role.
            migrationBuilder.Sql(
                "GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA nickfinance TO nscim_app;");
            migrationBuilder.Sql(
                "GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA nickfinance TO nscim_app;");

            // Default privileges so future tables inherit grants.
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA nickfinance "
                + "GRANT SELECT, INSERT, UPDATE ON TABLES TO nscim_app;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA nickfinance "
                + "GRANT USAGE, SELECT ON SEQUENCES TO nscim_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revoke default privileges first to prevent future tables from
            // re-granting back to nscim_app.
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA nickfinance "
                + "REVOKE USAGE, SELECT ON SEQUENCES FROM nscim_app;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA nickfinance "
                + "REVOKE SELECT, INSERT, UPDATE ON TABLES FROM nscim_app;");

            migrationBuilder.Sql(
                "REVOKE USAGE, SELECT ON ALL SEQUENCES IN SCHEMA nickfinance FROM nscim_app;");
            migrationBuilder.Sql(
                "REVOKE SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA nickfinance FROM nscim_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA nickfinance FROM nscim_app;");

            // RLS off in reverse order.
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_fx_rate ON nickfinance.fx_rate;");
            migrationBuilder.Sql("ALTER TABLE nickfinance.fx_rate NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE nickfinance.fx_rate DISABLE ROW LEVEL SECURITY;");

            foreach (var table in TenantedTables)
            {
                migrationBuilder.Sql(
                    $"DROP POLICY IF EXISTS tenant_isolation_{table} ON nickfinance.{table};");
                migrationBuilder.Sql(
                    $"ALTER TABLE nickfinance.{table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"ALTER TABLE nickfinance.{table} DISABLE ROW LEVEL SECURITY;");
            }
        }
    }
}
