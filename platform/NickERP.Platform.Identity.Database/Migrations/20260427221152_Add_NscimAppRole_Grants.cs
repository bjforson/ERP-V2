using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Identity.Database.Migrations
{
    /// <summary>
    /// Phase F5 — non-superuser app role + grants for the
    /// <c>identity</c> schema. See the matching migration in
    /// NickERP.Inspection.Database for full context (the role is
    /// cluster-wide; the schema grants are per-DB).
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

            migrationBuilder.Sql("GRANT USAGE ON SCHEMA identity TO nscim_app;");
            migrationBuilder.Sql(
                "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity TO nscim_app;");
            migrationBuilder.Sql(
                "GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA identity TO nscim_app;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA identity "
                + "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO nscim_app;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA identity "
                + "GRANT USAGE, SELECT ON SEQUENCES TO nscim_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA identity "
                + "REVOKE USAGE, SELECT ON SEQUENCES FROM nscim_app;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA identity "
                + "REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM nscim_app;");
            migrationBuilder.Sql(
                "REVOKE USAGE, SELECT ON ALL SEQUENCES IN SCHEMA identity FROM nscim_app;");
            migrationBuilder.Sql(
                "REVOKE SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity FROM nscim_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA identity FROM nscim_app;");
        }
    }
}
