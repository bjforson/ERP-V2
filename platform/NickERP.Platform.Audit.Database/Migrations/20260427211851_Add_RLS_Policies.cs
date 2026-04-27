using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Audit.Database.Migrations
{
    /// <summary>
    /// Phase F1 — defense-in-depth tenancy at the DB layer for the
    /// <c>audit</c> schema. Enables + FORCE-enables Row Level Security on
    /// <c>audit.events</c> and installs a <c>tenant_isolation_events</c>
    /// policy that filters by the session-local <c>app.tenant_id</c>
    /// setting pushed by
    /// <see cref="NickERP.Platform.Tenancy.TenantConnectionInterceptor"/>.
    ///
    /// Note: the audit log is append-only at the role level (REVOKE
    /// UPDATE/DELETE — see <c>AUDIT.md</c>). RLS layers cleanly on top:
    /// the policy applies to SELECT/INSERT, the role grants block UPDATE/DELETE
    /// regardless. With FORCE RLS, even superuser INSERTs go through the
    /// policy's WITH CHECK clause — preventing accidental cross-tenant
    /// audit emissions.
    /// </summary>
    public partial class Add_RLS_Policies : Migration
    {
        /// <summary>
        /// Tenant-owned tables in the <c>audit</c> schema. The single
        /// <c>events</c> table carries <c>"TenantId"</c> per
        /// <see cref="AuditDbContextModelSnapshot"/>.
        /// </summary>
        private static readonly string[] TenantedTables = new[]
        {
            "events"
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantedTables)
            {
                migrationBuilder.Sql(
                    $"ALTER TABLE audit.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"ALTER TABLE audit.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"CREATE POLICY tenant_isolation_{table} ON audit.{table} "
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
                    $"DROP POLICY IF EXISTS tenant_isolation_{table} ON audit.{table};");
                migrationBuilder.Sql(
                    $"ALTER TABLE audit.{table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"ALTER TABLE audit.{table} DISABLE ROW LEVEL SECURITY;");
            }
        }
    }
}
