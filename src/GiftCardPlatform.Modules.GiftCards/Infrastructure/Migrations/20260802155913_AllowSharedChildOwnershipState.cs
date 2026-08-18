using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.GiftCards.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AllowSharedChildOwnershipState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_gift_cards_distribution_state",
                schema: "gift_cards",
                table: "gift_cards");

            migrationBuilder.AddCheckConstraint(
                name: "ck_gift_cards_distribution_state",
                schema: "gift_cards",
                table: "gift_cards",
                sql: "(\"ownership_state\" = 'OrganizationInventory'\r\n    AND \"lifecycle_state\" IN (\r\n        'Active', 'Suspended', 'Cancelled', 'Expired')\r\n    AND \"distribution_invitation_id\" IS NULL\r\n    AND \"distributed_at_utc\" IS NULL\r\n    AND \"claimed_at_utc\" IS NULL)\r\nOR\r\n(\"ownership_state\" = 'AwaitingClaim'\r\n    AND \"lifecycle_state\" IN (\r\n        'AwaitingClaim', 'Suspended', 'Cancelled', 'Expired')\r\n    AND \"distribution_invitation_id\" IS NOT NULL\r\n    AND \"distributed_at_utc\" IS NOT NULL\r\n    AND \"claimed_at_utc\" IS NULL)\r\nOR\r\n(\"ownership_state\" = 'IdentityOwned'\n    AND \"lifecycle_state\" IN (\n        'Active', 'Suspended', 'Cancelled', 'Expired')\n    AND \"claimed_at_utc\" IS NOT NULL\n    AND (\n        (\"generation\" = 0\n            AND \"distribution_invitation_id\" IS NOT NULL\n            AND \"distributed_at_utc\" IS NOT NULL)\n        OR\n        (\"generation\" > 0\n            AND \"source_gift_card_id\" IS NOT NULL\n            AND \"distribution_invitation_id\" IS NULL\n            AND \"distributed_at_utc\" IS NULL)))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_gift_cards_distribution_state",
                schema: "gift_cards",
                table: "gift_cards");

            migrationBuilder.AddCheckConstraint(
                name: "ck_gift_cards_distribution_state",
                schema: "gift_cards",
                table: "gift_cards",
                sql: "(\"ownership_state\" = 'OrganizationInventory'\r\n    AND \"lifecycle_state\" IN (\r\n        'Active', 'Suspended', 'Cancelled', 'Expired')\r\n    AND \"distribution_invitation_id\" IS NULL\r\n    AND \"distributed_at_utc\" IS NULL\r\n    AND \"claimed_at_utc\" IS NULL)\r\nOR\r\n(\"ownership_state\" = 'AwaitingClaim'\r\n    AND \"lifecycle_state\" IN (\r\n        'AwaitingClaim', 'Suspended', 'Cancelled', 'Expired')\r\n    AND \"distribution_invitation_id\" IS NOT NULL\r\n    AND \"distributed_at_utc\" IS NOT NULL\r\n    AND \"claimed_at_utc\" IS NULL)\r\nOR\r\n(\"ownership_state\" = 'IdentityOwned'\r\n    AND \"lifecycle_state\" IN (\r\n        'Active', 'Suspended', 'Cancelled', 'Expired')\r\n    AND \"distribution_invitation_id\" IS NOT NULL\r\n    AND \"distributed_at_utc\" IS NOT NULL\r\n    AND \"claimed_at_utc\" IS NOT NULL)");
        }
    }
}
