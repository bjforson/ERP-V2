using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 44 Phase A — closes the schema gap left by Sprint 41.
    ///
    /// <para>
    /// The Sprint 41 partial-batch landed three new entities
    /// (<c>ScannerOnboardingResponse</c>, <c>ThresholdProfileHistory</c>,
    /// <c>WebhookCursor</c>) on the <see cref="InspectionDbContext"/> + the
    /// model snapshot, but no migration ever materialised the tables —
    /// the master agent's Designer.cs gap left three empty stubs. Without
    /// this migration any production code-path touching those entities
    /// fails at runtime with <c>relation does not exist</c>.
    /// </para>
    ///
    /// <para>
    /// Three tables in the <c>inspection</c> schema:
    /// <list type="bullet">
    ///   <item><c>scanner_onboarding_responses</c> — append-on-overwrite
    ///   questionnaire rows; reader takes latest <c>RecordedAt</c> per
    ///   field. <b>Append-only</b> — SELECT/INSERT/UPDATE only, no
    ///   DELETE.</item>
    ///   <item><c>threshold_profile_history</c> — diff trail per threshold
    ///   change. <b>Append-only</b> — SELECT/INSERT only, no UPDATE / no
    ///   DELETE (mirrors <c>audit.events</c> posture).</item>
    ///   <item><c>webhook_cursors</c> — per-tenant per-adapter cursor
    ///   into <c>audit.events</c>. SELECT/INSERT/UPDATE; no DELETE so
    ///   re-adding an adapter resumes from where it left off.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Tenant-scoped + RLS-enforced. Mirrors the Sprint 31
    /// <c>Add_SlaWindow_And_CrossRecordDetection</c> migration — FORCE
    /// RLS + COALESCE-to-'0' fail-closed default + nscim_app GRANTs
    /// (DELETE deliberately omitted on all three).
    /// </para>
    ///
    /// <para>
    /// <b>Hand-written Up/Down.</b> EF generated an empty stub because
    /// the model snapshot already tracks these entities (the Sprint 41
    /// partial recorded the entity shapes in the snapshot but never
    /// emitted DDL). The Up() body below mirrors what
    /// <c>dotnet ef migrations add</c> would have produced if the
    /// snapshot were unaware of these entities; the snapshot stays as-is.
    /// </para>
    /// </summary>
    public partial class Add_Sprint41_GovernanceTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ----- scanner_onboarding_responses ------------------------
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

            // ----- threshold_profile_history ---------------------------
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

            // ----- webhook_cursors -------------------------------------
            migrationBuilder.CreateTable(
                name: "webhook_cursors",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AdapterName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastProcessedEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_cursors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_webhook_cursors_tenant_adapter",
                schema: "inspection",
                table: "webhook_cursors",
                columns: new[] { "TenantId", "AdapterName" },
                unique: true);

            // ----- RLS posture for the three new tables ----------------
            // Mirror Phase F1 / Sprint 31 Add_SlaWindow migration:
            //   ENABLE + FORCE Row Level Security
            //   CREATE POLICY tenant_isolation_<table>
            //   COALESCE(current_setting('app.tenant_id', true), '0')::bigint
            //     so unset → fail-closed at TenantId=0.
            string[] tables = new[]
            {
                "scanner_onboarding_responses",
                "threshold_profile_history",
                "webhook_cursors"
            };
            foreach (var t in tables)
            {
                migrationBuilder.Sql($"ALTER TABLE inspection.{t} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE inspection.{t} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"CREATE POLICY tenant_isolation_{t} ON inspection.{t} "
                    + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                    + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");
            }

            // ----- Grants ----------------------------------------------
            // scanner_onboarding_responses: append-on-overwrite — SELECT/
            //   INSERT/UPDATE. No DELETE (questionnaire history survives
            //   so compliance audits can replay).
            // threshold_profile_history: append-only mirror of audit.events
            //   posture — SELECT/INSERT only. No UPDATE / no DELETE — once
            //   a threshold change is recorded, it cannot be rewritten or
            //   removed. Reversibility comes from emitting a follow-up
            //   row, not in-place edit.
            // webhook_cursors: SELECT/INSERT/UPDATE. No DELETE so removing
            //   an adapter doesn't drop the cursor; re-adding the adapter
            //   resumes from where it left off.
            migrationBuilder.Sql(@"
GRANT SELECT, INSERT, UPDATE ON inspection.scanner_onboarding_responses TO nscim_app;
GRANT SELECT, INSERT ON inspection.threshold_profile_history TO nscim_app;
GRANT SELECT, INSERT, UPDATE ON inspection.webhook_cursors TO nscim_app;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            string[] tables = new[]
            {
                "scanner_onboarding_responses",
                "threshold_profile_history",
                "webhook_cursors"
            };
            foreach (var t in tables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation_{t} ON inspection.{t};");
                migrationBuilder.Sql($"ALTER TABLE inspection.{t} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE inspection.{t} DISABLE ROW LEVEL SECURITY;");
            }

            migrationBuilder.DropTable(
                name: "scanner_onboarding_responses",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "threshold_profile_history",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "webhook_cursors",
                schema: "inspection");
        }
    }
}
