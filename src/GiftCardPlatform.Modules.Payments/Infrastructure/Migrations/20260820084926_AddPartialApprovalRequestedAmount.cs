using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPartialApprovalRequestedAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "requested_amount",
                schema: "payments",
                table: "payment_provisions",
                type: "numeric(20,4)",
                nullable: false,
                defaultValue: 0m);

            // Every provision that already exists was taken under the rule that
            // a hold could never exceed available value, so each one was a full
            // approval and requested exactly what it held. Left at the column
            // default of zero they would each report an outstanding amount of
            // minus their own value.
            migrationBuilder.Sql(
                """
                UPDATE payments.payment_provisions
                SET requested_amount = amount
                WHERE requested_amount = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "requested_amount",
                schema: "payments",
                table: "payment_provisions");
        }
    }
}
