using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// FU-6 — drop the pre-H3 <c>public.__EFMigrationsHistory</c> remnant
    /// from <c>nickerp_inspection</c>.
    ///
    /// <para>
    /// H3 (Sprint 2 hardening) relocated this DbContext's migration
    /// history into <c>inspection.__EFMigrationsHistory</c>. The
    /// <c>public</c> copy was kept around as a rollback safety net.
    /// Production is now stable on per-context history, so the orphan
    /// can be removed.
    /// </para>
    ///
    /// <para>
    /// <c>DROP TABLE IF EXISTS</c> keeps the migration safe on fresh
    /// post-H3 installs that never had a <c>public</c> history table.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Down is intentionally a no-op: the pre-H3 history rows are gone,
    /// so there is nothing meaningful to restore. Rollback past this
    /// point would require a database backup.
    /// </remarks>
    public partial class Drop_PublicEFMigrationsHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS public.""__EFMigrationsHistory"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // intentionally no-op; pre-H3 history is gone
        }
    }
}
