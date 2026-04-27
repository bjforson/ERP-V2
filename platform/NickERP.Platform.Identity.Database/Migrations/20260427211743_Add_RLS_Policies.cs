using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Identity.Database.Migrations
{
    /// <summary>
    /// Phase F1 — defense-in-depth tenancy at the DB layer for the
    /// <c>identity</c> schema. Enables + FORCE-enables Row Level Security
    /// on every tenant-owned identity table and installs a
    /// <c>tenant_isolation_&lt;table&gt;</c> policy that filters by the
    /// session-local <c>app.tenant_id</c> setting pushed by
    /// <see cref="NickERP.Platform.Tenancy.TenantConnectionInterceptor"/>.
    ///
    /// Fail-closed: an unset <c>app.tenant_id</c> COALESCEs to <c>'0'</c>,
    /// which excludes every row (no application data is ever written with
    /// <c>TenantId = 0</c>).
    /// </summary>
    public partial class Add_RLS_Policies : Migration
    {
        /// <summary>
        /// Tenant-owned tables in the <c>identity</c> schema. All five carry
        /// a <c>"TenantId"</c> column per <see cref="IdentityDbContextModelSnapshot"/>.
        /// </summary>
        private static readonly string[] TenantedTables = new[]
        {
            "identity_users",
            "app_scopes",
            "user_scopes",
            "service_token_identities",
            "service_token_scopes"
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantedTables)
            {
                migrationBuilder.Sql(
                    $"ALTER TABLE identity.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"ALTER TABLE identity.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"CREATE POLICY tenant_isolation_{table} ON identity.{table} "
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
                    $"DROP POLICY IF EXISTS tenant_isolation_{table} ON identity.{table};");
                migrationBuilder.Sql(
                    $"ALTER TABLE identity.{table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"ALTER TABLE identity.{table} DISABLE ROW LEVEL SECURITY;");
            }
        }
    }
}
