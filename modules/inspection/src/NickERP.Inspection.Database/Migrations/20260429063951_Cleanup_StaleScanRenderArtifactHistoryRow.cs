using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint-6 follow-up FU-4 — delete the stale
    /// <c>20260427164643_Add_ScanRenderArtifact</c> row from
    /// <c>inspection.&quot;__EFMigrationsHistory&quot;</c>.
    ///
    /// <para>
    /// Background. During Phase H3's relocation of EF migration history out
    /// of the <c>public</c> schema and into per-context schemas, the old
    /// <c>20260427164643_Add_ScanRenderArtifact</c> row was carried into
    /// <c>inspection.&quot;__EFMigrationsHistory&quot;</c> alongside the
    /// live migration <c>20260427164855_Add_ScanRenderArtifact</c>. Benign
    /// (EF only re-applies migrations it cannot find a row for) but it
    /// clutters <c>dotnet ef migrations list</c> output and is just untidy.
    /// See PLAN.md §17.2 / FU-4.
    /// </para>
    ///
    /// <para>
    /// <b>Down is intentionally a no-op — this migration is irreversible.</b>
    /// Re-INSERTing the orphan row would require fabricating a synthetic
    /// <c>ProductVersion</c> string and would re-introduce the very clutter
    /// this migration removes; not worth supporting. If the row truly needs
    /// to come back, do it by hand.
    /// </para>
    /// </summary>
    public partial class Cleanup_StaleScanRenderArtifactHistoryRow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
  DELETE FROM inspection.""__EFMigrationsHistory""
  WHERE ""MigrationId"" = '20260427164643_Add_ScanRenderArtifact';
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // intentionally irreversible — see XML doc on the class.
        }
    }
}
