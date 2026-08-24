using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentProvisionIdempotencyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                schema: "payments",
                table: "payment_provisions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // Existing rows predate the key and would all share the empty
            // default, so the unique index below would fail for any client with
            // more than one provision. Each row is given its own id, which is
            // unique by construction and is the truthful answer to "what did the
            // till call this attempt": nothing, because it could not yet.
            migrationBuilder.Sql(
                """
                select set_config('app.is_platform_operator', 'true', true);

                UPDATE payments.payment_provisions
                SET idempotency_key = id::text
                WHERE idempotency_key = '';

                select set_config('app.is_platform_operator', 'false', true);
                """);

            migrationBuilder.CreateIndex(
                name: "ux_payment_provisions_client_idempotency",
                schema: "payments",
                table: "payment_provisions",
                columns: new[] { "pos_client_id", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_payment_provisions_client_idempotency",
                schema: "payments",
                table: "payment_provisions");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                schema: "payments",
                table: "payment_provisions");
        }
    }
}
