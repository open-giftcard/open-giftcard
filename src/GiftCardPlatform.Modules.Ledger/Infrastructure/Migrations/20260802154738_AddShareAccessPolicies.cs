using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Ledger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShareAccessPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create function ledger.caller_share_allows_gift_card(target_gift_card_id uuid)
                returns boolean
                language plpgsql
                stable
                security invoker
                set search_path = pg_catalog, sharing
                as $$
                begin
                    return exists (
                        select 1 from sharing.shares share
                        where share.id = nullif(current_setting('app.share_id', true), '')::uuid
                          and share.state in ('Pending', 'Claiming', 'Claimed')
                          and (share.source_gift_card_id = target_gift_card_id
                               or share.child_gift_card_id = target_gift_card_id)
                    );
                exception
                    when undefined_table then return false;
                end;
                $$;

                create function ledger.caller_share_allows_transaction(target_transaction_id uuid)
                returns boolean
                language plpgsql
                stable
                security invoker
                set search_path = pg_catalog, sharing
                as $$
                begin
                    return exists (
                        select 1 from sharing.shares share
                        where share.id = nullif(current_setting('app.share_id', true), '')::uuid
                          and share.state in ('Claiming', 'Claimed')
                          and share.ledger_transaction_id = target_transaction_id
                    );
                exception
                    when undefined_table then return false;
                end;
                $$;

                create policy ledger_accounts_share_access on ledger.accounts
                    using (
                        gift_card_id is not null
                        and ledger.caller_share_allows_gift_card(gift_card_id)
                    )
                    with check (
                        gift_card_id is not null
                        and ledger.caller_share_allows_gift_card(gift_card_id)
                    );

                create policy ledger_transactions_share_access on ledger.transactions
                    using (ledger.caller_share_allows_transaction(id))
                    with check (ledger.caller_share_allows_transaction(id));

                create policy ledger_entries_share_access on ledger.entries
                    using (
                        ledger.caller_share_allows_transaction(transaction_id)
                        or exists (
                            select 1 from ledger.accounts account
                            where account.id = ledger.entries.account_id
                              and account.gift_card_id is not null
                              and ledger.caller_share_allows_gift_card(account.gift_card_id)
                        )
                    )
                    with check (
                        ledger.caller_share_allows_transaction(transaction_id)
                        or exists (
                            select 1 from ledger.accounts account
                            where account.id = ledger.entries.account_id
                              and account.gift_card_id is not null
                              and ledger.caller_share_allows_gift_card(account.gift_card_id)
                        )
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop policy if exists ledger_entries_share_access on ledger.entries;
                drop policy if exists ledger_transactions_share_access on ledger.transactions;
                drop policy if exists ledger_accounts_share_access on ledger.accounts;
                drop function if exists ledger.caller_share_allows_transaction(uuid);
                drop function if exists ledger.caller_share_allows_gift_card(uuid);
                """);
        }
    }
}
