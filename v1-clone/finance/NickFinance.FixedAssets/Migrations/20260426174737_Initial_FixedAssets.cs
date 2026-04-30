using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickFinance.FixedAssets.Migrations
{
    /// <inheritdoc />
    public partial class Initial_FixedAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "fixed_assets");

            migrationBuilder.CreateTable(
                name: "assets",
                schema: "fixed_assets",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_tag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<short>(type: "smallint", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acquired_on = table.Column<DateOnly>(type: "date", nullable: false),
                    acquisition_cost_minor = table.Column<long>(type: "bigint", nullable: false),
                    salvage_value_minor = table.Column<long>(type: "bigint", nullable: false),
                    useful_life_months = table.Column<int>(type: "integer", nullable: false),
                    method = table.Column<short>(type: "smallint", nullable: false),
                    declining_balance_rate = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false),
                    accumulated_depreciation_minor = table.Column<long>(type: "bigint", nullable: false),
                    last_depreciated_through = table.Column<DateOnly>(type: "date", nullable: true),
                    cost_account = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    accumulated_depreciation_account = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    depreciation_expense_account = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    disposed_on = table.Column<DateOnly>(type: "date", nullable: true),
                    disposal_proceeds_minor = table.Column<long>(type: "bigint", nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => x.asset_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_status_cat",
                schema: "fixed_assets",
                table: "assets",
                columns: new[] { "tenant_id", "status", "category" });

            migrationBuilder.CreateIndex(
                name: "ux_assets_tenant_tag",
                schema: "fixed_assets",
                table: "assets",
                columns: new[] { "tenant_id", "asset_tag" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assets",
                schema: "fixed_assets");
        }
    }
}
