using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Audit.Database.Migrations
{
    /// <summary>
    /// Phase F5 — non-superuser app role + grants for the
    /// <c>audit</c> schema.
    ///
    /// <para>
    /// The audit log is append-only at the role level (per AUDIT.md):
    /// <c>nscim_app</c> only gets <c>SELECT, INSERT</c>, never
    /// <c>UPDATE / DELETE / TRUNCATE</c>. RLS on top still scopes reads
    /// + writes by tenant; the role grants ensure that even if a SQL
    /// injection slipped past the ORM layer, the attacker cannot
    /// rewrite history.
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

            migrationBuilder.Sql("GRANT USAGE ON SCHEMA audit TO nscim_app;");

            // SELECT + INSERT only — audit is append-only.
            migrationBuilder.Sql(
                "GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA audit TO nscim_app;");
            migrationBuilder.Sql(
                "GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA audit TO nscim_app;");

            // Default privileges keep the same posture for future audit
            // tables — append-only stays append-only without us having
            // to remember on every schema change.
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA audit "
                + "GRANT SELECT, INSERT ON TABLES TO nscim_app;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA audit "
                + "GRANT USAGE, SELECT ON SEQUENCES TO nscim_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA audit "
                + "REVOKE USAGE, SELECT ON SEQUENCES FROM nscim_app;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA audit "
                + "REVOKE SELECT, INSERT ON TABLES FROM nscim_app;");
            migrationBuilder.Sql(
                "REVOKE USAGE, SELECT ON ALL SEQUENCES IN SCHEMA audit FROM nscim_app;");
            migrationBuilder.Sql(
                "REVOKE SELECT, INSERT ON ALL TABLES IN SCHEMA audit FROM nscim_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA audit FROM nscim_app;");
        }
    }
}
