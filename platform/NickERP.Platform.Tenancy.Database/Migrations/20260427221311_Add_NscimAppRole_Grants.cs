using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Tenancy.Database.Migrations
{
    /// <summary>
    /// Phase F5 — non-superuser app role + grants for the
    /// <c>tenancy</c> schema.
    ///
    /// <para>
    /// The <c>tenants</c> table is the root of the tenancy graph and is
    /// intentionally NOT under RLS (per F1's <c>Add_RLS_Policies</c>).
    /// The <c>nscim_app</c> role still gets full CRUD on tenancy so the
    /// admin Portal can list / create tenants while running as the
    /// app role.
    /// </para>
    /// </summary>
    public partial class Add_NscimAppRole_Grants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Cluster-wide role; idempotent so any DB's migration may win.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nscim_app') THEN
        CREATE ROLE nscim_app WITH LOGIN NOSUPERUSER NOBYPASSRLS;
    END IF;
END $$;");

            migrationBuilder.Sql("GRANT USAGE ON SCHEMA tenancy TO nscim_app;");
            migrationBuilder.Sql(
                "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA tenancy TO nscim_app;");
            migrationBuilder.Sql(
                "GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA tenancy TO nscim_app;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA tenancy "
                + "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO nscim_app;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA tenancy "
                + "GRANT USAGE, SELECT ON SEQUENCES TO nscim_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA tenancy "
                + "REVOKE USAGE, SELECT ON SEQUENCES FROM nscim_app;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA tenancy "
                + "REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM nscim_app;");
            migrationBuilder.Sql(
                "REVOKE USAGE, SELECT ON ALL SEQUENCES IN SCHEMA tenancy FROM nscim_app;");
            migrationBuilder.Sql(
                "REVOKE SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA tenancy FROM nscim_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA tenancy FROM nscim_app;");
        }
    }
}
