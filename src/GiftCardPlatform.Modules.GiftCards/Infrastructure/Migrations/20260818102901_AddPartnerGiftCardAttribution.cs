using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.GiftCards.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// A partner mints with a machine credential and has no organization
    /// membership, so issuer attribution becomes one of two shapes: a membership
    /// for a person, or a partner API client (ADR-053). A check constraint keeps
    /// it to exactly one, so no minted card can end up with no traceable issuer.
    ///
    /// Every existing card was issued by a person and already satisfies it.
    ///
    /// The down path makes the membership column non-nullable again with a zero
    /// default, which is EF's standard shape but would rewrite the membership of
    /// any partner-minted card. Reverting is therefore only safe while no
    /// partner card exists; after that, delete or reassign them first.
    /// </summary>
    public partial class AddPartnerGiftCardAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "issued_by_membership_id",
                schema: "gift_cards",
                table: "gift_cards",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "issued_by_partner_client_id",
                schema: "gift_cards",
                table: "gift_cards",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_gift_cards_issuer_attribution",
                schema: "gift_cards",
                table: "gift_cards",
                sql: "(\"issued_by_membership_id\" IS NOT NULL\r\n    AND \"issued_by_partner_client_id\" IS NULL)\r\nOR\r\n(\"issued_by_membership_id\" IS NULL\r\n    AND \"issued_by_partner_client_id\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_gift_cards_issuer_attribution",
                schema: "gift_cards",
                table: "gift_cards");

            migrationBuilder.DropColumn(
                name: "issued_by_partner_client_id",
                schema: "gift_cards",
                table: "gift_cards");

            migrationBuilder.AlterColumn<Guid>(
                name: "issued_by_membership_id",
                schema: "gift_cards",
                table: "gift_cards",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
