using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Ledger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRedemptionSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ledger_accounts_scope",
                schema: "ledger",
                table: "accounts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ledger_accounts_scope",
                schema: "ledger",
                table: "accounts",
                sql: "(\"type\" = 'PlatformFunding'\n    AND \"organization_id\" IS NULL\n    AND \"gift_card_id\" IS NULL)\nOR\n(\"type\" = 'PlatformRedemptionSettlement'\n    AND \"organization_id\" IS NULL\n    AND \"gift_card_id\" IS NULL)\nOR\n(\"type\" = 'OrganizationCorporateCredit'\n    AND \"organization_id\" IS NOT NULL\n    AND \"gift_card_id\" IS NULL)\nOR\n(\"type\" = 'GiftCardValue'\n    AND \"organization_id\" IS NOT NULL\n    AND \"gift_card_id\" IS NOT NULL)");

            migrationBuilder.Sql(
                """
                -- A POS principal has no tenant membership. These policies admit
                -- only the ledger rows needed to confirm the exact provision
                -- named by both its authenticated POS client and server-parsed
                -- payment-token candidate. Neither identifier is sufficient on
                -- its own.
                create function ledger.caller_payment_provision_matches(
                    target_organization_id uuid,
                    target_gift_card_id uuid,
                    target_currency text)
                returns boolean
                language plpgsql
                stable
                security invoker
                set search_path = pg_catalog, payments
                as $$
                begin
                    return exists (
                        select 1
                        from payments.payment_provisions provision
                        where provision.payment_token_id = nullif(current_setting('app.payment_token_id', true), '')::uuid
                          and provision.pos_client_id = nullif(current_setting('app.pos_client_id', true), '')::uuid
                          and provision.funding_organization_id = target_organization_id
                          and provision.gift_card_id = target_gift_card_id
                          and provision.currency = target_currency
                    );
                exception
                    when undefined_table then return false;
                end;
                $$;

                create function ledger.caller_payment_provision_allows_settlement(
                    target_currency text)
                returns boolean
                language plpgsql
                stable
                security invoker
                set search_path = pg_catalog, payments
                as $$
                begin
                    return exists (
                        select 1
                        from payments.payment_provisions provision
                        where provision.payment_token_id = nullif(current_setting('app.payment_token_id', true), '')::uuid
                          and provision.pos_client_id = nullif(current_setting('app.pos_client_id', true), '')::uuid
                          and provision.currency = target_currency
                    );
                exception
                    when undefined_table then return false;
                end;
                $$;

                create function ledger.caller_payment_provision_matches_transaction(
                    target_organization_id uuid,
                    target_operation_type text,
                    target_idempotency_key text)
                returns boolean
                language plpgsql
                stable
                security invoker
                set search_path = pg_catalog, payments
                as $$
                declare
                    candidate_token_id uuid;
                begin
                    candidate_token_id := nullif(current_setting('app.payment_token_id', true), '')::uuid;
                    return target_operation_type = 'gift_card.redemption'
                       and target_idempotency_key = 'payment-token:' || replace(candidate_token_id::text, '-', '')
                       and exists (
                           select 1
                           from payments.payment_provisions provision
                           where provision.payment_token_id = candidate_token_id
                             and provision.pos_client_id = nullif(current_setting('app.pos_client_id', true), '')::uuid
                             and provision.funding_organization_id = target_organization_id
                       );
                exception
                    when undefined_table then return false;
                end;
                $$;

                create policy ledger_accounts_redemption_candidate on ledger.accounts
                    for all
                    using (
                        (type = 'GiftCardValue'
                         and ledger.caller_payment_provision_matches(organization_id, gift_card_id, currency))
                        or
                        (type = 'PlatformRedemptionSettlement'
                         and organization_id is null
                         and gift_card_id is null
                         and ledger.caller_payment_provision_allows_settlement(currency))
                    )
                    with check (
                        type = 'PlatformRedemptionSettlement'
                        and organization_id is null
                        and gift_card_id is null
                        and ledger.caller_payment_provision_allows_settlement(currency)
                    );

                create policy ledger_transactions_redemption_candidate on ledger.transactions
                    for all
                    using (ledger.caller_payment_provision_matches_transaction(
                        organization_id, operation_type, idempotency_key))
                    with check (ledger.caller_payment_provision_matches_transaction(
                        organization_id, operation_type, idempotency_key));

                create policy ledger_entries_redemption_candidate_read on ledger.entries
                    for select
                    using (
                        transaction_id = nullif(
                            current_setting('app.ledger_transaction_id', true), '')::uuid
                    );

                create policy ledger_entries_redemption_candidate_insert on ledger.entries
                    for insert
                    with check (
                        transaction_id = nullif(
                            current_setting('app.ledger_transaction_id', true), '')::uuid
                        and
                        exists (
                            select 1
                            from ledger.accounts account
                            where account.id = ledger.entries.account_id
                              and ledger.entries.currency = account.currency
                              and (
                                  (account.type = 'GiftCardValue'
                                   and ledger.caller_payment_provision_matches(
                                       account.organization_id,
                                       account.gift_card_id,
                                       account.currency))
                                  or
                                  (account.type = 'PlatformRedemptionSettlement'
                                   and account.organization_id is null
                                   and account.gift_card_id is null)
                              )
                        )
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop policy if exists ledger_entries_redemption_candidate_insert on ledger.entries;
                drop policy if exists ledger_entries_redemption_candidate_read on ledger.entries;
                drop policy if exists ledger_transactions_redemption_candidate on ledger.transactions;
                drop policy if exists ledger_accounts_redemption_candidate on ledger.accounts;
                drop function if exists ledger.caller_payment_provision_matches_transaction(uuid, text, text);
                drop function if exists ledger.caller_payment_provision_allows_settlement(text);
                drop function if exists ledger.caller_payment_provision_matches(uuid, uuid, text);
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_ledger_accounts_scope",
                schema: "ledger",
                table: "accounts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ledger_accounts_scope",
                schema: "ledger",
                table: "accounts",
                sql: "(\"type\" = 'PlatformFunding'\n    AND \"organization_id\" IS NULL\n    AND \"gift_card_id\" IS NULL)\nOR\n(\"type\" = 'OrganizationCorporateCredit'\n    AND \"organization_id\" IS NOT NULL\n    AND \"gift_card_id\" IS NULL)\nOR\n(\"type\" = 'GiftCardValue'\n    AND \"organization_id\" IS NOT NULL\n    AND \"gift_card_id\" IS NOT NULL)");
        }
    }
}
