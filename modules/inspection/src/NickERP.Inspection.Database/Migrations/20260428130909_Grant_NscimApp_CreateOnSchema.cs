using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Phase H3 — grant <c>CREATE</c> on the <c>inspection</c> schema to
    /// <c>nscim_app</c>.
    ///
    /// <para>
    /// EF Core's <see cref="Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.Internal.NpgsqlHistoryRepository"/>
    /// runs <c>CREATE TABLE IF NOT EXISTS &quot;inspection&quot;.&quot;__EFMigrationsHistory&quot;</c>
    /// on every <c>DbContext</c> startup, regardless of whether the
    /// table already exists. Postgres still checks <c>CREATE</c>
    /// privilege on the schema, so this grant is needed even though
    /// the relocate step (see <c>tools/migrations/phase-h3/</c>)
    /// pre-created the table.
    /// </para>
    ///
    /// <para>
    /// This is the alternative to granting <c>CREATE ON SCHEMA public</c>
    /// — by giving <c>nscim_app</c> CREATE only on the schema it already
    /// owns CRUD on, the role's overall surface area shrinks: it
    /// continues to have zero privileges on <c>public</c>.
    /// </para>
    /// </summary>
    public partial class Grant_NscimApp_CreateOnSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("GRANT CREATE ON SCHEMA inspection TO nscim_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE CREATE ON SCHEMA inspection FROM nscim_app;");
        }
    }
}
