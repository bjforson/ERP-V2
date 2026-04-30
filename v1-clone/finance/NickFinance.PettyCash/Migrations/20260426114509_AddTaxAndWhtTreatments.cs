using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickFinance.PettyCash.Migrations
{
    /// <inheritdoc />
    public partial class AddTaxAndWhtTreatments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "tax_treatment",
                schema: "petty_cash",
                table: "vouchers",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "wht_treatment",
                schema: "petty_cash",
                table: "vouchers",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "tax_treatment",
                schema: "petty_cash",
                table: "vouchers");

            migrationBuilder.DropColumn(
                name: "wht_treatment",
                schema: "petty_cash",
                table: "vouchers");
        }
    }
}
