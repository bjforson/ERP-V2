using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickFinance.PettyCash.Migrations
{
    /// <inheritdoc />
    public partial class AddDelegationsAndApprovalIndexShape : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_voucher_approvals_voucher_step",
                schema: "petty_cash",
                table: "voucher_approvals");

            migrationBuilder.CreateTable(
                name: "approval_delegations",
                schema: "petty_cash",
                columns: table => new
                {
                    approval_delegation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delegate_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    valid_from_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_delegations", x => x.approval_delegation_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_voucher_approvals_voucher_step",
                schema: "petty_cash",
                table: "voucher_approvals",
                columns: new[] { "voucher_id", "step_no" });

            migrationBuilder.CreateIndex(
                name: "ux_voucher_approvals_voucher_step_assignee",
                schema: "petty_cash",
                table: "voucher_approvals",
                columns: new[] { "voucher_id", "step_no", "assigned_to_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_approval_delegations_user_window",
                schema: "petty_cash",
                table: "approval_delegations",
                columns: new[] { "tenant_id", "user_id", "valid_from_utc", "valid_until_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approval_delegations",
                schema: "petty_cash");

            migrationBuilder.DropIndex(
                name: "ix_voucher_approvals_voucher_step",
                schema: "petty_cash",
                table: "voucher_approvals");

            migrationBuilder.DropIndex(
                name: "ux_voucher_approvals_voucher_step_assignee",
                schema: "petty_cash",
                table: "voucher_approvals");

            migrationBuilder.CreateIndex(
                name: "ux_voucher_approvals_voucher_step",
                schema: "petty_cash",
                table: "voucher_approvals",
                columns: new[] { "voucher_id", "step_no" },
                unique: true);
        }
    }
}
