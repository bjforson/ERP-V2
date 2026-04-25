using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Identity.Database.Migrations
{
    /// <inheritdoc />
    public partial class Initial_AddIdentitySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "app_scopes",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AppName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_scopes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "identity_users",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "service_token_identities",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenClientId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_token_identities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_scopes",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdentityUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppScopeCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    GrantedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_scopes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_scopes_identity_users_IdentityUserId",
                        column: x => x.IdentityUserId,
                        principalSchema: "identity",
                        principalTable: "identity_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_token_scopes",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceTokenIdentityId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppScopeCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    GrantedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_token_scopes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_token_scopes_service_token_identities_ServiceTokenI~",
                        column: x => x.ServiceTokenIdentityId,
                        principalSchema: "identity",
                        principalTable: "service_token_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_app_scopes_tenant_app",
                schema: "identity",
                table: "app_scopes",
                columns: new[] { "TenantId", "AppName" });

            migrationBuilder.CreateIndex(
                name: "ux_app_scopes_tenant_code",
                schema: "identity",
                table: "app_scopes",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_identity_users_last_seen",
                schema: "identity",
                table: "identity_users",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "ix_identity_users_tenant",
                schema: "identity",
                table: "identity_users",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_identity_users_tenant_normalized_email",
                schema: "identity",
                table: "identity_users",
                columns: new[] { "TenantId", "NormalizedEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_tokens_tenant",
                schema: "identity",
                table: "service_token_identities",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_service_tokens_tenant_client_id",
                schema: "identity",
                table: "service_token_identities",
                columns: new[] { "TenantId", "TokenClientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_token_scopes_revoked_at",
                schema: "identity",
                table: "service_token_scopes",
                column: "RevokedAt");

            migrationBuilder.CreateIndex(
                name: "IX_service_token_scopes_ServiceTokenIdentityId",
                schema: "identity",
                table: "service_token_scopes",
                column: "ServiceTokenIdentityId");

            migrationBuilder.CreateIndex(
                name: "ix_service_token_scopes_tenant_token_scope",
                schema: "identity",
                table: "service_token_scopes",
                columns: new[] { "TenantId", "ServiceTokenIdentityId", "AppScopeCode" });

            migrationBuilder.CreateIndex(
                name: "IX_user_scopes_IdentityUserId",
                schema: "identity",
                table: "user_scopes",
                column: "IdentityUserId");

            migrationBuilder.CreateIndex(
                name: "ix_user_scopes_revoked_at",
                schema: "identity",
                table: "user_scopes",
                column: "RevokedAt");

            migrationBuilder.CreateIndex(
                name: "ix_user_scopes_tenant_scope",
                schema: "identity",
                table: "user_scopes",
                columns: new[] { "TenantId", "AppScopeCode" });

            migrationBuilder.CreateIndex(
                name: "ix_user_scopes_tenant_user_scope",
                schema: "identity",
                table: "user_scopes",
                columns: new[] { "TenantId", "IdentityUserId", "AppScopeCode" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_scopes",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "service_token_scopes",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_scopes",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "service_token_identities",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "identity_users",
                schema: "identity");
        }
    }
}
