using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Tenancy.Database.Migrations
{
    /// <summary>
    /// Phase F1 — defense-in-depth tenancy at the DB layer for the
    /// <c>tenancy</c> schema.
    ///
    /// <para>
    /// Intentionally a no-op for tables in this schema. The only table here
    /// is <c>tenancy.tenants</c>, which is the ROOT of the tenancy graph and
    /// MUST remain unprotected by RLS — bootstrapping a new tenant requires
    /// reading + inserting rows BEFORE any <c>app.tenant_id</c> can be
    /// resolved. Per <c>docs/sprint/team-tenant-safety.md</c>: "the tenants
    /// table is the root and stays unprotected."
    /// </para>
    ///
    /// <para>
    /// This migration is checked in (rather than skipped) so the chronology
    /// of the F1 rollout is uniform across all four platform schemas; a
    /// future tenant-owned table added to this schema can extend
    /// <c>TenantedTables</c> below without renumbering.
    /// </para>
    /// </summary>
    public partial class Add_RLS_Policies : Migration
    {
        /// <summary>
        /// Empty by design — see class summary. <c>tenancy.tenants</c> stays
        /// unprotected. Add new tenant-owned tenancy tables here (and to the
        /// <c>Down()</c> teardown).
        /// </summary>
        private static readonly string[] TenantedTables = System.Array.Empty<string>();

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantedTables)
            {
                migrationBuilder.Sql(
                    $"ALTER TABLE tenancy.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"ALTER TABLE tenancy.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"CREATE POLICY tenant_isolation_{table} ON tenancy.{table} "
                    + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                    + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantedTables)
            {
                migrationBuilder.Sql(
                    $"DROP POLICY IF EXISTS tenant_isolation_{table} ON tenancy.{table};");
                migrationBuilder.Sql(
                    $"ALTER TABLE tenancy.{table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"ALTER TABLE tenancy.{table} DISABLE ROW LEVEL SECURITY;");
            }
        }
    }
}
