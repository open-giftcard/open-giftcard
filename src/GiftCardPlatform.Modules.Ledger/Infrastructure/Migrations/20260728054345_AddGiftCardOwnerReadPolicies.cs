using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Ledger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGiftCardOwnerReadPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create function ledger.caller_owns_gift_card(
                    target_gift_card_id uuid)
                returns boolean
                language plpgsql
                stable
                security invoker
                set search_path = pg_catalog, gift_cards
                as $$
                begin
                    return exists (
                        select 1
                        from gift_cards.gift_cards card
                        where card.id = target_gift_card_id
                          and card.ownership_state = 'IdentityOwned'
                          and card.owner_user_id =
                              nullif(
                                  current_setting('app.user_id', true),
                                  '')::uuid
                    );
                end
                $$;

                create policy ledger_accounts_identity_owner_read
                    on ledger.accounts
                    for select
                    using (
                        gift_card_id is not null
                        and ledger.caller_owns_gift_card(gift_card_id)
                    );

                create policy ledger_entries_identity_owner_read
                    on ledger.entries
                    for select
                    using (
                        exists (
                            select 1
                            from ledger.accounts account
                            where account.id = ledger.entries.account_id
                              and account.gift_card_id is not null
                        )
                    );

                create policy ledger_transactions_identity_owner_read
                    on ledger.transactions
                    for select
                    using (
                        exists (
                            select 1
                            from ledger.entries entry
                            where entry.transaction_id = ledger.transactions.id
                        )
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop policy if exists ledger_transactions_identity_owner_read
                    on ledger.transactions;
                drop policy if exists ledger_entries_identity_owner_read
                    on ledger.entries;
                drop policy if exists ledger_accounts_identity_owner_read
                    on ledger.accounts;
                drop function if exists ledger.caller_owns_gift_card(uuid);
                """);
        }
    }
}
