using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickFinance.PettyCash.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurringVouchers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recurring_voucher_templates",
                schema: "petty_cash",
                columns: table => new
                {
                    recurring_voucher_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    float_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<short>(type: "smallint", nullable: false),
                    purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    frequency = table.Column<short>(type: "smallint", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    last_fired_on = table.Column<DateOnly>(type: "date", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    payee_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    project_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recurring_voucher_templates", x => x.recurring_voucher_template_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_recurring_tenant_active_freq",
                schema: "petty_cash",
                table: "recurring_voucher_templates",
                columns: new[] { "tenant_id", "is_active", "frequency" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recurring_voucher_templates",
                schema: "petty_cash");
        }
    }
}
