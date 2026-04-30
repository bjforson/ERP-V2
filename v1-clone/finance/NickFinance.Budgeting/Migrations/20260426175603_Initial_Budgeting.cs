using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickFinance.Budgeting.Migrations
{
    /// <inheritdoc />
    public partial class Initial_Budgeting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "budgeting");

            migrationBuilder.CreateTable(
                name: "annual_budgets",
                schema: "budgeting",
                columns: table => new
                {
                    annual_budget_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fiscal_year = table.Column<int>(type: "integer", nullable: false),
                    department_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_annual_budgets", x => x.annual_budget_id);
                });

            migrationBuilder.CreateTable(
                name: "budget_lines",
                schema: "budgeting",
                columns: table => new
                {
                    budget_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    annual_budget_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    month_number = table.Column<byte>(type: "smallint", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_lines", x => x.budget_line_id);
                    table.ForeignKey(
                        name: "FK_budget_lines_annual_budgets_annual_budget_id",
                        column: x => x.annual_budget_id,
                        principalSchema: "budgeting",
                        principalTable: "annual_budgets",
                        principalColumn: "annual_budget_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_annual_budgets_tenant_year_dept",
                schema: "budgeting",
                table: "annual_budgets",
                columns: new[] { "tenant_id", "fiscal_year", "department_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_budget_lines_budget_account_month",
                schema: "budgeting",
                table: "budget_lines",
                columns: new[] { "annual_budget_id", "account_code", "month_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "budget_lines",
                schema: "budgeting");

            migrationBuilder.DropTable(
                name: "annual_budgets",
                schema: "budgeting");
        }
    }
}
