using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Audit.Database.Migrations
{
    /// <summary>
    /// Sprint 13 / P2-FU-edge-auth — per-edge-node API keys for the
    /// <c>/api/edge/replay</c> endpoint, sibling to the Sprint 11
    /// <c>edge_node_authorizations</c> reference table.
    ///
    /// <para>
    /// Mirrors the F1 / FU-icums-signing RLS posture: <c>ENABLE</c> +
    /// <c>FORCE ROW LEVEL SECURITY</c>, plus a
    /// <c>tenant_isolation_edge_node_api_keys</c> policy comparing
    /// <c>"TenantId"</c> against the session-local <c>app.tenant_id</c>
    /// setting with COALESCE-to-<c>'0'</c> fail-closed default.
    /// </para>
    ///
    /// <para>
    /// <b>System-context opt-in.</b> The <c>EdgeAuthHandler</c> looks
    /// up the key BEFORE any tenant context exists (the request is
    /// pre-auth), so it calls <see cref="NickERP.Platform.Tenancy.ITenantContext.SetSystemContext"/>
    /// which sets <c>app.tenant_id = '-1'</c>. The policy admits this
    /// via the <c>OR app.tenant_id = '-1'</c> clause. After the
    /// authoritative tenant set is computed (from
    /// <c>edge_node_authorizations</c>), the handler checks the row's
    /// <c>TenantId</c> matches one of the authorized tenants.
    /// </para>
    ///
    /// <para>
    /// Grants follow the FU-icums-signing pattern: SELECT, INSERT,
    /// UPDATE — no DELETE. Revoke is via <c>UPDATE SET RevokedAt =
    /// now()</c>; rows are never deleted (audit posture).
    /// </para>
    /// </summary>
    public partial class Add_EdgeNodeApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "edge_node_api_keys",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    EdgeNodeId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    KeyHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    KeyPrefix = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_edge_node_api_keys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_edge_node_api_keys_tenant_edge",
                schema: "audit",
                table: "edge_node_api_keys",
                columns: new[] { "TenantId", "EdgeNodeId" });

            migrationBuilder.CreateIndex(
                name: "ux_edge_node_api_keys_keyhash",
                schema: "audit",
                table: "edge_node_api_keys",
                column: "KeyHash",
                unique: true);

            // ----------------------------------------------------------------
            // RLS — mirrors phase-F1 + FU-icums-signing pattern. The OR clause
            // on app.tenant_id = '-1' admits writes/reads from the
            // EdgeAuthHandler, which runs under SetSystemContext (pre-tenant-
            // resolution lookup). The handler still asserts row.TenantId is
            // among the authorized tenants for the edge node post-lookup;
            // the OR clause is the gate that lets the lookup itself succeed.
            // ----------------------------------------------------------------
            migrationBuilder.Sql(
                "ALTER TABLE audit.edge_node_api_keys ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE audit.edge_node_api_keys FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_edge_node_api_keys ON audit.edge_node_api_keys "
                + "USING ("
                + "  \"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint "
                + "  OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'"
                + ") "
                + "WITH CHECK ("
                + "  \"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint "
                + "  OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'"
                + ");");

            // ----------------------------------------------------------------
            // Role grants. ALTER DEFAULT PRIVILEGES from
            // 20260427221238_Add_NscimAppRole_Grants already grants nscim_app
            // the standard CRUD set on every new table in the audit schema.
            // The spec for FU-edge-auth forbids DELETE on this table — the
            // revoke flow is UPDATE SET RevokedAt; rows are never removed
            // (audit / forensic posture). Explicitly REVOKE DELETE; explicit
            // GRANTs are defence-in-depth (idempotent, harmless, survives a
            // re-bootstrap that missed the ALTER DEFAULT).
            // ----------------------------------------------------------------
            migrationBuilder.Sql(
                "GRANT SELECT, INSERT, UPDATE ON audit.edge_node_api_keys TO nscim_app;");
            migrationBuilder.Sql(
                "REVOKE DELETE ON audit.edge_node_api_keys FROM nscim_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "REVOKE SELECT, INSERT, UPDATE ON audit.edge_node_api_keys FROM nscim_app;");

            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_edge_node_api_keys ON audit.edge_node_api_keys;");
            migrationBuilder.Sql(
                "ALTER TABLE audit.edge_node_api_keys NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE audit.edge_node_api_keys DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "edge_node_api_keys",
                schema: "audit");
        }
    }
}
