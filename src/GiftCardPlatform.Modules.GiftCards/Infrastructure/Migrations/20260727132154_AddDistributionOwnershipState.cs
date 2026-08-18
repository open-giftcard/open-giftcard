using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.GiftCards.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDistributionOwnershipState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "claimed_at_utc",
                schema: "gift_cards",
                table: "gift_cards",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "distributed_at_utc",
                schema: "gift_cards",
                table: "gift_cards",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "distribution_invitation_id",
                schema: "gift_cards",
                table: "gift_cards",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_gift_cards_distribution_invitation",
                schema: "gift_cards",
                table: "gift_cards",
                column: "distribution_invitation_id",
                unique: true,
                filter: "\"distribution_invitation_id\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_gift_cards_distribution_state",
                schema: "gift_cards",
                table: "gift_cards",
                sql: "(\"ownership_state\" = 'OrganizationInventory'\n    AND \"distribution_invitation_id\" IS NULL\n    AND \"distributed_at_utc\" IS NULL\n    AND \"claimed_at_utc\" IS NULL)\nOR\n(\"ownership_state\" = 'AwaitingClaim'\n    AND \"lifecycle_state\" = 'AwaitingClaim'\n    AND \"distribution_invitation_id\" IS NOT NULL\n    AND \"distributed_at_utc\" IS NOT NULL\n    AND \"claimed_at_utc\" IS NULL)\nOR\n(\"ownership_state\" = 'IdentityOwned'\n    AND \"distribution_invitation_id\" IS NOT NULL\n    AND \"distributed_at_utc\" IS NOT NULL\n    AND \"claimed_at_utc\" IS NOT NULL)");

            migrationBuilder.Sql(
                """
                drop policy if exists gift_cards_tenant_and_owner_isolation
                    on gift_cards.gift_cards;

                create policy gift_cards_tenant_owner_and_claim_isolation
                    on gift_cards.gift_cards
                    using (
                        coalesce(
                            nullif(
                                current_setting('app.is_platform_operator', true),
                                ''),
                            'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(
                            funding_organization_id)
                        or (
                            owner_user_id is not null
                            and owner_user_id =
                                nullif(
                                    current_setting('app.user_id', true),
                                    '')::uuid
                        )
                        or (
                            distribution_invitation_id is not null
                            and distribution_invitation_id =
                                nullif(
                                    current_setting(
                                        'app.claim_invitation_id',
                                        true),
                                    '')::uuid
                        )
                    )
                    with check (
                        coalesce(
                            nullif(
                                current_setting('app.is_platform_operator', true),
                                ''),
                            'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(
                            funding_organization_id)
                        or (
                            owner_user_id is not null
                            and owner_user_id =
                                nullif(
                                    current_setting('app.user_id', true),
                                    '')::uuid
                        )
                        or (
                            distribution_invitation_id is not null
                            and distribution_invitation_id =
                                nullif(
                                    current_setting(
                                        'app.claim_invitation_id',
                                        true),
                                    '')::uuid
                        )
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop policy if exists gift_cards_tenant_owner_and_claim_isolation
                    on gift_cards.gift_cards;

                create policy gift_cards_tenant_and_owner_isolation
                    on gift_cards.gift_cards
                    using (
                        coalesce(
                            nullif(
                                current_setting('app.is_platform_operator', true),
                                ''),
                            'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(
                            funding_organization_id)
                        or (
                            owner_user_id is not null
                            and owner_user_id =
                                nullif(
                                    current_setting('app.user_id', true),
                                    '')::uuid
                        )
                    )
                    with check (
                        coalesce(
                            nullif(
                                current_setting('app.is_platform_operator', true),
                                ''),
                            'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(
                            funding_organization_id)
                        or (
                            owner_user_id is not null
                            and owner_user_id =
                                nullif(
                                    current_setting('app.user_id', true),
                                    '')::uuid
                        )
                    );
                """);

            migrationBuilder.DropIndex(
                name: "ux_gift_cards_distribution_invitation",
                schema: "gift_cards",
                table: "gift_cards");

            migrationBuilder.DropCheckConstraint(
                name: "ck_gift_cards_distribution_state",
                schema: "gift_cards",
                table: "gift_cards");

            migrationBuilder.DropColumn(
                name: "claimed_at_utc",
                schema: "gift_cards",
                table: "gift_cards");

            migrationBuilder.DropColumn(
                name: "distributed_at_utc",
                schema: "gift_cards",
                table: "gift_cards");

            migrationBuilder.DropColumn(
                name: "distribution_invitation_id",
                schema: "gift_cards",
                table: "gift_cards");
        }
    }
}
