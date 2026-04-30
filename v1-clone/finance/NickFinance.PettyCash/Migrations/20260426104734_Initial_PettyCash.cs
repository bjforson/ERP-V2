using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickFinance.PettyCash.Migrations
{
    /// <inheritdoc />
    public partial class Initial_PettyCash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "petty_cash");

            migrationBuilder.CreateTable(
                name: "floats",
                schema: "petty_cash",
                columns: table => new
                {
                    float_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    custodian_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    float_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    replenish_threshold_pct = table.Column<short>(type: "smallint", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_floats", x => x.float_id);
                });

            migrationBuilder.CreateTable(
                name: "vouchers",
                schema: "petty_cash",
                columns: table => new
                {
                    voucher_id = table.Column<Guid>(type: "uuid", nullable: false),
                    voucher_no = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    float_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<short>(type: "smallint", nullable: false),
                    purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    amount_requested_minor = table.Column<long>(type: "bigint", nullable: false),
                    amount_approved_minor = table.Column<long>(type: "bigint", nullable: true),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    payee_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    project_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disbursed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decision_comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    disbursed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ledger_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vouchers", x => x.voucher_id);
                    table.ForeignKey(
                        name: "FK_vouchers_floats_float_id",
                        column: x => x.float_id,
                        principalSchema: "petty_cash",
                        principalTable: "floats",
                        principalColumn: "float_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "voucher_approvals",
                schema: "petty_cash",
                columns: table => new
                {
                    voucher_approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    voucher_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_no = table.Column<short>(type: "smallint", nullable: false),
                    role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    assigned_to_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<short>(type: "smallint", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_voucher_approvals", x => x.voucher_approval_id);
                    table.ForeignKey(
                        name: "FK_voucher_approvals_vouchers_voucher_id",
                        column: x => x.voucher_id,
                        principalSchema: "petty_cash",
                        principalTable: "vouchers",
                        principalColumn: "voucher_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "voucher_line_items",
                schema: "petty_cash",
                columns: table => new
                {
                    voucher_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    voucher_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<short>(type: "smallint", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    gross_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    gl_account = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_voucher_line_items", x => x.voucher_line_id);
                    table.ForeignKey(
                        name: "FK_voucher_line_items_vouchers_voucher_id",
                        column: x => x.voucher_id,
                        principalSchema: "petty_cash",
                        principalTable: "vouchers",
                        principalColumn: "voucher_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_floats_active_per_site_currency",
                schema: "petty_cash",
                table: "floats",
                columns: new[] { "tenant_id", "site_id", "currency_code", "is_active" },
                unique: true,
                filter: "\"is_active\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "ix_voucher_approvals_assignee_decision",
                schema: "petty_cash",
                table: "voucher_approvals",
                columns: new[] { "tenant_id", "assigned_to_user_id", "decision" });

            migrationBuilder.CreateIndex(
                name: "ux_voucher_approvals_voucher_step",
                schema: "petty_cash",
                table: "voucher_approvals",
                columns: new[] { "voucher_id", "step_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_voucher_lines_voucher_lineno",
                schema: "petty_cash",
                table: "voucher_line_items",
                columns: new[] { "voucher_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vouchers_float_id",
                schema: "petty_cash",
                table: "vouchers",
                column: "float_id");

            migrationBuilder.CreateIndex(
                name: "ix_vouchers_tenant_float_status",
                schema: "petty_cash",
                table: "vouchers",
                columns: new[] { "tenant_id", "float_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_vouchers_tenant_requester_status",
                schema: "petty_cash",
                table: "vouchers",
                columns: new[] { "tenant_id", "requester_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_vouchers_tenant_voucher_no",
                schema: "petty_cash",
                table: "vouchers",
                columns: new[] { "tenant_id", "voucher_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "voucher_approvals",
                schema: "petty_cash");

            migrationBuilder.DropTable(
                name: "voucher_line_items",
                schema: "petty_cash");

            migrationBuilder.DropTable(
                name: "vouchers",
                schema: "petty_cash");

            migrationBuilder.DropTable(
                name: "floats",
                schema: "petty_cash");
        }
    }
}
