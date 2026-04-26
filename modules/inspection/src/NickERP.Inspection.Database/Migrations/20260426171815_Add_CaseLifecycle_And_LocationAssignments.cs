using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <inheritdoc />
    public partial class Add_CaseLifecycle_And_LocationAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cases",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    StationId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubjectType = table.Column<int>(type: "integer", nullable: false),
                    SubjectIdentifier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SubjectPayloadJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    State = table.Column<int>(type: "integer", nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    StateEnteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OpenedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedAnalystUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cases_locations_LocationId",
                        column: x => x.LocationId,
                        principalSchema: "inspection",
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cases_stations_StationId",
                        column: x => x.StationId,
                        principalSchema: "inspection",
                        principalTable: "stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "location_assignments",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdentityUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Roles = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, defaultValue: ""),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    GrantedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_location_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_location_assignments_locations_LocationId",
                        column: x => x.LocationId,
                        principalSchema: "inspection",
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "authority_documents",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalSystemInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReferenceNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authority_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_authority_documents_cases_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "inspection",
                        principalTable: "cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_authority_documents_external_system_instances_ExternalSyste~",
                        column: x => x.ExternalSystemInstanceId,
                        principalSchema: "inspection",
                        principalTable: "external_system_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outbound_submissions",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalSystemInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "pending"),
                    ResponseJson = table.Column<string>(type: "jsonb", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    RespondedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbound_submissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_outbound_submissions_cases_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "inspection",
                        principalTable: "cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_outbound_submissions_external_system_instances_ExternalSyst~",
                        column: x => x.ExternalSystemInstanceId,
                        principalSchema: "inspection",
                        principalTable: "external_system_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "review_sessions",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalystUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "in-progress"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_review_sessions_cases_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "inspection",
                        principalTable: "cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scans",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScannerDeviceInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OperatorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scans_cases_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "inspection",
                        principalTable: "cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_scans_scanner_device_instances_ScannerDeviceInstanceId",
                        column: x => x.ScannerDeviceInstanceId,
                        principalSchema: "inspection",
                        principalTable: "scanner_device_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "verdicts",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    Basis = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisedVerdictId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verdicts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_verdicts_cases_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "inspection",
                        principalTable: "cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "analyst_reviews",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimeToDecisionMs = table.Column<int>(type: "integer", nullable: false),
                    RoiInteractionsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    ConfidenceScore = table.Column<double>(type: "double precision", nullable: false),
                    VerdictChangesJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    PeerDisagreementCount = table.Column<int>(type: "integer", nullable: false),
                    PostHocOutcomeJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analyst_reviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_analyst_reviews_review_sessions_ReviewSessionId",
                        column: x => x.ReviewSessionId,
                        principalSchema: "inspection",
                        principalTable: "review_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scan_artifacts",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScanId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Primary"),
                    StorageUri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WidthPx = table.Column<int>(type: "integer", nullable: false),
                    HeightPx = table.Column<int>(type: "integer", nullable: false),
                    Channels = table.Column<int>(type: "integer", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scan_artifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scan_artifacts_scans_ScanId",
                        column: x => x.ScanId,
                        principalSchema: "inspection",
                        principalTable: "scans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "findings",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalystReviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    FindingType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "info"),
                    LocationInImageJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_findings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_findings_analyst_reviews_AnalystReviewId",
                        column: x => x.AnalystReviewId,
                        principalSchema: "inspection",
                        principalTable: "analyst_reviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_analyst_reviews_ReviewSessionId",
                schema: "inspection",
                table: "analyst_reviews",
                column: "ReviewSessionId");

            migrationBuilder.CreateIndex(
                name: "ix_authority_docs_tenant_case",
                schema: "inspection",
                table: "authority_documents",
                columns: new[] { "TenantId", "CaseId" });

            migrationBuilder.CreateIndex(
                name: "ix_authority_docs_tenant_ref",
                schema: "inspection",
                table: "authority_documents",
                columns: new[] { "TenantId", "ReferenceNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_authority_documents_CaseId",
                schema: "inspection",
                table: "authority_documents",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_authority_documents_ExternalSystemInstanceId",
                schema: "inspection",
                table: "authority_documents",
                column: "ExternalSystemInstanceId");

            migrationBuilder.CreateIndex(
                name: "ix_cases_assigned_analyst",
                schema: "inspection",
                table: "cases",
                column: "AssignedAnalystUserId");

            migrationBuilder.CreateIndex(
                name: "IX_cases_LocationId",
                schema: "inspection",
                table: "cases",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_cases_StationId",
                schema: "inspection",
                table: "cases",
                column: "StationId");

            migrationBuilder.CreateIndex(
                name: "ix_cases_tenant_loc_state_time",
                schema: "inspection",
                table: "cases",
                columns: new[] { "TenantId", "LocationId", "State", "OpenedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_cases_tenant_subject",
                schema: "inspection",
                table: "cases",
                columns: new[] { "TenantId", "SubjectIdentifier" });

            migrationBuilder.CreateIndex(
                name: "IX_findings_AnalystReviewId",
                schema: "inspection",
                table: "findings",
                column: "AnalystReviewId");

            migrationBuilder.CreateIndex(
                name: "ix_findings_severity",
                schema: "inspection",
                table: "findings",
                column: "Severity");

            migrationBuilder.CreateIndex(
                name: "ix_findings_tenant_type",
                schema: "inspection",
                table: "findings",
                columns: new[] { "TenantId", "FindingType" });

            migrationBuilder.CreateIndex(
                name: "ix_location_assignments_loc",
                schema: "inspection",
                table: "location_assignments",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "ix_location_assignments_user",
                schema: "inspection",
                table: "location_assignments",
                column: "IdentityUserId");

            migrationBuilder.CreateIndex(
                name: "ux_location_assignments_tenant_user_loc",
                schema: "inspection",
                table: "location_assignments",
                columns: new[] { "TenantId", "IdentityUserId", "LocationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbound_submissions_CaseId",
                schema: "inspection",
                table: "outbound_submissions",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_outbound_submissions_ExternalSystemInstanceId",
                schema: "inspection",
                table: "outbound_submissions",
                column: "ExternalSystemInstanceId");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_tenant_status",
                schema: "inspection",
                table: "outbound_submissions",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_outbound_tenant_idempotency",
                schema: "inspection",
                table: "outbound_submissions",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_review_sessions_analyst",
                schema: "inspection",
                table: "review_sessions",
                column: "AnalystUserId");

            migrationBuilder.CreateIndex(
                name: "IX_review_sessions_CaseId",
                schema: "inspection",
                table: "review_sessions",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "ix_review_sessions_tenant_case_time",
                schema: "inspection",
                table: "review_sessions",
                columns: new[] { "TenantId", "CaseId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_scan_artifacts_content_hash",
                schema: "inspection",
                table: "scan_artifacts",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_scan_artifacts_ScanId",
                schema: "inspection",
                table: "scan_artifacts",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "ix_scan_artifacts_tenant_scan",
                schema: "inspection",
                table: "scan_artifacts",
                columns: new[] { "TenantId", "ScanId" });

            migrationBuilder.CreateIndex(
                name: "IX_scans_CaseId",
                schema: "inspection",
                table: "scans",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "ix_scans_device",
                schema: "inspection",
                table: "scans",
                column: "ScannerDeviceInstanceId");

            migrationBuilder.CreateIndex(
                name: "ix_scans_tenant_case_time",
                schema: "inspection",
                table: "scans",
                columns: new[] { "TenantId", "CaseId", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_scans_tenant_idempotency",
                schema: "inspection",
                table: "scans",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_verdicts_tenant_decision_time",
                schema: "inspection",
                table: "verdicts",
                columns: new[] { "TenantId", "Decision", "DecidedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_verdicts_case",
                schema: "inspection",
                table: "verdicts",
                column: "CaseId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "authority_documents",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "findings",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "location_assignments",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "outbound_submissions",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "scan_artifacts",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "verdicts",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "analyst_reviews",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "scans",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "review_sessions",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "cases",
                schema: "inspection");
        }
    }
}
