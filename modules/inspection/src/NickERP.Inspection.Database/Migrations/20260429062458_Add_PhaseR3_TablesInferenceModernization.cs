using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Phase R3 — image-analysis modernization tables. Adds the five tables
    /// specced in the IMAGE-ANALYSIS-MODERNIZATION roadmap:
    ///
    /// <list type="bullet">
    /// <item><c>scanner_threshold_profiles</c> (§6.5.3) — per-scanner
    /// threshold profile with proposed/shadow/active lifecycle.</item>
    /// <item><c>threat_library_provenance</c> (§6.9.4) — in-house threat
    /// library row with chain-of-custody back to the seizure.</item>
    /// <item><c>hs_commodity_reference</c> (§6.10.2) — per-tenant per-HS-6
    /// density / Z_eff window; composite PK <c>(TenantId, Hs6)</c>.</item>
    /// <item><c>outcome_pull_cursors</c> (§6.11.8) — per-instance pull cursor
    /// for the inbound post-hoc outcome adapter.</item>
    /// <item><c>posthoc_rollout_phase</c> (§6.11.13) — per-authority rollout
    /// phase tracker; one row per
    /// <c>(TenantId, ExternalSystemInstanceId)</c>.</item>
    /// </list>
    ///
    /// Also enables + force-enables RLS and installs the
    /// <c>tenant_isolation_*</c> policy on each new table, matching the
    /// pattern applied in F1's <c>Add_RLS_Policies</c> migration. Without
    /// this every new table would be a hole in the tenancy posture
    /// (per <c>reference_rls_now_enforces</c>).
    /// </summary>
    public partial class Add_PhaseR3_TablesInferenceModernization : Migration
    {
        /// <summary>
        /// Tenant-owned tables introduced by this migration. RLS-enable +
        /// FORCE + tenant_isolation_* policy installed for each.
        /// </summary>
        private static readonly string[] TenantedTables = new[]
        {
            "scanner_threshold_profiles",
            "threat_library_provenance",
            "hs_commodity_reference",
            "outcome_pull_cursors",
            "posthoc_rollout_phase"
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hs_commodity_reference",
                schema: "inspection",
                columns: table => new
                {
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    Hs6 = table.Column<string>(type: "character(6)", fixedLength: true, maxLength: 6, nullable: false),
                    ZEffMin = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: false),
                    ZEffMedian = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: false),
                    ZEffMax = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: false),
                    ZEffWindowMethod = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpectedDensityKgPerM3 = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    DensityWindowKgPerM3 = table.Column<string>(type: "text", nullable: true),
                    TypicalPackaging = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    Confidence = table.Column<int>(type: "integer", nullable: false),
                    SourcesJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    SampleCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ScannerCalibrationVersionAtFitJson = table.Column<string>(type: "jsonb", nullable: true),
                    LastValidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    NextReviewDueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hs_commodity_reference", x => new { x.TenantId, x.Hs6 });
                });

            migrationBuilder.CreateTable(
                name: "outcome_pull_cursors",
                schema: "inspection",
                columns: table => new
                {
                    ExternalSystemInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastSuccessfulPullAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastPullWindowUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outcome_pull_cursors", x => x.ExternalSystemInstanceId);
                    table.ForeignKey(
                        name: "FK_outcome_pull_cursors_external_system_instances_ExternalSyst~",
                        column: x => x.ExternalSystemInstanceId,
                        principalSchema: "inspection",
                        principalTable: "external_system_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "posthoc_rollout_phase",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    ExternalSystemInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentPhase = table.Column<int>(type: "integer", nullable: false),
                    PhaseEnteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PromotedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    GateNotesJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_posthoc_rollout_phase", x => x.Id);
                    table.ForeignKey(
                        name: "FK_posthoc_rollout_phase_external_system_instances_ExternalSys~",
                        column: x => x.ExternalSystemInstanceId,
                        principalSchema: "inspection",
                        principalTable: "external_system_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scanner_threshold_profiles",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScannerDeviceInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ValuesJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EffectiveTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProposedBy = table.Column<int>(type: "integer", nullable: false),
                    ProposalRationaleJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ShadowStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ShadowCompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ShadowOutcomeJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scanner_threshold_profiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scanner_threshold_profiles_scanner_device_instances_Scanner~",
                        column: x => x.ScannerDeviceInstanceId,
                        principalSchema: "inspection",
                        principalTable: "scanner_device_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "threat_library_provenance",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ThreatClass = table.Column<int>(type: "integer", nullable: false),
                    ThreatSubclass = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SourceSeizureCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceVerdictId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaptureCaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CapturedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceScannerInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceScannerTypeCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    LePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MaterialZeffPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    AlphaMaskPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PoseCanonicalJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    TagsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Sam2ModelVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SegmentationQualityScore = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: true),
                    RedactionFlagsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    LegalHoldStatus = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_threat_library_provenance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_threat_library_provenance_locations_LocationId",
                        column: x => x.LocationId,
                        principalSchema: "inspection",
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_threat_library_provenance_scanner_device_instances_SourceSc~",
                        column: x => x.SourceScannerInstanceId,
                        principalSchema: "inspection",
                        principalTable: "scanner_device_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hs_commodity_sources_gin",
                schema: "inspection",
                table: "hs_commodity_reference",
                column: "SourcesJson")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_hs_commodity_tenant_inferred",
                schema: "inspection",
                table: "hs_commodity_reference",
                columns: new[] { "TenantId", "Confidence" },
                filter: "\"Confidence\" = 20");

            migrationBuilder.CreateIndex(
                name: "ix_outcome_pull_cursors_tenant",
                schema: "inspection",
                table: "outcome_pull_cursors",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_posthoc_rollout_phase_ExternalSystemInstanceId",
                schema: "inspection",
                table: "posthoc_rollout_phase",
                column: "ExternalSystemInstanceId");

            migrationBuilder.CreateIndex(
                name: "ux_posthoc_rollout_tenant_instance",
                schema: "inspection",
                table: "posthoc_rollout_phase",
                columns: new[] { "TenantId", "ExternalSystemInstanceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scanner_threshold_profiles_tenant",
                schema: "inspection",
                table: "scanner_threshold_profiles",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_scanner_threshold_profiles_active",
                schema: "inspection",
                table: "scanner_threshold_profiles",
                column: "ScannerDeviceInstanceId",
                unique: true,
                filter: "\"Status\" = 20");

            migrationBuilder.CreateIndex(
                name: "ux_scanner_threshold_profiles_tenant_scanner_version",
                schema: "inspection",
                table: "scanner_threshold_profiles",
                columns: new[] { "TenantId", "ScannerDeviceInstanceId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_threat_library_active",
                schema: "inspection",
                table: "threat_library_provenance",
                columns: new[] { "TenantId", "Status" },
                filter: "\"Status\" = 10");

            migrationBuilder.CreateIndex(
                name: "ix_threat_library_legal_hold",
                schema: "inspection",
                table: "threat_library_provenance",
                column: "LegalHoldStatus",
                filter: "\"LegalHoldStatus\" = 10");

            migrationBuilder.CreateIndex(
                name: "IX_threat_library_provenance_LocationId",
                schema: "inspection",
                table: "threat_library_provenance",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_threat_library_provenance_SourceScannerInstanceId",
                schema: "inspection",
                table: "threat_library_provenance",
                column: "SourceScannerInstanceId");

            migrationBuilder.CreateIndex(
                name: "ix_threat_library_tenant",
                schema: "inspection",
                table: "threat_library_provenance",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_threat_library_tenant_class_scanner_type",
                schema: "inspection",
                table: "threat_library_provenance",
                columns: new[] { "TenantId", "ThreatClass", "SourceScannerTypeCode" });

            // Phase F1 parity — enable + FORCE-enable RLS and install the
            // tenant-isolation policy on every new tenant-owned table.
            // Without this the new tables would be holes in the tenancy
            // posture (see reference_rls_now_enforces).
            foreach (var table in TenantedTables)
            {
                migrationBuilder.Sql(
                    $"ALTER TABLE inspection.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"ALTER TABLE inspection.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"CREATE POLICY tenant_isolation_{table} ON inspection.{table} "
                    + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                    + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse RLS first, then drop tables.
            foreach (var table in TenantedTables)
            {
                migrationBuilder.Sql(
                    $"DROP POLICY IF EXISTS tenant_isolation_{table} ON inspection.{table};");
                migrationBuilder.Sql(
                    $"ALTER TABLE inspection.{table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"ALTER TABLE inspection.{table} DISABLE ROW LEVEL SECURITY;");
            }

            migrationBuilder.DropTable(
                name: "hs_commodity_reference",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "outcome_pull_cursors",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "posthoc_rollout_phase",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "scanner_threshold_profiles",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "threat_library_provenance",
                schema: "inspection");
        }
    }
}
