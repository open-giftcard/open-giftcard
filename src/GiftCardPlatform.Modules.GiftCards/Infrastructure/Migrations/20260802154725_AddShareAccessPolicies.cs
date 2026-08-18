using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.GiftCards.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShareAccessPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create function gift_cards.caller_share_allows_card(target_gift_card_id uuid)
                returns boolean
                language plpgsql
                stable
                security invoker
                set search_path = pg_catalog, sharing
                as $$
                begin
                    return exists (
                        select 1
                        from sharing.shares share
                        where share.id = nullif(current_setting('app.share_id', true), '')::uuid
                          and share.state in ('Pending', 'Claiming', 'Claimed')
                          and (share.source_gift_card_id = target_gift_card_id
                               or share.child_gift_card_id = target_gift_card_id)
                    );
                exception
                    when undefined_table then return false;
                end;
                $$;

                create policy gift_cards_share_candidate_read
                    on gift_cards.gift_cards
                    for select
                    using (gift_cards.caller_share_allows_card(id));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop policy if exists gift_cards_share_candidate_read
                    on gift_cards.gift_cards;
                drop function if exists gift_cards.caller_share_allows_card(uuid);
                """);
        }
    }
}
