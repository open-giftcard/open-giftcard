using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRedemptionConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_provisions_settlement",
                schema: "payments",
                table: "payment_provisions");

            migrationBuilder.AddColumn<decimal>(
                name: "confirmed_amount",
                schema: "payments",
                table: "payment_provisions",
                type: "numeric(20,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gift_card_public_reference",
                schema: "payments",
                table: "payment_provisions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "redemption_ledger_transaction_id",
                schema: "payments",
                table: "payment_provisions",
                type: "uuid",
                nullable: true);

            // Existing provisions predate the denormalized display reference.
            // Backfill it from the authoritative GiftCards module before making
            // the column mandatory; an empty default would create invalid
            // financial history that could never be repaired through the model.
            migrationBuilder.Sql(
                """
                select set_config('app.is_platform_operator', 'true', true);

                update payments.payment_provisions provision
                set gift_card_public_reference = card.public_reference
                from gift_cards.gift_cards card
                where card.id = provision.gift_card_id;

                select set_config('app.is_platform_operator', 'false', true);
                """);

            migrationBuilder.AlterColumn<string>(
                name: "gift_card_public_reference",
                schema: "payments",
                table: "payment_provisions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_payment_provisions_redemption_transaction",
                schema: "payments",
                table: "payment_provisions",
                column: "redemption_ledger_transaction_id",
                unique: true,
                filter: "\"redemption_ledger_transaction_id\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_provisions_settlement",
                schema: "payments",
                table: "payment_provisions",
                sql: "(\"state\" = 'Active'\n    AND \"settled_at_utc\" IS NULL\n    AND \"confirmed_amount\" IS NULL\n    AND \"redemption_ledger_transaction_id\" IS NULL)\nOR\n(\"state\" = 'Confirmed'\n    AND \"settled_at_utc\" IS NOT NULL\n    AND \"confirmed_amount\" > 0\n    AND \"confirmed_amount\" <= \"amount\"\n    AND \"redemption_ledger_transaction_id\" IS NOT NULL)\nOR\n(\"state\" IN ('Cancelled', 'Expired')\n    AND \"settled_at_utc\" IS NOT NULL\n    AND \"confirmed_amount\" IS NULL\n    AND \"redemption_ledger_transaction_id\" IS NULL)");

            migrationBuilder.Sql(
                """
                create or replace function payments.protect_provision_identity()
                returns trigger language plpgsql as $$
                begin
                    if new.id is distinct from old.id
                       or new.payment_token_id is distinct from old.payment_token_id
                       or new.gift_card_id is distinct from old.gift_card_id
                       or new.gift_card_public_reference is distinct from old.gift_card_public_reference
                       or new.funding_organization_id is distinct from old.funding_organization_id
                       or new.owner_user_id is distinct from old.owner_user_id
                       or new.pos_client_id is distinct from old.pos_client_id
                       or new.pos_terminal_id is distinct from old.pos_terminal_id
                       or new.store_reference is distinct from old.store_reference
                       or new.pos_transaction_reference is distinct from old.pos_transaction_reference
                       or new.amount is distinct from old.amount
                       or new.currency is distinct from old.currency
                       or new.created_at_utc is distinct from old.created_at_utc
                       or new.expires_at_utc is distinct from old.expires_at_utc
                    then
                        raise exception 'payment provision identity is immutable' using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;

                create or replace function payments.protect_provision_settlement()
                returns trigger language plpgsql as $$
                begin
                    if old.state <> 'Active'
                       and (
                           new.state is distinct from old.state
                           or new.settled_at_utc is distinct from old.settled_at_utc
                           or new.confirmed_amount is distinct from old.confirmed_amount
                           or new.redemption_ledger_transaction_id is distinct from old.redemption_ledger_transaction_id
                       )
                    then
                        raise exception 'payment provision is already settled' using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create or replace function payments.protect_provision_identity()
                returns trigger language plpgsql as $$
                begin
                    if new.id is distinct from old.id
                       or new.payment_token_id is distinct from old.payment_token_id
                       or new.gift_card_id is distinct from old.gift_card_id
                       or new.funding_organization_id is distinct from old.funding_organization_id
                       or new.owner_user_id is distinct from old.owner_user_id
                       or new.pos_client_id is distinct from old.pos_client_id
                       or new.pos_terminal_id is distinct from old.pos_terminal_id
                       or new.amount is distinct from old.amount
                       or new.currency is distinct from old.currency
                       or new.created_at_utc is distinct from old.created_at_utc
                       or new.expires_at_utc is distinct from old.expires_at_utc
                    then
                        raise exception 'payment provision identity is immutable' using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;

                create or replace function payments.protect_provision_settlement()
                returns trigger language plpgsql as $$
                begin
                    if old.state <> 'Active' and new.state is distinct from old.state then
                        raise exception 'payment provision is already settled' using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;
                """);

            migrationBuilder.DropIndex(
                name: "ux_payment_provisions_redemption_transaction",
                schema: "payments",
                table: "payment_provisions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_provisions_settlement",
                schema: "payments",
                table: "payment_provisions");

            migrationBuilder.DropColumn(
                name: "confirmed_amount",
                schema: "payments",
                table: "payment_provisions");

            migrationBuilder.DropColumn(
                name: "gift_card_public_reference",
                schema: "payments",
                table: "payment_provisions");

            migrationBuilder.DropColumn(
                name: "redemption_ledger_transaction_id",
                schema: "payments",
                table: "payment_provisions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_provisions_settlement",
                schema: "payments",
                table: "payment_provisions",
                sql: "(\"state\" = 'Active') = (\"settled_at_utc\" IS NULL)");
        }
    }
}
