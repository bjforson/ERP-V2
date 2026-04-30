using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickFinance.PettyCash.Migrations
{
    /// <inheritdoc />
    public partial class AddCashCountsAndBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "budgets",
                schema: "petty_cash",
                columns: table => new
                {
                    budget_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<short>(type: "smallint", nullable: false),
                    scope_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    consumed_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    alert_threshold_pct = table.Column<byte>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budgets", x => x.budget_id);
                });

            migrationBuilder.CreateTable(
                name: "cash_counts",
                schema: "petty_cash",
                columns: table => new
                {
                    cash_count_id = table.Column<Guid>(type: "uuid", nullable: false),
                    float_id = table.Column<Guid>(type: "uuid", nullable: false),
                    counted_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    witness_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    counted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    physical_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    system_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    variance_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cash_counts", x => x.cash_count_id);
                    table.ForeignKey(
                        name: "FK_cash_counts_floats_float_id",
                        column: x => x.float_id,
                        principalSchema: "petty_cash",
                        principalTable: "floats",
                        principalColumn: "float_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_budgets_tenant_scope_period",
                schema: "petty_cash",
                table: "budgets",
                columns: new[] { "tenant_id", "scope", "scope_key", "period_start", "period_end" });

            migrationBuilder.CreateIndex(
                name: "ix_cash_counts_float_when",
                schema: "petty_cash",
                table: "cash_counts",
                columns: new[] { "float_id", "counted_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "budgets",
                schema: "petty_cash");

            migrationBuilder.DropTable(
                name: "cash_counts",
                schema: "petty_cash");
        }
    }
}
