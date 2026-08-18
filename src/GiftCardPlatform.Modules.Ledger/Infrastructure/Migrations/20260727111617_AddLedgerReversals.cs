using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Ledger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLedgerReversals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "reverses_transaction_id",
                schema: "ledger",
                table: "transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_ledger_transactions_reversal",
                schema: "ledger",
                table: "transactions",
                column: "reverses_transaction_id",
                unique: true,
                filter: "\"reverses_transaction_id\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_transactions_transactions_reverses_transaction_id",
                schema: "ledger",
                table: "transactions",
                column: "reverses_transaction_id",
                principalSchema: "ledger",
                principalTable: "transactions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_transactions_transactions_reverses_transaction_id",
                schema: "ledger",
                table: "transactions");

            migrationBuilder.DropIndex(
                name: "ux_ledger_transactions_reversal",
                schema: "ledger",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "reverses_transaction_id",
                schema: "ledger",
                table: "transactions");
        }
    }
}
