using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <inheritdoc />
    public partial class Add_ScanArtifact_ManifestColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ManifestJson",
                schema: "inspection",
                table: "scan_artifacts",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ManifestSha256",
                schema: "inspection",
                table: "scan_artifacts",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ManifestSignature",
                schema: "inspection",
                table: "scan_artifacts",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ManifestVerifiedAt",
                schema: "inspection",
                table: "scan_artifacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSynthetic",
                schema: "inspection",
                table: "cases",
                type: "boolean",
                nullable: false,
                defaultValue: false);

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
                name: "ix_scan_artifacts_tenant_manifest_verified",
                schema: "inspection",
                table: "scan_artifacts",
                columns: new[] { "TenantId", "ManifestVerifiedAt" },
                descending: new[] { false, true });

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

            migrationBuilder.CreateIndex(
                name: "ux_webhook_cursors_tenant_adapter",
                schema: "inspection",
                table: "webhook_cursors",
                columns: new[] { "TenantId", "AdapterName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scanner_onboarding_responses",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "threshold_profile_history",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "webhook_cursors",
                schema: "inspection");

            migrationBuilder.DropIndex(
                name: "ix_scan_artifacts_tenant_manifest_verified",
                schema: "inspection",
                table: "scan_artifacts");

            migrationBuilder.DropColumn(
                name: "ManifestJson",
                schema: "inspection",
                table: "scan_artifacts");

            migrationBuilder.DropColumn(
                name: "ManifestSha256",
                schema: "inspection",
                table: "scan_artifacts");

            migrationBuilder.DropColumn(
                name: "ManifestSignature",
                schema: "inspection",
                table: "scan_artifacts");

            migrationBuilder.DropColumn(
                name: "ManifestVerifiedAt",
                schema: "inspection",
                table: "scan_artifacts");

            migrationBuilder.DropColumn(
                name: "IsSynthetic",
                schema: "inspection",
                table: "cases");
        }
    }
}
