using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickFinance.PettyCash.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptsAndDisbursementChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "disbursement_channel",
                schema: "petty_cash",
                table: "vouchers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "disbursement_reference",
                schema: "petty_cash",
                table: "vouchers",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payee_momo_network",
                schema: "petty_cash",
                table: "vouchers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payee_momo_number",
                schema: "petty_cash",
                table: "vouchers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "voucher_receipts",
                schema: "petty_cash",
                columns: table => new
                {
                    voucher_receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    voucher_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<short>(type: "smallint", nullable: false),
                    file_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    approximate_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    ocr_vendor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ocr_amount_minor = table.Column<long>(type: "bigint", nullable: true),
                    ocr_date = table.Column<DateOnly>(type: "date", nullable: true),
                    ocr_raw_text = table.Column<string>(type: "text", nullable: true),
                    ocr_confidence = table.Column<byte>(type: "smallint", nullable: true),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    gps_latitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    gps_longitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_voucher_receipts", x => x.voucher_receipt_id);
                    table.ForeignKey(
                        name: "FK_voucher_receipts_vouchers_voucher_id",
                        column: x => x.voucher_id,
                        principalSchema: "petty_cash",
                        principalTable: "vouchers",
                        principalColumn: "voucher_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_voucher_receipts_tenant_approx",
                schema: "petty_cash",
                table: "voucher_receipts",
                columns: new[] { "tenant_id", "approximate_hash" });

            migrationBuilder.CreateIndex(
                name: "ix_voucher_receipts_tenant_sha",
                schema: "petty_cash",
                table: "voucher_receipts",
                columns: new[] { "tenant_id", "sha256" });

            migrationBuilder.CreateIndex(
                name: "ux_voucher_receipts_voucher_ordinal",
                schema: "petty_cash",
                table: "voucher_receipts",
                columns: new[] { "voucher_id", "ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "voucher_receipts",
                schema: "petty_cash");

            migrationBuilder.DropColumn(
                name: "disbursement_channel",
                schema: "petty_cash",
                table: "vouchers");

            migrationBuilder.DropColumn(
                name: "disbursement_reference",
                schema: "petty_cash",
                table: "vouchers");

            migrationBuilder.DropColumn(
                name: "payee_momo_network",
                schema: "petty_cash",
                table: "vouchers");

            migrationBuilder.DropColumn(
                name: "payee_momo_number",
                schema: "petty_cash",
                table: "vouchers");
        }
    }
}
