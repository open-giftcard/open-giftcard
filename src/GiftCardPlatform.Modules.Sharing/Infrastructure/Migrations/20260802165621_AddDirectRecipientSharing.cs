using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Sharing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectRecipientSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sharing_state",
                schema: "sharing",
                table: "shares");

            migrationBuilder.AlterColumn<string>(
                name: "pin_hash",
                schema: "sharing",
                table: "shares",
                type: "character varying(104)",
                maxLength: 104,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(104)",
                oldMaxLength: 104);

            migrationBuilder.AddColumn<bool>(
                name: "identity_was_created_on_claim",
                schema: "sharing",
                table: "shares",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "sharing",
                table: "shares",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "masked_recipient_contact",
                schema: "sharing",
                table: "shares",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.Sql(
                "update sharing.shares set kind = 'ProtectedLink' where kind is null;");

            migrationBuilder.AlterColumn<string>(
                name: "kind",
                schema: "sharing",
                table: "shares",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(24)",
                oldMaxLength: 24,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recipient_contact",
                schema: "sharing",
                table: "shares",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recipient_contact_type",
                schema: "sharing",
                table: "shares",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_sharing_kind",
                schema: "sharing",
                table: "shares",
                sql: "(\"kind\" = 'ProtectedLink'\n    AND \"pin_hash\" IS NOT NULL\n    AND \"recipient_contact_type\" IS NULL\n    AND \"recipient_contact\" IS NULL\n    AND \"masked_recipient_contact\" IS NULL)\nOR\n(\"kind\" = 'DirectInvitation'\n    AND \"pin_hash\" IS NULL\n    AND \"recipient_contact_type\" IN ('Email', 'Phone')\n    AND \"recipient_contact\" IS NOT NULL\n    AND \"masked_recipient_contact\" IS NOT NULL\n    AND \"failed_pin_attempts\" = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sharing_state",
                schema: "sharing",
                table: "shares",
                sql: "(\"state\" = 'Pending'\r\n    AND \"claimed_by_user_id\" IS NULL\r\n    AND \"child_gift_card_id\" IS NULL\r\n    AND \"ledger_transaction_id\" IS NULL\r\n    AND \"claim_idempotency_key\" IS NULL\n    AND \"identity_was_created_on_claim\" IS NULL\n    AND \"claimed_at_utc\" IS NULL\r\n    AND \"closed_at_utc\" IS NULL)\r\nOR\r\n(\"state\" = 'Claiming'\r\n    AND \"claimed_by_user_id\" IS NOT NULL\r\n    AND \"child_gift_card_id\" IS NOT NULL\r\n    AND \"ledger_transaction_id\" IS NOT NULL\r\n    AND \"claim_idempotency_key\" IS NOT NULL\n    AND \"identity_was_created_on_claim\" IS NULL\n    AND \"claimed_at_utc\" IS NULL\r\n    AND \"closed_at_utc\" IS NULL)\r\nOR\r\n(\"state\" = 'Claimed'\r\n    AND \"claimed_by_user_id\" IS NOT NULL\r\n    AND \"child_gift_card_id\" IS NOT NULL\r\n    AND \"ledger_transaction_id\" IS NOT NULL\r\n    AND \"claim_idempotency_key\" IS NOT NULL\n    AND ((\"kind\" = 'ProtectedLink' AND \"identity_was_created_on_claim\" IS NULL)\n         OR (\"kind\" = 'DirectInvitation' AND \"identity_was_created_on_claim\" IS NOT NULL))\n    AND \"claimed_at_utc\" IS NOT NULL\r\n    AND \"closed_at_utc\" IS NOT NULL)\r\nOR\r\n(\"state\" IN ('Cancelled', 'Expired', 'Locked')\r\n    AND \"claimed_by_user_id\" IS NULL\r\n    AND \"child_gift_card_id\" IS NULL\r\n    AND \"ledger_transaction_id\" IS NULL\r\n    AND \"claim_idempotency_key\" IS NULL\n    AND \"identity_was_created_on_claim\" IS NULL\n    AND \"claimed_at_utc\" IS NULL\r\n    AND \"closed_at_utc\" IS NOT NULL)");

            migrationBuilder.Sql(
                """
                create or replace function sharing.protect_share_identity()
                returns trigger language plpgsql as $$
                begin
                    if new.id is distinct from old.id
                       or new.kind is distinct from old.kind
                       or new.source_gift_card_id is distinct from old.source_gift_card_id
                       or new.funding_organization_id is distinct from old.funding_organization_id
                       or new.sender_user_id is distinct from old.sender_user_id
                       or new.amount is distinct from old.amount
                       or new.currency is distinct from old.currency
                       or new.claim_secret_hash is distinct from old.claim_secret_hash
                       or new.pin_hash is distinct from old.pin_hash
                       or new.recipient_contact_type is distinct from old.recipient_contact_type
                       or new.recipient_contact is distinct from old.recipient_contact
                       or new.masked_recipient_contact is distinct from old.masked_recipient_contact
                       or new.create_idempotency_key is distinct from old.create_idempotency_key
                       or new.created_at_utc is distinct from old.created_at_utc
                       or new.expires_at_utc is distinct from old.expires_at_utc
                    then
                        raise exception 'share identity is immutable' using errcode = '55000';
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
                create or replace function sharing.protect_share_identity()
                returns trigger language plpgsql as $$
                begin
                    if new.id is distinct from old.id
                       or new.source_gift_card_id is distinct from old.source_gift_card_id
                       or new.funding_organization_id is distinct from old.funding_organization_id
                       or new.sender_user_id is distinct from old.sender_user_id
                       or new.amount is distinct from old.amount
                       or new.currency is distinct from old.currency
                       or new.claim_secret_hash is distinct from old.claim_secret_hash
                       or new.pin_hash is distinct from old.pin_hash
                       or new.create_idempotency_key is distinct from old.create_idempotency_key
                       or new.created_at_utc is distinct from old.created_at_utc
                       or new.expires_at_utc is distinct from old.expires_at_utc
                    then
                        raise exception 'share identity is immutable' using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_sharing_kind",
                schema: "sharing",
                table: "shares");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sharing_state",
                schema: "sharing",
                table: "shares");

            migrationBuilder.DropColumn(
                name: "identity_was_created_on_claim",
                schema: "sharing",
                table: "shares");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "sharing",
                table: "shares");

            migrationBuilder.DropColumn(
                name: "masked_recipient_contact",
                schema: "sharing",
                table: "shares");

            migrationBuilder.DropColumn(
                name: "recipient_contact",
                schema: "sharing",
                table: "shares");

            migrationBuilder.DropColumn(
                name: "recipient_contact_type",
                schema: "sharing",
                table: "shares");

            migrationBuilder.AlterColumn<string>(
                name: "pin_hash",
                schema: "sharing",
                table: "shares",
                type: "character varying(104)",
                maxLength: 104,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(104)",
                oldMaxLength: 104,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_sharing_state",
                schema: "sharing",
                table: "shares",
                sql: "(\"state\" = 'Pending'\n    AND \"claimed_by_user_id\" IS NULL\n    AND \"child_gift_card_id\" IS NULL\n    AND \"ledger_transaction_id\" IS NULL\n    AND \"claim_idempotency_key\" IS NULL\n    AND \"claimed_at_utc\" IS NULL\n    AND \"closed_at_utc\" IS NULL)\nOR\n(\"state\" = 'Claiming'\n    AND \"claimed_by_user_id\" IS NOT NULL\n    AND \"child_gift_card_id\" IS NOT NULL\n    AND \"ledger_transaction_id\" IS NOT NULL\n    AND \"claim_idempotency_key\" IS NOT NULL\n    AND \"claimed_at_utc\" IS NULL\n    AND \"closed_at_utc\" IS NULL)\nOR\n(\"state\" = 'Claimed'\n    AND \"claimed_by_user_id\" IS NOT NULL\n    AND \"child_gift_card_id\" IS NOT NULL\n    AND \"ledger_transaction_id\" IS NOT NULL\n    AND \"claim_idempotency_key\" IS NOT NULL\n    AND \"claimed_at_utc\" IS NOT NULL\n    AND \"closed_at_utc\" IS NOT NULL)\nOR\n(\"state\" IN ('Cancelled', 'Expired', 'Locked')\n    AND \"claimed_by_user_id\" IS NULL\n    AND \"child_gift_card_id\" IS NULL\n    AND \"ledger_transaction_id\" IS NULL\n    AND \"claim_idempotency_key\" IS NULL\n    AND \"claimed_at_utc\" IS NULL\n    AND \"closed_at_utc\" IS NOT NULL)");
        }
    }
}
