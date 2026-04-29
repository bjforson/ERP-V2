using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Audit.Database.Migrations
{
    /// <summary>
    /// Sprint 5 / G1-3 — opt <c>audit.events</c> in to the system-context
    /// sentinel pushed by
    /// <see cref="NickERP.Platform.Tenancy.TenantConnectionInterceptor"/>
    /// when <see cref="NickERP.Platform.Tenancy.ITenantContext.IsSystem"/>
    /// is <c>true</c>. Drops the existing <c>tenant_isolation_events</c>
    /// policy and recreates it with an extra disjunct that admits the
    /// session value <c>app.tenant_id = '-1'</c> for rows whose
    /// <c>"TenantId"</c> is <c>NULL</c> (suite-wide events such as FX-rate
    /// publication, GL chart-of-accounts updates).
    ///
    /// <para>
    /// Applies to <c>audit.events</c> ONLY. Adding the same opt-in clause
    /// to other tables is a deliberate per-table decision documented in
    /// <c>docs/system-context-audit-register.md</c>; never blanket-apply.
    /// </para>
    ///
    /// <para>
    /// Down reverts to the F1 plain shape (no <c>OR</c> clause) so a
    /// rollback re-asserts strict per-tenant isolation.
    /// </para>
    /// </summary>
    public partial class AddSystemContextOptInToEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_events ON audit.events;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_events ON audit.events "
                + "USING ("
                + "\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint "
                + "OR (current_setting('app.tenant_id', true) = '-1' AND \"TenantId\" IS NULL)"
                + ") "
                + "WITH CHECK ("
                + "\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint "
                + "OR (current_setting('app.tenant_id', true) = '-1' AND \"TenantId\" IS NULL)"
                + ");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the F1 (20260427211851_Add_RLS_Policies) shape: strict
            // per-tenant filter only, no system-context disjunct.
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_events ON audit.events;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_events ON audit.events "
                + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");
        }
    }
}
