using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Phase F5 — non-superuser app role + grants for the
    /// <c>inspection</c> schema.
    ///
    /// <para>
    /// The host has been connecting as <c>postgres</c>, which has
    /// <c>BYPASSRLS</c> and silently nullifies the F1 RLS policies. This
    /// migration installs the <c>nscim_app</c> role
    /// (<c>LOGIN NOSUPERUSER NOBYPASSRLS</c>) and grants it the minimum
    /// privileges needed to serve traffic: <c>USAGE</c> on the schema,
    /// CRUD on every table, <c>USAGE+SELECT</c> on every sequence, plus
    /// matching default privileges so future tables / sequences inherit
    /// the same grants without another migration.
    /// </para>
    ///
    /// <para>
    /// Roles are cluster-wide in Postgres, so the <c>CREATE ROLE</c>
    /// step is wrapped in a <c>DO ... IF NOT EXISTS</c> block — the
    /// other three platform DBs (<c>identity</c>, <c>audit</c>,
    /// <c>tenancy</c>) carry the same idempotent stanza so any of them
    /// can win the race.
    /// </para>
    ///
    /// <para>
    /// The password is NOT set here. EF migrations are deterministic
    /// SQL; baking a secret in git is unacceptable. Ops sets the
    /// password out of band (see <c>tools/migrations/phase-f5/</c>),
    /// reading from <c>$NICKERP_APP_DB_PASSWORD</c>; in dev, set it to
    /// the same value as <c>$NICKSCAN_DB_PASSWORD</c>.
    /// </para>
    /// </summary>
    public partial class Add_NscimAppRole_Grants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Cluster-wide role. Idempotent — any DB's migration may run first.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nscim_app') THEN
        CREATE ROLE nscim_app WITH LOGIN NOSUPERUSER NOBYPASSRLS;
    END IF;
END $$;");

            // 2) Schema usage.
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA inspection TO nscim_app;");

            // 3) CRUD on every existing table in the schema.
            migrationBuilder.Sql(
                "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA inspection TO nscim_app;");

            // 4) USAGE+SELECT on every existing sequence (gen_random_uuid
            // doesn't need this, but identity-column sequences do, and
            // future numeric ids will too).
            migrationBuilder.Sql(
                "GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA inspection TO nscim_app;");

            // 5) Default privileges for tables + sequences created
            // *later* by future migrations applied as the postgres
            // owner. Without this every new table needs a fresh GRANT.
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA inspection "
                + "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO nscim_app;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA inspection "
                + "GRANT USAGE, SELECT ON SEQUENCES TO nscim_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse default privileges first so the REVOKE on TABLES
            // sweep below isn't undone by future inserts auto-granting
            // back to nscim_app.
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA inspection "
                + "REVOKE USAGE, SELECT ON SEQUENCES FROM nscim_app;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA inspection "
                + "REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM nscim_app;");

            migrationBuilder.Sql(
                "REVOKE USAGE, SELECT ON ALL SEQUENCES IN SCHEMA inspection FROM nscim_app;");
            migrationBuilder.Sql(
                "REVOKE SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA inspection FROM nscim_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA inspection FROM nscim_app;");

            // Don't drop the role here — other DBs still depend on it.
            // A separate cluster-level teardown handles role removal.
        }
    }
}
