using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Ledger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforceLedgerEntryDirection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_ledger_entries_direction",
                schema: "ledger",
                table: "entries",
                sql: "\"direction\" IN ('Debit', 'Credit')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ledger_entries_direction",
                schema: "ledger",
                table: "entries");
        }
    }
}
