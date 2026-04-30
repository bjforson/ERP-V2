using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickFinance.AP.Migrations
{
    /// <inheritdoc />
    public partial class Initial_Ap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ap");

            migrationBuilder.CreateTable(
                name: "vendors",
                schema: "ap",
                columns: table => new
                {
                    vendor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tin = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_vat_registered = table.Column<bool>(type: "boolean", nullable: false),
                    default_wht = table.Column<short>(type: "smallint", nullable: false),
                    wht_exempt = table.Column<bool>(type: "boolean", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    momo_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    momo_network = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    bank_account_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    bank_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    default_expense_account = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vendors", x => x.vendor_id);
                });

            migrationBuilder.CreateTable(
                name: "wht_certificates",
                schema: "ap",
                columns: table => new
                {
                    wht_certificate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    certificate_no = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    vendor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    gross_paid_minor = table.Column<long>(type: "bigint", nullable: false),
                    wht_deducted_minor = table.Column<long>(type: "bigint", nullable: false),
                    wht_rate = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false),
                    transaction_type = table.Column<short>(type: "smallint", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wht_certificates", x => x.wht_certificate_id);
                });

            migrationBuilder.CreateTable(
                name: "bills",
                schema: "ap",
                columns: table => new
                {
                    bill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_no = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    vendor_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    vendor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_date = table.Column<DateOnly>(type: "date", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    po_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    grn_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    subtotal_net_minor = table.Column<long>(type: "bigint", nullable: false),
                    levies_minor = table.Column<long>(type: "bigint", nullable: false),
                    vat_minor = table.Column<long>(type: "bigint", nullable: false),
                    gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    wht_rate_bp = table.Column<long>(type: "bigint", nullable: false),
                    wht_minor = table.Column<long>(type: "bigint", nullable: false),
                    paid_minor = table.Column<long>(type: "bigint", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ledger_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bills", x => x.bill_id);
                    table.ForeignKey(
                        name: "FK_bills_vendors_vendor_id",
                        column: x => x.vendor_id,
                        principalSchema: "ap",
                        principalTable: "vendors",
                        principalColumn: "vendor_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bill_lines",
                schema: "ap",
                columns: table => new
                {
                    bill_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<short>(type: "smallint", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    net_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    expense_account = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bill_lines", x => x.bill_line_id);
                    table.ForeignKey(
                        name: "FK_bill_lines_bills_bill_id",
                        column: x => x.bill_id,
                        principalSchema: "ap",
                        principalTable: "bills",
                        principalColumn: "bill_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                schema: "ap",
                columns: table => new
                {
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    payment_rail = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    cash_account = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    rail_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    payment_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ledger_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payments", x => x.payment_id);
                    table.ForeignKey(
                        name: "FK_payments_bills_bill_id",
                        column: x => x.bill_id,
                        principalSchema: "ap",
                        principalTable: "bills",
                        principalColumn: "bill_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_bill_lines_bill_line",
                schema: "ap",
                table: "bill_lines",
                columns: new[] { "bill_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bills_tenant_vendor_status",
                schema: "ap",
                table: "bills",
                columns: new[] { "tenant_id", "vendor_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_bills_vendor_id",
                schema: "ap",
                table: "bills",
                column: "vendor_id");

            migrationBuilder.CreateIndex(
                name: "ux_bills_tenant_billno",
                schema: "ap",
                table: "bills",
                columns: new[] { "tenant_id", "bill_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_bill_id",
                schema: "ap",
                table: "payments",
                column: "bill_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_tenant_run",
                schema: "ap",
                table: "payments",
                columns: new[] { "tenant_id", "payment_run_id" });

            migrationBuilder.CreateIndex(
                name: "ux_vendors_tenant_code",
                schema: "ap",
                table: "vendors",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_wht_certs_vendor_date",
                schema: "ap",
                table: "wht_certificates",
                columns: new[] { "tenant_id", "vendor_id", "issue_date" });

            migrationBuilder.CreateIndex(
                name: "ux_wht_certs_tenant_no",
                schema: "ap",
                table: "wht_certificates",
                columns: new[] { "tenant_id", "certificate_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bill_lines",
                schema: "ap");

            migrationBuilder.DropTable(
                name: "payments",
                schema: "ap");

            migrationBuilder.DropTable(
                name: "wht_certificates",
                schema: "ap");

            migrationBuilder.DropTable(
                name: "bills",
                schema: "ap");

            migrationBuilder.DropTable(
                name: "vendors",
                schema: "ap");
        }
    }
}
