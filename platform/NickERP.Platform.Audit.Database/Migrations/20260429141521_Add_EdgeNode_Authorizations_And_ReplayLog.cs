using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Audit.Database.Migrations
{
    /// <summary>
    /// Sprint 11 / P2 — server-side schema for the edge-node replay
    /// surface. Adds two tables:
    ///
    /// <list type="bullet">
    ///   <item><description><c>audit.edge_node_authorizations</c> — pre-
    ///         configured (edge_node_id, tenant_id) pairs the server
    ///         consults under system context to authorize an incoming
    ///         edge replay batch. Composite PK.</description></item>
    ///   <item><description><c>audit.edge_node_replay_log</c> — one row
    ///         per replay batch the server processed. Operator-facing
    ///         visibility into edge activity (batch size, ok/failed
    ///         counts, per-failure JSON).</description></item>
    /// </list>
    ///
    /// <para>
    /// <b>RLS posture (deliberate skip).</b> Neither table is under
    /// tenant RLS. Rationale:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>edge_node_authorizations</c> is suite-wide
    ///         reference data. The server reads it under system context
    ///         (the replay endpoint is a SetSystemContext caller) to
    ///         determine whether an incoming edge may post events for a
    ///         given tenant. Putting it under tenant RLS would create a
    ///         chicken-and-egg situation: the lookup itself would need
    ///         a tenant scope, but the lookup is what determines
    ///         tenancy.</description></item>
    ///   <item><description><c>edge_node_replay_log</c> rows can carry
    ///         events for multiple tenants in a single batch — a
    ///         per-tenant policy can't filter the row. Reads are
    ///         operator-facing, scoped by an admin role check at the
    ///         endpoint layer (out of scope for v0; v0 has no read
    ///         endpoint, ops query via psql).</description></item>
    /// </list>
    ///
    /// <para>
    /// Both tables get standard <c>nscim_app</c> grants:
    /// <list type="bullet">
    ///   <item><description><c>edge_node_authorizations</c>: SELECT only
    ///         (operator-driven seed via psql; the host cannot mutate
    ///         its own authorization table).</description></item>
    ///   <item><description><c>edge_node_replay_log</c>: SELECT, INSERT
    ///         (the replay endpoint writes one row per batch).
    ///         </description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Audit register entry — see
    /// <c>docs/system-context-audit-register.md</c>; the replay
    /// endpoint is registered as a <c>SetSystemContext()</c> caller
    /// (writes <c>audit.events</c> on behalf of multiple tenants under
    /// system context). The Sprint 5 opt-in clause on
    /// <c>audit.events</c> already admits the writes.
    /// </para>
    /// </summary>
    public partial class Add_EdgeNode_Authorizations_And_ReplayLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "edge_node_authorizations",
                schema: "audit",
                columns: table => new
                {
                    EdgeNodeId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    AuthorizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    AuthorizedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_edge_node_authorizations", x => new { x.EdgeNodeId, x.TenantId });
                });

            migrationBuilder.CreateTable(
                name: "edge_node_replay_log",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    EdgeNodeId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ReplayedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    OkCount = table.Column<int>(type: "integer", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    FailuresJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_edge_node_replay_log", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_edge_node_replay_log_edge_time",
                schema: "audit",
                table: "edge_node_replay_log",
                columns: new[] { "EdgeNodeId", "ReplayedAt" });

            // ----------------------------------------------------------------
            // Role grants. nscim_app gets SELECT on edge_node_authorizations
            // (operator seeds via psql under postgres; the host reads to
            // gate replay) and SELECT + INSERT on edge_node_replay_log
            // (the replay endpoint writes one row per batch). DELETE is
            // withheld on both — pruning is an operator action under
            // postgres.
            //
            // The default ALTER DEFAULT PRIVILEGES set up in
            // Add_NscimAppRole_Grants already grants SELECT + INSERT for
            // future tables; the explicit GRANT here is defence-in-depth
            // (idempotent, harmless, and survives a re-bootstrap that
            // missed the ALTER DEFAULT).
            // ----------------------------------------------------------------
            migrationBuilder.Sql(
                "GRANT SELECT ON audit.edge_node_authorizations TO nscim_app;");
            migrationBuilder.Sql(
                "GRANT SELECT, INSERT ON audit.edge_node_replay_log TO nscim_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "REVOKE SELECT, INSERT ON audit.edge_node_replay_log FROM nscim_app;");
            migrationBuilder.Sql(
                "REVOKE SELECT ON audit.edge_node_authorizations FROM nscim_app;");

            migrationBuilder.DropTable(
                name: "edge_node_authorizations",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "edge_node_replay_log",
                schema: "audit");
        }
    }
}
