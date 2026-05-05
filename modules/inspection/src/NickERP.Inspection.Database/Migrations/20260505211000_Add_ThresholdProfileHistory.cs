using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 41 / Phase B — adds <c>inspection.threshold_profile_history</c>.
    ///
    /// <para>
    /// Append-only history of threshold-value changes per scanner. One
    /// row per (ScannerDeviceInstanceId, ModelId, ClassId) per change.
    /// Auto-emitted from <c>ThresholdAdminService.ApproveAsync</c>.
    /// Closes the "reversible" half of doc-analysis Table 21
    /// (threshold changes must be role-controlled, logged and
    /// reversible) — the existing <c>ScannerThresholdProfile</c> table
    /// already handles role-controlled + logged via the proposal flow;
    /// this table makes the per-class diff queryable directly so an
    /// admin can roll back a single threshold without reconstructing
    /// JSON values.
    /// </para>
    ///
    /// <para>
    /// Tenant-scoped + RLS-enforced. Mirrors the audit-events posture:
    /// SELECT + INSERT only on <c>nscim_app</c> — no UPDATE / DELETE.
    /// </para>
    /// </summary>
    public partial class Add_ThresholdProfileHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "threshold_profile_history",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScannerDeviceInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ClassId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OldThreshold = table.Column<double>(type: "double precision", nullable: true),
                    NewThreshold = table.Column<double>(type: "double precision", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_threshold_profile_history", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_threshold_history_tenant",
                schema: "inspection",
                table: "threshold_profile_history",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_threshold_history_tenant_scanner_time",
                schema: "inspection",
                table: "threshold_profile_history",
                columns: new[] { "TenantId", "ScannerDeviceInstanceId", "ChangedAt" },
                descending: new[] { false, false, true });

            // RLS — same posture as every other inspection table.
            migrationBuilder.Sql("ALTER TABLE inspection.threshold_profile_history ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.threshold_profile_history FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_threshold_profile_history ON inspection.threshold_profile_history "
                + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");

            // SELECT + INSERT only — append-only. Mirrors audit.events
            // posture so the "reversibility" trail is itself tamper-
            // evident.
            migrationBuilder.Sql(@"
GRANT SELECT, INSERT ON inspection.threshold_profile_history TO nscim_app;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_threshold_profile_history ON inspection.threshold_profile_history;");
            migrationBuilder.Sql("ALTER TABLE inspection.threshold_profile_history NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.threshold_profile_history DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "threshold_profile_history",
                schema: "inspection");
        }
    }
}
