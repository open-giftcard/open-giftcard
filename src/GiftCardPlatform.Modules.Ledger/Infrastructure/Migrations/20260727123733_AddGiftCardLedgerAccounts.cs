using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Ledger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGiftCardLedgerAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_ledger_organization_account",
                schema: "ledger",
                table: "accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ledger_accounts_scope",
                schema: "ledger",
                table: "accounts");

            migrationBuilder.AddColumn<Guid>(
                name: "gift_card_id",
                schema: "ledger",
                table: "accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_ledger_gift_card_account",
                schema: "ledger",
                table: "accounts",
                column: "gift_card_id",
                unique: true,
                filter: "\"gift_card_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_ledger_organization_account",
                schema: "ledger",
                table: "accounts",
                columns: new[] { "organization_id", "type", "currency" },
                unique: true,
                filter: "\"type\" = 'OrganizationCorporateCredit'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ledger_accounts_scope",
                schema: "ledger",
                table: "accounts",
                sql: "(\"type\" = 'PlatformFunding'\n    AND \"organization_id\" IS NULL\n    AND \"gift_card_id\" IS NULL)\nOR\n(\"type\" = 'OrganizationCorporateCredit'\n    AND \"organization_id\" IS NOT NULL\n    AND \"gift_card_id\" IS NULL)\nOR\n(\"type\" = 'GiftCardValue'\n    AND \"organization_id\" IS NOT NULL\n    AND \"gift_card_id\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_ledger_gift_card_account",
                schema: "ledger",
                table: "accounts");

            migrationBuilder.DropIndex(
                name: "ux_ledger_organization_account",
                schema: "ledger",
                table: "accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ledger_accounts_scope",
                schema: "ledger",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "gift_card_id",
                schema: "ledger",
                table: "accounts");

            migrationBuilder.CreateIndex(
                name: "ux_ledger_organization_account",
                schema: "ledger",
                table: "accounts",
                columns: new[] { "organization_id", "type", "currency" },
                unique: true,
                filter: "\"organization_id\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ledger_accounts_scope",
                schema: "ledger",
                table: "accounts",
                sql: "(\"type\" = 'PlatformFunding' AND \"organization_id\" IS NULL)\nOR\n(\"type\" = 'OrganizationCorporateCredit' AND \"organization_id\" IS NOT NULL)");
        }
    }
}
