using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Audit.Database.Migrations
{
    /// <summary>
    /// Sprint 4 / FU-1 — fresh-install grant for the audit context's EF
    /// migrations history table.
    ///
    /// <para>
    /// The audit schema's role posture is append-only at the table level:
    /// <c>nscim_app</c> has SELECT + INSERT and never UPDATE / DELETE /
    /// TRUNCATE on real audit tables (see <c>Add_NscimAppRole_Grants</c>).
    /// EF Core's <see cref="Microsoft.EntityFrameworkCore.Migrations.HistoryRepository" />
    /// however needs to UPDATE / DELETE its own bookkeeping table
    /// (<c>audit.&quot;__EFMigrationsHistory&quot;</c>) and acquires
    /// <c>LOCK TABLE … ACCESS EXCLUSIVE MODE</c> at the start of every
    /// <c>Database.Migrate()</c> — Postgres requires
    /// UPDATE / DELETE / TRUNCATE / MAINTAIN to grant ACCESS EXCLUSIVE.
    /// </para>
    ///
    /// <para>
    /// On installs that flipped over from the H3 cutover, the one-shot
    /// relocate script already added these grants. On a fresh install
    /// (brand-new cluster, <c>dotnet ef database update</c> as
    /// <c>nscim_app</c>), the lock would fail with permission-denied. This
    /// migration closes that gap by granting UPDATE + DELETE on exactly
    /// one table — the EF history table — leaving <c>audit.events</c>
    /// (and any future audit tables) append-only.
    /// </para>
    ///
    /// <para>Surfaced by E1; documented in PLAN.md §15.2 (FU-1).</para>
    /// </summary>
    public partial class Grant_NscimApp_AuditHistoryWriteAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Targeted at the EF bookkeeping table only. audit.events
            // and any other audit table keeps SELECT + INSERT only.
            migrationBuilder.Sql(
                "GRANT UPDATE, DELETE ON audit.\"__EFMigrationsHistory\" TO nscim_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "REVOKE UPDATE, DELETE ON audit.\"__EFMigrationsHistory\" FROM nscim_app;");
        }
    }
}
