using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Ledger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentCredentialReadPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                -- Deriving available value needs the card's posted balance, which
                -- means reading its value account and that account's entries. A
                -- verified payment credential admits exactly those rows for
                -- exactly one card.
                --
                -- SELECT only, and only the value account: transactions are not
                -- admitted, because reserving value posts nothing. Confirmation
                -- (ADR-018) will need its own write path and is not enabled here.
                -- plpgsql and tolerant of a missing table: Payments migrates after
                -- Ledger, so the referenced table does not exist yet when this
                -- runs. Same shape as the existing share access helper.
                create function ledger.caller_payment_credential_allows_card(
                    target_gift_card_id uuid)
                returns boolean
                language plpgsql
                stable
                security invoker
                set search_path = pg_catalog, payments
                as $$
                begin
                    if target_gift_card_id is null then
                        return false;
                    end if;

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

                create policy ledger_accounts_payment_candidate_read on ledger.accounts
                    for select
                    using (ledger.caller_payment_credential_allows_card(gift_card_id));

                create policy ledger_entries_payment_candidate_read on ledger.entries
                    for select
                    using (
                        exists (
                            select 1
                            from ledger.accounts account
                            where account.id = ledger.entries.account_id
                              and ledger.caller_payment_credential_allows_card(account.gift_card_id)
                        )
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop policy if exists ledger_entries_payment_candidate_read on ledger.entries;
                drop policy if exists ledger_accounts_payment_candidate_read on ledger.accounts;
                drop function if exists ledger.caller_payment_credential_allows_card(uuid);
                """);
        }
    }
}
