using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Identity.Database.Migrations
{
    /// <summary>
    /// Sprint 21 / Phase B — one-time invite tokens for the first-user
    /// invite flow. Sibling to the Sprint 13 / P2-FU-edge-auth
    /// <c>audit.edge_node_api_keys</c> table.
    ///
    /// <para>
    /// <b>Tenancy posture.</b> Mirrors the FU-icums-signing pattern:
    /// <c>ENABLE</c> + <c>FORCE</c> ROW LEVEL SECURITY plus a
    /// <c>tenant_isolation_invite_tokens</c> policy comparing
    /// <c>"TenantId"</c> against <c>app.tenant_id</c> with the
    /// COALESCE-to-<c>'0'</c> fail-closed default. Plus the
    /// <c>OR app.tenant_id = '-1'</c> system-context opt-in clause
    /// because <see cref="NickERP.Platform.Identity.Database.Services.InviteService.RedeemInviteAsync"/>
    /// looks up the row BEFORE the tenant is known from request context.
    /// Registered in <c>docs/system-context-audit-register.md</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Single-use enforcement.</b> Unique partial index on
    /// <c>(TokenHash) WHERE RedeemedAt IS NULL AND RevokedAt IS NULL</c>
    /// keeps redemption race-safe via Postgres' unique-constraint atomicity:
    /// concurrent <c>UPDATE SET RedeemedAt = now()</c> calls both
    /// validate, but only the winner stays in the active-row index;
    /// the loser hits a unique violation and the service returns
    /// "already redeemed".
    /// </para>
    ///
    /// <para>
    /// <b>Grants.</b> SELECT, INSERT, UPDATE for nscim_app — no DELETE.
    /// Rows are forensic / audit-trail records (alongside the
    /// audit.events lineage); revocation is via UPDATE SET RevokedAt
    /// = now(). Idempotent <c>GRANT</c> / <c>REVOKE</c> in case the
    /// ALTER DEFAULT PRIVILEGES bootstrap missed this table.
    /// </para>
    /// </summary>
    public partial class Add_InviteTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invite_tokens",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    TokenPrefix = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    IntendedRoles = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IssuedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RedeemedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RedeemedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invite_tokens", x => x.Id);
                });

            // Operator UI: list invites by tenant + email.
            migrationBuilder.CreateIndex(
                name: "ix_invite_tokens_tenant_email",
                schema: "identity",
                table: "invite_tokens",
                columns: new[] { "TenantId", "Email" });

            // Single-use partial unique index. EF's CreateIndex models a
            // filter via Annotations, but the migration scaffolder is
            // happier when we declare the filter via a raw SQL hop —
            // keeps the model snapshot stable across providers.
            migrationBuilder.CreateIndex(
                name: "ux_invite_tokens_active_token_hash",
                schema: "identity",
                table: "invite_tokens",
                column: "TokenHash",
                unique: true,
                filter: "\"RedeemedAt\" IS NULL AND \"RevokedAt\" IS NULL");

            // ----------------------------------------------------------------
            // RLS — phase-F1 + system-context opt-in.
            // SetSystemContext writes app.tenant_id = '-1'. The OR clause
            // admits cross-tenant lookups from
            // InviteService.RedeemInviteAsync (the redemption is pre-tenant-
            // resolution, same shape as Sprint 13's edge-auth lookup).
            // After validation the service uses the row's TenantId for
            // downstream user / tenant assignment.
            // ----------------------------------------------------------------
            migrationBuilder.Sql(
                "ALTER TABLE identity.invite_tokens ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE identity.invite_tokens FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_invite_tokens ON identity.invite_tokens "
                + "USING ("
                + "  \"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint "
                + "  OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'"
                + ") "
                + "WITH CHECK ("
                + "  \"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint "
                + "  OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'"
                + ");");

            // ----------------------------------------------------------------
            // Role grants. The Phase-F5 ALTER DEFAULT PRIVILEGES already
            // grants nscim_app the standard CRUD set on every new table in
            // the identity schema. Sprint 21 forbids DELETE on this table —
            // invites are forensic records once issued; revocation is via
            // UPDATE SET RevokedAt. Idempotent GRANT / REVOKE.
            // ----------------------------------------------------------------
            migrationBuilder.Sql(
                "GRANT SELECT, INSERT, UPDATE ON identity.invite_tokens TO nscim_app;");
            migrationBuilder.Sql(
                "REVOKE DELETE ON identity.invite_tokens FROM nscim_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "REVOKE SELECT, INSERT, UPDATE ON identity.invite_tokens FROM nscim_app;");

            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_invite_tokens ON identity.invite_tokens;");
            migrationBuilder.Sql(
                "ALTER TABLE identity.invite_tokens NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE identity.invite_tokens DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "invite_tokens",
                schema: "identity");
        }
    }
}
