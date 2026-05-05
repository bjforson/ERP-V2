using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 41 / Phase A — adds <c>inspection.scanner_onboarding_responses</c>.
    ///
    /// <para>
    /// One row per (TenantId, ScannerDeviceTypeId, FieldName) per
    /// recording. Append-on-overwrite — the reader takes the latest
    /// <c>RecordedAt</c> per field. Captures the structured vendor-survey
    /// metadata (Annex B Table 55 — manufacturer/model, image export
    /// format + metadata, API/SDK availability, network access, image
    /// ownership, performance, image size, material channels, dual-view
    /// pairing, time sync, operator identity, local storage, legal
    /// constraints) for compliance + future adapter authoring.
    /// </para>
    ///
    /// <para>
    /// Tenant-scoped + RLS-enforced (FORCE RLS + COALESCE-to-'0'
    /// fail-closed default). Mirrors the
    /// <c>Add_SlaWindow_And_CrossRecordDetection</c> migration's
    /// posture. <c>nscim_app</c> gets SELECT / INSERT — no UPDATE
    /// (corrections are new rows; the reader takes the latest) and
    /// no DELETE (history is part of the compliance posture).
    /// </para>
    /// </summary>
    public partial class Add_ScannerOnboardingResponses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scanner_onboarding_responses",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScannerDeviceTypeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FieldName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scanner_onboarding_responses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scanner_onboarding_tenant",
                schema: "inspection",
                table: "scanner_onboarding_responses",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_scanner_onboarding_tenant_type_field_time",
                schema: "inspection",
                table: "scanner_onboarding_responses",
                columns: new[] { "TenantId", "ScannerDeviceTypeId", "FieldName", "RecordedAt" },
                descending: new[] { false, false, false, true });

            // RLS — same posture as every other inspection table.
            migrationBuilder.Sql("ALTER TABLE inspection.scanner_onboarding_responses ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.scanner_onboarding_responses FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_scanner_onboarding_responses ON inspection.scanner_onboarding_responses "
                + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");

            // SELECT + INSERT only — corrections are new rows; the
            // reader takes the latest. No UPDATE / DELETE keeps the
            // questionnaire history queryable for compliance review.
            migrationBuilder.Sql(@"
GRANT SELECT, INSERT ON inspection.scanner_onboarding_responses TO nscim_app;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_scanner_onboarding_responses ON inspection.scanner_onboarding_responses;");
            migrationBuilder.Sql("ALTER TABLE inspection.scanner_onboarding_responses NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.scanner_onboarding_responses DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "scanner_onboarding_responses",
                schema: "inspection");
        }
    }
}
