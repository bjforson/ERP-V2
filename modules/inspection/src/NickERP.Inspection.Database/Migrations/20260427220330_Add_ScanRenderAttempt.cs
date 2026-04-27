using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Phase F5 — sibling tracking table for the
    /// <c>PreRenderWorker</c>. Records every failed render attempt so
    /// the worker can stop retrying after
    /// <c>ImagingOptions.MaxRenderAttempts</c>.
    ///
    /// Also enables + force-enables RLS and installs the
    /// tenant-isolation policy, matching the pattern applied to every
    /// tenant-owned table in F1's <c>Add_RLS_Policies</c>.
    /// </summary>
    public partial class Add_ScanRenderAttempt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scan_render_attempts",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScanArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    PermanentlyFailedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scan_render_attempts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_render_attempt_failed",
                schema: "inspection",
                table: "scan_render_attempts",
                column: "PermanentlyFailedAt");

            migrationBuilder.CreateIndex(
                name: "ix_render_attempt_tenant",
                schema: "inspection",
                table: "scan_render_attempts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_render_attempt_artifact_kind",
                schema: "inspection",
                table: "scan_render_attempts",
                columns: new[] { "ScanArtifactId", "Kind" },
                unique: true);

            // Phase F1 parity — enable + FORCE-enable RLS and install the
            // tenant-isolation policy. Without this the new table would be
            // a hole in the tenancy posture.
            migrationBuilder.Sql("ALTER TABLE inspection.scan_render_attempts ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.scan_render_attempts FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_scan_render_attempts ON inspection.scan_render_attempts "
                + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation_scan_render_attempts ON inspection.scan_render_attempts;");
            migrationBuilder.Sql("ALTER TABLE inspection.scan_render_attempts NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.scan_render_attempts DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "scan_render_attempts",
                schema: "inspection");
        }
    }
}
