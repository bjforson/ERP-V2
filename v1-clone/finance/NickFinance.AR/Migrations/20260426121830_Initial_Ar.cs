using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickFinance.AR.Migrations
{
    /// <inheritdoc />
    public partial class Initial_Ar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ar");

            migrationBuilder.CreateTable(
                name: "customers",
                schema: "ar",
                columns: table => new
                {
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tin = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_vat_registered = table.Column<bool>(type: "boolean", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ar_control_account = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customers", x => x.customer_id);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                schema: "ar",
                columns: table => new
                {
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_no = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    invoice_date = table.Column<DateOnly>(type: "date", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    evat_irn = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    evat_issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    subtotal_net_minor = table.Column<long>(type: "bigint", nullable: false),
                    levies_minor = table.Column<long>(type: "bigint", nullable: false),
                    vat_minor = table.Column<long>(type: "bigint", nullable: false),
                    gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    paid_minor = table.Column<long>(type: "bigint", nullable: false),
                    ledger_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_module = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_entity_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    void_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoices", x => x.invoice_id);
                    table.ForeignKey(
                        name: "FK_invoices_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "ar",
                        principalTable: "customers",
                        principalColumn: "customer_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_lines",
                schema: "ar",
                columns: table => new
                {
                    invoice_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<short>(type: "smallint", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    net_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    revenue_account = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_lines", x => x.invoice_line_id);
                    table.ForeignKey(
                        name: "FK_invoice_lines_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "ar",
                        principalTable: "invoices",
                        principalColumn: "invoice_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "receipts",
                schema: "ar",
                columns: table => new
                {
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    cash_account = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ledger_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipts", x => x.receipt_id);
                    table.ForeignKey(
                        name: "FK_receipts_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "ar",
                        principalTable: "invoices",
                        principalColumn: "invoice_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_customers_tenant_code",
                schema: "ar",
                table: "customers",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_invoice_lines_invoice_line",
                schema: "ar",
                table: "invoice_lines",
                columns: new[] { "invoice_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_customer_id",
                schema: "ar",
                table: "invoices",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_customer_status",
                schema: "ar",
                table: "invoices",
                columns: new[] { "tenant_id", "customer_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_invoices_tenant_invoiceno",
                schema: "ar",
                table: "invoices",
                columns: new[] { "tenant_id", "invoice_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receipts_invoice_id",
                schema: "ar",
                table: "receipts",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_receipts_tenant_invoice_date",
                schema: "ar",
                table: "receipts",
                columns: new[] { "tenant_id", "invoice_id", "receipt_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invoice_lines",
                schema: "ar");

            migrationBuilder.DropTable(
                name: "receipts",
                schema: "ar");

            migrationBuilder.DropTable(
                name: "invoices",
                schema: "ar");

            migrationBuilder.DropTable(
                name: "customers",
                schema: "ar");
        }
    }
}
