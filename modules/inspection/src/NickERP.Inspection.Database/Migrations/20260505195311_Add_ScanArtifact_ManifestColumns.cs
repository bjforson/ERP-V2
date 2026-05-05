using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 45 / Phase B — adds canonical-scan-package manifest fields
    /// to <c>inspection.scan_artifacts</c>: the deterministic manifest
    /// JSON, its sha256 digest, the per-edge HMAC signature, and the
    /// server-side verification timestamp. All four columns nullable so
    /// pre-existing rows + legacy ingest paths keep working.
    ///
    /// <para>
    /// The supporting <c>(TenantId, ManifestVerifiedAt DESC)</c> index
    /// backs the SLA dashboard's "newly verified replays" feed without
    /// a sort.
    /// </para>
    ///
    /// <para>
    /// Snapshot drift cleanup: the EF generator emitted extras
    /// (<c>cases.IsSynthetic</c>, <c>scanner_onboarding_responses</c>,
    /// <c>threshold_profile_history</c>, <c>webhook_cursors</c>) because
    /// the Designer files of the migrations in <c>20260505181000</c> /
    /// <c>20260505190000</c> didn't carry those entities even though
    /// they exist in <c>OnModelCreating</c>. Production already has
    /// them via the Sprint 38 + 41 partials; this migration's
    /// hand-edited body keeps only the genuine Sprint 45 changes and
    /// leaves the (corrected) <see cref="Add_ScanArtifact_ManifestColumns.BuildTargetModel"/>
    /// snapshot in the Designer to reconcile the drift on next-up
    /// migration generation.
    /// </para>
    /// </summary>
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

            migrationBuilder.CreateIndex(
                name: "ix_scan_artifacts_tenant_manifest_verified",
                schema: "inspection",
                table: "scan_artifacts",
                columns: new[] { "TenantId", "ManifestVerifiedAt" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
        }
    }
}
