using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <inheritdoc />
    public partial class Add_RetentionClasses_And_LegalHold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LegalHold",
                schema: "inspection",
                table: "scan_artifacts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LegalHoldAppliedAt",
                schema: "inspection",
                table: "scan_artifacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LegalHoldAppliedByUserId",
                schema: "inspection",
                table: "scan_artifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalHoldReason",
                schema: "inspection",
                table: "scan_artifacts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionClass",
                schema: "inspection",
                table: "scan_artifacts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetentionClassSetAt",
                schema: "inspection",
                table: "scan_artifacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RetentionClassSetByUserId",
                schema: "inspection",
                table: "scan_artifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LegalHold",
                schema: "inspection",
                table: "cases",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LegalHoldAppliedAt",
                schema: "inspection",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LegalHoldAppliedByUserId",
                schema: "inspection",
                table: "cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalHoldReason",
                schema: "inspection",
                table: "cases",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionClass",
                schema: "inspection",
                table: "cases",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetentionClassSetAt",
                schema: "inspection",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RetentionClassSetByUserId",
                schema: "inspection",
                table: "cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_scan_artifacts_legal_hold",
                schema: "inspection",
                table: "scan_artifacts",
                columns: new[] { "TenantId", "LegalHold" },
                filter: "\"LegalHold\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "ix_scan_artifacts_retention_class",
                schema: "inspection",
                table: "scan_artifacts",
                columns: new[] { "TenantId", "RetentionClass", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_cases_legal_hold",
                schema: "inspection",
                table: "cases",
                columns: new[] { "TenantId", "LegalHold" },
                filter: "\"LegalHold\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "ix_cases_retention_class",
                schema: "inspection",
                table: "cases",
                columns: new[] { "TenantId", "RetentionClass", "ClosedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_scan_artifacts_legal_hold",
                schema: "inspection",
                table: "scan_artifacts");

            migrationBuilder.DropIndex(
                name: "ix_scan_artifacts_retention_class",
                schema: "inspection",
                table: "scan_artifacts");

            migrationBuilder.DropIndex(
                name: "ix_cases_legal_hold",
                schema: "inspection",
                table: "cases");

            migrationBuilder.DropIndex(
                name: "ix_cases_retention_class",
                schema: "inspection",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "LegalHold",
                schema: "inspection",
                table: "scan_artifacts");

            migrationBuilder.DropColumn(
                name: "LegalHoldAppliedAt",
                schema: "inspection",
                table: "scan_artifacts");

            migrationBuilder.DropColumn(
                name: "LegalHoldAppliedByUserId",
                schema: "inspection",
                table: "scan_artifacts");

            migrationBuilder.DropColumn(
                name: "LegalHoldReason",
                schema: "inspection",
                table: "scan_artifacts");

            migrationBuilder.DropColumn(
                name: "RetentionClass",
                schema: "inspection",
                table: "scan_artifacts");

            migrationBuilder.DropColumn(
                name: "RetentionClassSetAt",
                schema: "inspection",
                table: "scan_artifacts");

            migrationBuilder.DropColumn(
                name: "RetentionClassSetByUserId",
                schema: "inspection",
                table: "scan_artifacts");

            migrationBuilder.DropColumn(
                name: "LegalHold",
                schema: "inspection",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "LegalHoldAppliedAt",
                schema: "inspection",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "LegalHoldAppliedByUserId",
                schema: "inspection",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "LegalHoldReason",
                schema: "inspection",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "RetentionClass",
                schema: "inspection",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "RetentionClassSetAt",
                schema: "inspection",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "RetentionClassSetByUserId",
                schema: "inspection",
                table: "cases");
        }
    }
}
