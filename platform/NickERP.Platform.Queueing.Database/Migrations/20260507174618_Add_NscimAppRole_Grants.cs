using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Queueing.Database.Migrations
{
    /// <summary>
    /// Sprint 14 / B-queues — non-superuser app-role grants for the
    /// <c>queueing</c> schema. Mirrors the pattern from
    /// <see cref="Add_NscimAppRole_Grants"/> in
    /// <c>NickERP.Platform.Tenancy.Database</c> (and the equivalent in
    /// <c>NickERP.Platform.Audit.Database</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a follow-up migration.</b> The <c>Initial_AddQueueingSchema</c>
    /// migration creates the schema + tables under the <c>postgres</c>
    /// owner. The runtime app connects as the non-superuser
    /// <c>nscim_app</c> role; without these grants every query against
    /// <c>queueing.outbox</c> / <c>queueing.dead_letter</c> /
    /// <c>queueing.queue_metrics</c> fails with
    /// <c>permission denied for schema queueing</c>. The
    /// <c>Migrations/NOTES.md</c> already flagged this as the
    /// outstanding follow-up; this migration closes it.
    /// </para>
    /// <para>
    /// <b>Role creation is idempotent</b> (CREATE ROLE only if missing).
    /// In practice the role is created by the earlier Tenancy grants
    /// migration; this block is defence-in-depth so the queueing
    /// schema can be applied to a brand-new platform DB without an
    /// implicit dependency on the tenancy migration order.
    /// </para>
    /// <para>
    /// <b>ALTER DEFAULT PRIVILEGES</b> ensures any future tables added
    /// to the <c>queueing</c> schema (e.g. additional cross-module
    /// queues, extra outbox shards) automatically inherit the same
    /// grants without needing per-table follow-up.
    /// </para>
    /// <para>
    /// <b>RLS posture preserved.</b> <c>nscim_app</c> is created with
    /// <c>NOSUPERUSER NOBYPASSRLS</c> so the
    /// <c>tenant_isolation_*</c> policies on
    /// <c>queueing.outbox</c> / <c>queueing.dead_letter</c> still
    /// apply. Granting CRUD doesn't bypass RLS — the policies still
    /// filter rows.
    /// </para>
    /// </remarks>
    public partial class Add_NscimAppRole_Grants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Idempotent role creation — should already exist via the
            // Tenancy grants migration.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nscim_app') THEN
        CREATE ROLE nscim_app WITH LOGIN NOSUPERUSER NOBYPASSRLS;
    END IF;
END $$;");

            migrationBuilder.Sql("GRANT USAGE ON SCHEMA queueing TO nscim_app;");
            migrationBuilder.Sql(
                "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA queueing TO nscim_app;");
            migrationBuilder.Sql(
                "GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA queueing TO nscim_app;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA queueing "
                + "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO nscim_app;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA queueing "
                + "GRANT USAGE, SELECT ON SEQUENCES TO nscim_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA queueing "
                + "REVOKE USAGE, SELECT ON SEQUENCES FROM nscim_app;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES IN SCHEMA queueing "
                + "REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM nscim_app;");
            migrationBuilder.Sql(
                "REVOKE USAGE, SELECT ON ALL SEQUENCES IN SCHEMA queueing FROM nscim_app;");
            migrationBuilder.Sql(
                "REVOKE SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA queueing FROM nscim_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA queueing FROM nscim_app;");
        }
    }
}
