using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Tenancy.Database.Migrations
{
    /// <summary>
    /// Sprint 25 — Tenant lifecycle Pt 3 (scoped export tooling). Adds
    /// the cross-tenant <c>tenancy.tenant_export_requests</c> queue
    /// table that records every platform-admin-initiated export
    /// through Pending / Running / Completed / Failed / Expired /
    /// Revoked. Indexed for the per-tenant admin view + the runner's
    /// pickup query.
    /// </summary>
    /// <remarks>
    /// Same posture as <c>tenant_purge_log</c>: not under RLS,
    /// cross-tenant by design (admin tooling). nscim_app gets SELECT +
    /// INSERT + UPDATE; DELETE stays out (revoke writes a status flip,
    /// expiry reads run via the runner's UPDATE).
    /// </remarks>
    public partial class Add_TenantExportRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_export_requests",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Format = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Scope = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ArtifactPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ArtifactSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    ArtifactSha256 = table.Column<byte[]>(type: "bytea", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DownloadCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LastDownloadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_export_requests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_export_requests_status_requestedat",
                schema: "tenancy",
                table: "tenant_export_requests",
                columns: new[] { "Status", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_export_requests_tenant_requestedat",
                schema: "tenancy",
                table: "tenant_export_requests",
                columns: new[] { "TenantId", "RequestedAt" },
                descending: new[] { false, true });

            // Grants: app code needs SELECT (list / status) +
            // INSERT (request) + UPDATE (runner status flips, download
            // counter bumps, revoke). DELETE intentionally omitted —
            // expiry is a status flip, not a row delete; the audit
            // trail survives.
            migrationBuilder.Sql(@"
GRANT SELECT, INSERT, UPDATE ON tenancy.tenant_export_requests TO nscim_app;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_export_requests",
                schema: "tenancy");
        }
    }
}
