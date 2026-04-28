using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Audit.Database.Migrations
{
    /// <summary>
    /// Phase H3 — grant <c>CREATE</c> on the <c>audit</c> schema to
    /// <c>nscim_app</c>. Required because EF Core's history repository
    /// runs <c>CREATE TABLE IF NOT EXISTS</c> on the relocated
    /// <c>audit.&quot;__EFMigrationsHistory&quot;</c> at every host
    /// startup — Postgres still checks <c>CREATE</c> privilege.
    ///
    /// <para>This deliberately does NOT grant CREATE on <c>public</c>
    /// — that's the posture-weakener the H3 redo replaces. The audit
    /// schema's append-only posture is preserved at the table level
    /// (<c>nscim_app</c> still only has SELECT + INSERT on existing
    /// audit tables; CREATE on the schema is for EF Core's
    /// idempotent history-table check, not for ad-hoc DDL).</para>
    /// </summary>
    public partial class Grant_NscimApp_CreateOnSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("GRANT CREATE ON SCHEMA audit TO nscim_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE CREATE ON SCHEMA audit FROM nscim_app;");
        }
    }
}
