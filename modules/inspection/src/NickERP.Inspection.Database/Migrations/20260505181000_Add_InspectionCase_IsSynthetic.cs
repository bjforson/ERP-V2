using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 38 / Phase D — adds <c>IsSynthetic</c> bool to
    /// <c>inspection.cases</c>. Default <c>false</c>; production /
    /// pilot creation paths leave it alone. Tests + seed fixtures opt
    /// in to <c>true</c> so the pilot-readiness probe
    /// <c>gate.analyst.decisioned_real_case</c> can distinguish "the
    /// system has demonstrated end-to-end correctness on production
    /// data" from "tests + seeders set the table on fire". No
    /// backfill required: existing rows take the default <c>false</c>
    /// (which matches their meaning — they're production data).
    /// </summary>
    public partial class Add_InspectionCase_IsSynthetic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSynthetic",
                schema: "inspection",
                table: "cases",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSynthetic",
                schema: "inspection",
                table: "cases");
        }
    }
}
