using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickFinance.Coa.Migrations
{
    /// <inheritdoc />
    public partial class Initial_Coa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "coa");

            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "coa",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    parent_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    is_control = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounts_tenant_type_active",
                schema: "coa",
                table: "accounts",
                columns: new[] { "tenant_id", "type", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_accounts_tenant_code",
                schema: "coa",
                table: "accounts",
                columns: new[] { "tenant_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "accounts",
                schema: "coa");
        }
    }
}
