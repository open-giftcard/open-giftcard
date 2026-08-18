using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Sharing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentCredentialReservationRead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                """
                -- Available value is posted balance minus every active hold, and a
                -- till has to subtract share reservations before it may reserve
                -- anything (DOMAIN_RULES 10.20). Reserved value is summed, so a
                -- policy that hides a reservation does not fail closed: it returns
                -- a smaller number and lets a payment and a share promise the same
                -- money. The rows must therefore be visible for exactly the card
                -- the presented credential is bound to.
                --
                -- SELECT only, and only shares against that one card. A POS client
                -- never sees a token, PIN, contact, or any share on another card,
                -- and can never write here.
                --
                -- plpgsql and tolerant of a missing table for the same reason the
                -- Gift Cards and Ledger payment helpers are: Payments migrates
                -- after Sharing, so the referenced table does not exist yet when
                -- this runs.
                create function sharing.caller_payment_credential_allows_card(
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

                create policy sharing_shares_payment_candidate_read on sharing.shares
                    for select
                    using (sharing.caller_payment_credential_allows_card(source_gift_card_id));
                """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                """
                drop policy if exists sharing_shares_payment_candidate_read on sharing.shares;
                drop function if exists sharing.caller_payment_credential_allows_card(uuid);
                """);
    }
}
