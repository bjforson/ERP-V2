using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 31 / B5 — SLA window tracking + cross-record-scan
    /// detection tables.
    ///
    /// <para>
    /// Two new tables in the <c>inspection</c> schema:
    /// <list type="bullet">
    ///   <item><c>sla_window</c> — wall-clock window per (CaseId,
    ///   WindowName); auto-opened on case creation, auto-closed on
    ///   terminal-state transitions.</item>
    ///   <item><c>cross_record_detection</c> — multi-container
    ///   detection candidates the analyst reviews + splits via
    ///   <c>CaseWorkflowService.SplitCaseAsync</c>.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Tenant-scoped + RLS-enforced. Mirrors the
    /// <c>AddRuleEvaluations</c> migration's posture (FORCE RLS +
    /// COALESCE-to-'0' fail-closed default + nscim_app SELECT/INSERT/
    /// UPDATE/DELETE grants — DELETE is permitted on the cross-record
    /// table because dismissed false-positives can be removed cleanly,
    /// but SLA windows keep DELETE off to preserve breach history).
    /// </para>
    /// </summary>
    public partial class Add_SlaWindow_And_CrossRecordDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cross_record_detection",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DetectorVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    DetectedSubjectsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    SplitCaseIdsJson = table.Column<string>(type: "jsonb", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cross_record_detection", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sla_window",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    WindowName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    BudgetMinutes = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sla_window", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cross_record_detection_tenant_case",
                schema: "inspection",
                table: "cross_record_detection",
                columns: new[] { "TenantId", "CaseId" });

            migrationBuilder.CreateIndex(
                name: "ix_cross_record_detection_tenant_state_detected",
                schema: "inspection",
                table: "cross_record_detection",
                columns: new[] { "TenantId", "State", "DetectedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ux_cross_record_detection_case_version",
                schema: "inspection",
                table: "cross_record_detection",
                columns: new[] { "CaseId", "DetectorVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sla_window_tenant_case",
                schema: "inspection",
                table: "sla_window",
                columns: new[] { "TenantId", "CaseId" });

            migrationBuilder.CreateIndex(
                name: "ix_sla_window_tenant_state_due",
                schema: "inspection",
                table: "sla_window",
                columns: new[] { "TenantId", "State", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "ux_sla_window_open_per_case_window",
                schema: "inspection",
                table: "sla_window",
                columns: new[] { "CaseId", "WindowName" },
                unique: true,
                filter: "\"ClosedAt\" IS NULL");

            // Phase F1 parity — enable + FORCE-enable RLS and install
            // tenant-isolation policies. Without this the new tables
            // would be holes in the tenancy posture.
            migrationBuilder.Sql("ALTER TABLE inspection.sla_window ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.sla_window FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_sla_window ON inspection.sla_window "
                + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");

            migrationBuilder.Sql("ALTER TABLE inspection.cross_record_detection ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.cross_record_detection FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_cross_record_detection ON inspection.cross_record_detection "
                + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");

            // Grants. SLA windows: SELECT/INSERT/UPDATE only — DELETE
            // intentionally omitted so breach history survives even if
            // a case is administratively retired. Cross-record
            // detections: SELECT/INSERT/UPDATE/DELETE — dismissed
            // false-positives can be removed cleanly to keep the
            // admin queue tidy.
            migrationBuilder.Sql(@"
GRANT SELECT, INSERT, UPDATE ON inspection.sla_window TO nscim_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON inspection.cross_record_detection TO nscim_app;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_sla_window ON inspection.sla_window;");
            migrationBuilder.Sql("ALTER TABLE inspection.sla_window NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.sla_window DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_cross_record_detection ON inspection.cross_record_detection;");
            migrationBuilder.Sql("ALTER TABLE inspection.cross_record_detection NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.cross_record_detection DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "cross_record_detection",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "sla_window",
                schema: "inspection");
        }
    }
}
