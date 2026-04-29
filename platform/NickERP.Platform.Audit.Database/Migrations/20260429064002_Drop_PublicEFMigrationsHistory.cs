using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Audit.Database.Migrations
{
    /// <summary>
    /// FU-6 — drop the pre-H3 <c>public.__EFMigrationsHistory</c> remnant
    /// from <c>nickerp_platform</c>.
    ///
    /// <para>
    /// H3 (Sprint 2 hardening) relocated EF migration history out of
    /// <c>public</c> and into per-context schemas
    /// (<c>audit.__EFMigrationsHistory</c>,
    /// <c>identity.__EFMigrationsHistory</c>,
    /// <c>tenancy.__EFMigrationsHistory</c>). The original
    /// <c>public</c> copy was deliberately left in place during the
    /// cutover to provide a rollback safety net. Production has run
    /// cleanly on per-context history since then, so the orphan can come
    /// out.
    /// </para>
    ///
    /// <para>
    /// Attached to <see cref="AuditDbContext"/> rather than Identity /
    /// Tenancy because the audit schema is the foundational platform
    /// context — on a fresh install <c>Database.Migrate()</c> typically
    /// hits Audit first, ensuring the cleanup runs even on greenfield
    /// hosts (where <c>public.__EFMigrationsHistory</c> won't exist —
    /// hence <c>DROP TABLE IF EXISTS</c> for idempotence).
    /// </para>
    ///
    /// <para>
    /// The other two platform contexts (Identity, Tenancy) share the
    /// same <c>nickerp_platform</c> database; one drop here clears the
    /// orphan for all three. The companion migration on
    /// <c>InspectionDbContext</c> handles <c>nickerp_inspection</c>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Down is intentionally a no-op: the pre-H3 history rows are gone
    /// (they were never copied to the per-context tables wholesale —
    /// the relocate step rebuilt them from the migration filesystem),
    /// so there is nothing meaningful to restore. A rollback past this
    /// point would have to repopulate from a database backup.
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
