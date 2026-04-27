using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <inheritdoc />
    public partial class Add_ScanRenderArtifact : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scan_render_artifacts",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScanArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StorageUri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    WidthPx = table.Column<int>(type: "integer", nullable: false),
                    HeightPx = table.Column<int>(type: "integer", nullable: false),
                    MimeType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RenderedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scan_render_artifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scan_render_artifacts_scan_artifacts_ScanArtifactId",
                        column: x => x.ScanArtifactId,
                        principalSchema: "inspection",
                        principalTable: "scan_artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_render_tenant_artifact",
                schema: "inspection",
                table: "scan_render_artifacts",
                columns: new[] { "TenantId", "ScanArtifactId" });

            migrationBuilder.CreateIndex(
                name: "ux_render_artifact_kind",
                schema: "inspection",
                table: "scan_render_artifacts",
                columns: new[] { "ScanArtifactId", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scan_render_artifacts",
                schema: "inspection");
        }
    }
}
