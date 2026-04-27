using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Phase F1 — defense-in-depth tenancy at the DB layer.
    /// Enables + FORCE-enables Row Level Security on every tenant-owned table
    /// in the <c>inspection</c> schema and installs a
    /// <c>tenant_isolation_&lt;table&gt;</c> policy that filters rows by the
    /// session-local <c>app.tenant_id</c> setting (pushed by
    /// <see cref="NickERP.Platform.Tenancy.TenantConnectionInterceptor"/>).
    ///
    /// Fail-closed semantics: when <c>app.tenant_id</c> is unset (anonymous
    /// connection, missing middleware), <c>current_setting('app.tenant_id', true)</c>
    /// returns NULL → COALESCE pins it to <c>'0'</c> → the policy excludes
    /// every row, since application code never writes <c>TenantId = 0</c>.
    /// </summary>
    public partial class Add_RLS_Policies : Migration
    {
        /// <summary>
        /// Tenant-owned tables in the <c>inspection</c> schema. Matches the
        /// <see cref="InspectionDbContextModelSnapshot"/>; every entry has a
        /// <c>"TenantId"</c> column. Update if a new tenant-owned table lands.
        /// </summary>
        private static readonly string[] TenantedTables = new[]
        {
            "locations",
            "stations",
            "scanner_device_instances",
            "external_system_instances",
            "external_system_bindings",
            "location_assignments",
            "cases",
            "scans",
            "scan_artifacts",
            "scan_render_artifacts",
            "authority_documents",
            "review_sessions",
            "analyst_reviews",
            "findings",
            "verdicts",
            "outbound_submissions"
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantedTables)
            {
                migrationBuilder.Sql(
                    $"ALTER TABLE inspection.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"ALTER TABLE inspection.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"CREATE POLICY tenant_isolation_{table} ON inspection.{table} "
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
                    $"DROP POLICY IF EXISTS tenant_isolation_{table} ON inspection.{table};");
                migrationBuilder.Sql(
                    $"ALTER TABLE inspection.{table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"ALTER TABLE inspection.{table} DISABLE ROW LEVEL SECURITY;");
            }
        }
    }
}
