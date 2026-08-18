using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Ledger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentRefundLedgerPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create or replace function ledger.caller_payment_provision_matches_transaction(
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
                    candidate_refund_id uuid;
                begin
                    candidate_token_id := nullif(current_setting('app.payment_token_id', true), '')::uuid;
                    candidate_refund_id := nullif(current_setting('app.payment_refund_id', true), '')::uuid;
                    return (
                            (target_operation_type = 'gift_card.redemption'
                             and target_idempotency_key = 'payment-token:' || replace(candidate_token_id::text, '-', ''))
                            or
                            (target_operation_type = 'gift_card.refund'
                             and candidate_refund_id is not null
                             and target_idempotency_key = 'payment-refund:' || replace(candidate_refund_id::text, '-', ''))
                           )
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
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create or replace function ledger.caller_payment_provision_matches_transaction(
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
                """);
        }
    }
}
