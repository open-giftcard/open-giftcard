using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.GiftCards.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentCredentialReadPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                -- A till holds no user, membership, organization, or tenant scope,
                -- so tenant RLS correctly hides every card from it. Presenting a
                -- payment credential must therefore admit exactly one card: the
                -- one that credential was issued against, and nothing else.
                --
                -- SELECT only. A POS client never mutates card state; reserving
                -- value is a Payments concern and posting it is the Ledger's. The
                -- credential's 256-bit secret is still verified in constant time
                -- before value moves, so admitting the row grants visibility,
                -- never authority.
                -- plpgsql, and tolerant of a missing table, for the same reason
                -- the share helper is: module migrations run in a fixed order and
                -- Payments migrates after Gift Cards, so the referenced table does
                -- not exist yet when this runs. plpgsql resolves the reference at
                -- execution rather than creation.
                create function gift_cards.caller_payment_credential_allows_card(
                    target_gift_card_id uuid)
                returns boolean
                language plpgsql
                stable
                security invoker
                set search_path = pg_catalog, payments
                as $$
                begin
                    return exists (
                        select 1
                        from payments.payment_tokens token
                        where token.id = nullif(current_setting('app.payment_token_id', true), '')::uuid
                          and token.gift_card_id = target_gift_card_id
                    );
                exception
                    when undefined_table then return false;
                end;
                $$;

                create policy gift_cards_payment_candidate_read on gift_cards.gift_cards
                    for select
                    using (gift_cards.caller_payment_credential_allows_card(id));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop policy if exists gift_cards_payment_candidate_read on gift_cards.gift_cards;
                drop function if exists gift_cards.caller_payment_credential_allows_card(uuid);
                """);
        }
    }
}
