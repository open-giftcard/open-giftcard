using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Distribution.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrphanEpinInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_distribution_invitations_contact_type",
                schema: "distribution",
                table: "invitations");

            migrationBuilder.AlterColumn<string>(
                name: "recipient_contact",
                schema: "distribution",
                table: "invitations",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320);

            migrationBuilder.AlterColumn<string>(
                name: "masked_recipient_contact",
                schema: "distribution",
                table: "invitations",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320);

            migrationBuilder.AlterColumn<Guid>(
                name: "distributed_by_membership_id",
                schema: "distribution",
                table: "invitations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "contact_type",
                schema: "distribution",
                table: "invitations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AddColumn<Guid>(
                name: "distributed_by_partner_client_id",
                schema: "distribution",
                table: "invitations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "distribution",
                table: "invitations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Directed");

            migrationBuilder.AddColumn<string>(
                name: "pin_hash",
                schema: "distribution",
                table: "invitations",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_distribution_invitations_contact_type",
                schema: "distribution",
                table: "invitations",
                sql: "\"contact_type\" is null or \"contact_type\" in ('Email', 'Phone')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_distribution_invitations_kind",
                schema: "distribution",
                table: "invitations",
                sql: "(\"kind\" = 'Directed'\n    AND \"contact_type\" IS NOT NULL\n    AND \"recipient_contact\" IS NOT NULL\n    AND \"masked_recipient_contact\" IS NOT NULL\n    AND \"pin_hash\" IS NULL\n    AND \"distributed_by_membership_id\" IS NOT NULL\n    AND \"distributed_by_partner_client_id\" IS NULL)\nOR\n(\"kind\" = 'OrphanPin'\n    AND \"contact_type\" IS NULL\n    AND \"recipient_contact\" IS NULL\n    AND \"masked_recipient_contact\" IS NULL\n    AND \"pin_hash\" IS NOT NULL\n    AND \"distributed_by_membership_id\" IS NULL\n    AND \"distributed_by_partner_client_id\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_distribution_invitations_partner_client",
                schema: "distribution",
                table: "invitations",
                column: "distributed_by_partner_client_id",
                filter: "\"distributed_by_partner_client_id\" IS NOT NULL");

            migrationBuilder.Sql(
                """
                create or replace function distribution.protect_invitation_identity()
                returns trigger
                language plpgsql
                as $$
                begin
                    if new.id is distinct from old.id
                       or new.funding_organization_id is distinct from old.funding_organization_id
                       or new.issuing_organization_id is distinct from old.issuing_organization_id
                       or new.gift_card_id is distinct from old.gift_card_id
                       or new.kind is distinct from old.kind
                       or new.contact_type is distinct from old.contact_type
                       or new.recipient_contact is distinct from old.recipient_contact
                       or new.masked_recipient_contact is distinct from old.masked_recipient_contact
                       or new.claim_secret_hash is distinct from old.claim_secret_hash
                       or new.pin_hash is distinct from old.pin_hash
                       or new.claim_expires_at_utc is distinct from old.claim_expires_at_utc
                       or new.business_reference is distinct from old.business_reference
                       or new.idempotency_key is distinct from old.idempotency_key
                       or new.distributed_by_user_id is distinct from old.distributed_by_user_id
                       or new.distributed_by_membership_id is distinct from old.distributed_by_membership_id
                       or new.distributed_by_partner_client_id is distinct from old.distributed_by_partner_client_id
                       or new.distributed_at_utc is distinct from old.distributed_at_utc
                    then
                        raise exception 'distribution invitation identity is immutable'
                            using errcode = '55000';
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
                do $$
                begin
                    if exists (select 1 from distribution.invitations where kind = 'OrphanPin') then
                        raise exception 'cannot remove orphan e-pin schema while orphan invitations exist';
                    end if;
                end;
                $$;
                """);

            migrationBuilder.DropIndex(
                name: "ix_distribution_invitations_partner_client",
                schema: "distribution",
                table: "invitations");
            migrationBuilder.DropCheckConstraint(
                name: "ck_distribution_invitations_contact_type",
                schema: "distribution",
                table: "invitations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_distribution_invitations_kind",
                schema: "distribution",
                table: "invitations");

            migrationBuilder.DropColumn(
                name: "distributed_by_partner_client_id",
                schema: "distribution",
                table: "invitations");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "distribution",
                table: "invitations");

            migrationBuilder.DropColumn(
                name: "pin_hash",
                schema: "distribution",
                table: "invitations");

            migrationBuilder.AlterColumn<string>(
                name: "recipient_contact",
                schema: "distribution",
                table: "invitations",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "masked_recipient_contact",
                schema: "distribution",
                table: "invitations",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "distributed_by_membership_id",
                schema: "distribution",
                table: "invitations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "contact_type",
                schema: "distribution",
                table: "invitations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_distribution_invitations_contact_type",
                schema: "distribution",
                table: "invitations",
                sql: "\"contact_type\" in ('Email', 'Phone')");

            migrationBuilder.Sql(
                """
                create or replace function distribution.protect_invitation_identity()
                returns trigger
                language plpgsql
                as $$
                begin
                    if new.id is distinct from old.id
                       or new.funding_organization_id is distinct from old.funding_organization_id
                       or new.issuing_organization_id is distinct from old.issuing_organization_id
                       or new.gift_card_id is distinct from old.gift_card_id
                       or new.contact_type is distinct from old.contact_type
                       or new.recipient_contact is distinct from old.recipient_contact
                       or new.masked_recipient_contact is distinct from old.masked_recipient_contact
                       or new.claim_secret_hash is distinct from old.claim_secret_hash
                       or new.claim_expires_at_utc is distinct from old.claim_expires_at_utc
                       or new.business_reference is distinct from old.business_reference
                       or new.idempotency_key is distinct from old.idempotency_key
                       or new.distributed_by_user_id is distinct from old.distributed_by_user_id
                       or new.distributed_by_membership_id is distinct from old.distributed_by_membership_id
                       or new.distributed_at_utc is distinct from old.distributed_at_utc
                    then
                        raise exception 'distribution invitation identity is immutable'
                            using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;
                """);
        }
    }
}
