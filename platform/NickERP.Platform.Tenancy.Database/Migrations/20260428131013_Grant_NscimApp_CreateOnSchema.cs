using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Tenancy.Database.Migrations
{
    /// <summary>
    /// Phase H3 — grant <c>CREATE</c> on the <c>tenancy</c> schema to
    /// <c>nscim_app</c>. Required because EF Core's history repository
    /// runs <c>CREATE TABLE IF NOT EXISTS</c> on the relocated
    /// <c>tenancy.&quot;__EFMigrationsHistory&quot;</c> at every host
    /// startup — Postgres still checks <c>CREATE</c> privilege.
    ///
    /// <para>This deliberately does NOT grant CREATE on <c>public</c>
    /// — that's the posture-weakener the H3 redo replaces.</para>
    /// </summary>
    public partial class Grant_NscimApp_CreateOnSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("GRANT CREATE ON SCHEMA tenancy TO nscim_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE CREATE ON SCHEMA tenancy FROM nscim_app;");
        }
    }
}
