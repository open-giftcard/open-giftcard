using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Distribution.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAsyncBulkGiftCardBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_distribution_bulk_item_card",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropIndex(
                name: "ux_distribution_bulk_item_invitation",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_distribution_bulk_items_result_states",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_distribution_bulk_batches_state",
                schema: "distribution",
                table: "bulk_batches");

            migrationBuilder.DropCheckConstraint(
                name: "ck_distribution_bulk_batches_total_items",
                schema: "distribution",
                table: "bulk_batches");

            migrationBuilder.AlterColumn<string>(
                name: "invitation_state",
                schema: "distribution",
                table: "bulk_items",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<Guid>(
                name: "invitation_id",
                schema: "distribution",
                table: "bulk_items",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "gift_card_state",
                schema: "distribution",
                table: "bulk_items",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "gift_card_public_reference",
                schema: "distribution",
                table: "bulk_items",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<Guid>(
                name: "gift_card_id",
                schema: "distribution",
                table: "bulk_items",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "distributed_at_utc",
                schema: "distribution",
                table: "bulk_items",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<decimal>(
                name: "amount",
                schema: "distribution",
                table: "bulk_items",
                type: "numeric(20,4)",
                precision: 20,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AddColumn<string>(
                name: "distribution_idempotency_key",
                schema: "distribution",
                table: "bulk_items",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at_utc",
                schema: "distribution",
                table: "bulk_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "failure_code",
                schema: "distribution",
                table: "bulk_items",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "failure_message",
                schema: "distribution",
                table: "bulk_items",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_divisible",
                schema: "distribution",
                table: "bulk_items",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_transferable",
                schema: "distribution",
                table: "bulk_items",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "issuance_idempotency_key",
                schema: "distribution",
                table: "bulk_items",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recipient_contact",
                schema: "distribution",
                table: "bulk_items",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "settled_at_utc",
                schema: "distribution",
                table: "bulk_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "state",
                schema: "distribution",
                table: "bulk_items",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "valid_from_utc",
                schema: "distribution",
                table: "bulk_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "failed_items",
                schema: "distribution",
                table: "bulk_batches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "retry_of_batch_id",
                schema: "distribution",
                table: "bulk_batches",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "succeeded_items",
                schema: "distribution",
                table: "bulk_batches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                select set_config('app.is_platform_operator', 'true', true);

                drop trigger if exists distribution_bulk_batch_immutable
                    on distribution.bulk_batches;
                drop trigger if exists distribution_bulk_item_valid
                    on distribution.bulk_items;
                drop trigger if exists distribution_bulk_items_append_only
                    on distribution.bulk_items;
                drop function if exists distribution.protect_bulk_batch();
                drop function if exists distribution.validate_bulk_item();
                drop function if exists distribution.reject_bulk_item_mutation();

                update distribution.bulk_items item
                set state = 'Succeeded',
                    recipient_contact = invitation.recipient_contact,
                    valid_from_utc = card.valid_from_utc,
                    expires_at_utc = card.expires_at_utc,
                    is_transferable = card.is_transferable,
                    is_divisible = card.is_divisible,
                    issuance_idempotency_key = card.idempotency_key,
                    distribution_idempotency_key = invitation.idempotency_key,
                    settled_at_utc = item.distributed_at_utc
                from gift_cards.gift_cards card,
                     distribution.invitations invitation
                where card.id = item.gift_card_id
                  and invitation.id = item.invitation_id;

                update distribution.bulk_batches
                set succeeded_items = total_items;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "distribution_idempotency_key",
                schema: "distribution",
                table: "bulk_items",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "expires_at_utc",
                schema: "distribution",
                table: "bulk_items",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<bool>(
                name: "is_divisible",
                schema: "distribution",
                table: "bulk_items",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);

            migrationBuilder.AlterColumn<bool>(
                name: "is_transferable",
                schema: "distribution",
                table: "bulk_items",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "issuance_idempotency_key",
                schema: "distribution",
                table: "bulk_items",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "recipient_contact",
                schema: "distribution",
                table: "bulk_items",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "state",
                schema: "distribution",
                table: "bulk_items",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "valid_from_utc",
                schema: "distribution",
                table: "bulk_items",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_distribution_bulk_item_card",
                schema: "distribution",
                table: "bulk_items",
                column: "gift_card_id",
                unique: true,
                filter: "\"gift_card_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_distribution_bulk_item_invitation",
                schema: "distribution",
                table: "bulk_items",
                column: "invitation_id",
                unique: true,
                filter: "\"invitation_id\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_distribution_bulk_items_outcome",
                schema: "distribution",
                table: "bulk_items",
                sql: "(\n    \"state\" = 'Pending'\n    AND \"gift_card_id\" IS NULL\n    AND \"gift_card_public_reference\" IS NULL\n    AND \"invitation_id\" IS NULL\n    AND \"gift_card_state\" IS NULL\n    AND \"invitation_state\" IS NULL\n    AND \"distributed_at_utc\" IS NULL\n    AND \"failure_code\" IS NULL\n    AND \"failure_message\" IS NULL\n    AND \"settled_at_utc\" IS NULL\n)\nOR\n(\n    \"state\" = 'Succeeded'\n    AND \"gift_card_id\" IS NOT NULL\n    AND \"gift_card_public_reference\" IS NOT NULL\n    AND \"invitation_id\" IS NOT NULL\n    AND \"gift_card_state\" = 'AwaitingClaim'\n    AND \"invitation_state\" = 'Pending'\n    AND \"distributed_at_utc\" IS NOT NULL\n    AND \"failure_code\" IS NULL\n    AND \"failure_message\" IS NULL\n    AND \"settled_at_utc\" IS NOT NULL\n)\nOR\n(\n    \"state\" = 'Failed'\n    AND \"gift_card_id\" IS NULL\n    AND \"gift_card_public_reference\" IS NULL\n    AND \"invitation_id\" IS NULL\n    AND \"gift_card_state\" IS NULL\n    AND \"invitation_state\" IS NULL\n    AND \"distributed_at_utc\" IS NULL\n    AND \"failure_code\" IS NOT NULL\n    AND \"failure_message\" IS NOT NULL\n    AND \"settled_at_utc\" IS NOT NULL\n)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_distribution_bulk_items_validity",
                schema: "distribution",
                table: "bulk_items",
                sql: "\"expires_at_utc\" > \"valid_from_utc\"");

            migrationBuilder.CreateIndex(
                name: "ux_distribution_bulk_batch_retry_parent",
                schema: "distribution",
                table: "bulk_batches",
                column: "retry_of_batch_id",
                unique: true,
                filter: "\"retry_of_batch_id\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_distribution_bulk_batches_counts",
                schema: "distribution",
                table: "bulk_batches",
                sql: "\"succeeded_items\" >= 0\nAND \"failed_items\" >= 0\nAND \"succeeded_items\" + \"failed_items\" <= \"total_items\"\nAND (\n    \"state\" <> 'Completed'\n    OR \"succeeded_items\" + \"failed_items\" = \"total_items\"\n)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_distribution_bulk_batches_state",
                schema: "distribution",
                table: "bulk_batches",
                sql: "(\"state\" IN ('Pending', 'Processing') AND \"completed_at_utc\" IS NULL)\nOR\n(\"state\" = 'Completed' AND \"completed_at_utc\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_distribution_bulk_batches_total_items",
                schema: "distribution",
                table: "bulk_batches",
                sql: "\"total_items\" between 1 and 2000");

            migrationBuilder.AddForeignKey(
                name: "FK_bulk_batches_bulk_batches_retry_of_batch_id",
                schema: "distribution",
                table: "bulk_batches",
                column: "retry_of_batch_id",
                principalSchema: "distribution",
                principalTable: "bulk_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(
                """
                create function distribution.protect_bulk_batch()
                returns trigger
                language plpgsql
                as $$
                begin
                    if tg_op = 'DELETE' then
                        raise exception
                            'gift-card bulk batches are immutable history'
                            using errcode = '55000';
                    end if;

                    if old.state = 'Completed'
                       or new.state not in ('Processing', 'Completed')
                       or new.id is distinct from old.id
                       or new.funding_organization_id is distinct from old.funding_organization_id
                       or new.issuing_organization_id is distinct from old.issuing_organization_id
                       or new.batch_reference is distinct from old.batch_reference
                       or new.idempotency_key is distinct from old.idempotency_key
                       or new.intent_hash is distinct from old.intent_hash
                       or new.total_items is distinct from old.total_items
                       or new.created_by_user_id is distinct from old.created_by_user_id
                       or new.created_by_membership_id is distinct from old.created_by_membership_id
                       or new.created_at_utc is distinct from old.created_at_utc
                       or new.retry_of_batch_id is distinct from old.retry_of_batch_id
                       or new.succeeded_items < old.succeeded_items
                       or new.failed_items < old.failed_items
                       or (
                            (new.succeeded_items + new.failed_items)
                                <> (old.succeeded_items + old.failed_items + 1)
                            and not (
                                old.state = 'Pending'
                                and new.state = 'Processing'
                                and new.succeeded_items = old.succeeded_items
                                and new.failed_items = old.failed_items
                            )
                       )
                       or (new.state = 'Completed' and (
                            new.completed_at_utc is null
                            or new.succeeded_items + new.failed_items <> new.total_items
                       ))
                       or (new.state = 'Processing' and new.completed_at_utc is not null)
                    then
                        raise exception
                            'gift-card bulk batch transition is invalid'
                            using errcode = '55000';
                    end if;

                    return new;
                end;
                $$;

                create trigger distribution_bulk_batch_immutable
                    before update or delete
                    on distribution.bulk_batches
                    for each row execute function distribution.protect_bulk_batch();

                create function distribution.protect_bulk_item()
                returns trigger
                language plpgsql
                as $$
                begin
                    if tg_op = 'DELETE' or old.state <> 'Pending'
                       or new.state not in ('Succeeded', 'Failed')
                       or new.id is distinct from old.id
                       or new.batch_id is distinct from old.batch_id
                       or new.funding_organization_id is distinct from old.funding_organization_id
                       or new.issuing_organization_id is distinct from old.issuing_organization_id
                       or new.position is distinct from old.position
                       or new.item_reference is distinct from old.item_reference
                       or new.amount is distinct from old.amount
                       or new.currency is distinct from old.currency
                       or new.valid_from_utc is distinct from old.valid_from_utc
                       or new.expires_at_utc is distinct from old.expires_at_utc
                       or new.is_transferable is distinct from old.is_transferable
                       or new.is_divisible is distinct from old.is_divisible
                       or new.contact_type is distinct from old.contact_type
                       or new.recipient_contact is distinct from old.recipient_contact
                       or new.masked_recipient_contact is distinct from old.masked_recipient_contact
                       or new.issuance_idempotency_key is distinct from old.issuance_idempotency_key
                       or new.distribution_idempotency_key is distinct from old.distribution_idempotency_key
                    then
                        raise exception
                            'gift-card bulk item outcome is immutable'
                            using errcode = '55000';
                    end if;

                    return new;
                end;
                $$;

                create trigger distribution_bulk_items_append_only
                    before update or delete
                    on distribution.bulk_items
                    for each row execute function distribution.protect_bulk_item();

                create function distribution.validate_bulk_item()
                returns trigger
                language plpgsql
                as $$
                begin
                    if not exists (
                        select 1
                        from distribution.bulk_batches batch
                        where batch.id = new.batch_id
                          and batch.funding_organization_id = new.funding_organization_id
                          and batch.issuing_organization_id = new.issuing_organization_id
                          and batch.state <> 'Completed'
                    ) and new.state = 'Pending'
                    then
                        raise exception
                            'gift-card bulk item does not match an active batch'
                            using errcode = '55000';
                    end if;

                    if new.state = 'Succeeded' and not exists (
                        select 1
                        from gift_cards.gift_cards card
                        join distribution.invitations invitation
                          on invitation.id = new.invitation_id
                         and invitation.gift_card_id = card.id
                        where card.id = new.gift_card_id
                          and card.funding_organization_id = new.funding_organization_id
                          and card.issuing_organization_id = new.issuing_organization_id
                          and card.public_reference = new.gift_card_public_reference
                          and card.initial_value = new.amount
                          and card.currency = new.currency
                          and card.valid_from_utc = new.valid_from_utc
                          and card.expires_at_utc = new.expires_at_utc
                          and card.is_transferable = new.is_transferable
                          and card.is_divisible = new.is_divisible
                          and card.idempotency_key = new.issuance_idempotency_key
                          and invitation.funding_organization_id = new.funding_organization_id
                          and invitation.issuing_organization_id = new.issuing_organization_id
                          and invitation.contact_type = new.contact_type
                          and invitation.recipient_contact = new.recipient_contact
                          and invitation.masked_recipient_contact = new.masked_recipient_contact
                          and invitation.idempotency_key = new.distribution_idempotency_key
                          and invitation.state = new.invitation_state
                          and invitation.distributed_at_utc = new.distributed_at_utc
                    )
                    then
                        raise exception
                            'gift-card bulk item result does not match its sources'
                            using errcode = '55000';
                    end if;

                    return new;
                end;
                $$;

                create trigger distribution_bulk_item_valid
                    before insert or update
                    on distribution.bulk_items
                    for each row execute function distribution.validate_bulk_item();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                select set_config('app.is_platform_operator', 'true', true);
                drop trigger if exists distribution_bulk_batch_immutable
                    on distribution.bulk_batches;
                drop trigger if exists distribution_bulk_item_valid
                    on distribution.bulk_items;
                drop trigger if exists distribution_bulk_items_append_only
                    on distribution.bulk_items;
                drop function if exists distribution.protect_bulk_batch();
                drop function if exists distribution.validate_bulk_item();
                drop function if exists distribution.protect_bulk_item();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_bulk_batches_bulk_batches_retry_of_batch_id",
                schema: "distribution",
                table: "bulk_batches");

            migrationBuilder.Sql(
                """
                delete from distribution.bulk_items item
                where item.batch_id in (
                    select batch.id
                    from distribution.bulk_batches batch
                    where batch.retry_of_batch_id is not null
                       or batch.total_items > 100
                       or not exists (
                            select 1
                            from distribution.bulk_items candidate
                            where candidate.batch_id = batch.id
                       )
                       or exists (
                            select 1
                            from distribution.bulk_items candidate
                            where candidate.batch_id = batch.id
                              and candidate.state <> 'Succeeded'
                       )
                );

                delete from distribution.bulk_batches batch
                where batch.retry_of_batch_id is not null
                   or batch.total_items > 100
                   or not exists (
                        select 1
                        from distribution.bulk_items item
                        where item.batch_id = batch.id
                   );
                """);

            migrationBuilder.DropIndex(
                name: "ux_distribution_bulk_item_card",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropIndex(
                name: "ux_distribution_bulk_item_invitation",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_distribution_bulk_items_outcome",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_distribution_bulk_items_validity",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropIndex(
                name: "ux_distribution_bulk_batch_retry_parent",
                schema: "distribution",
                table: "bulk_batches");

            migrationBuilder.DropCheckConstraint(
                name: "ck_distribution_bulk_batches_counts",
                schema: "distribution",
                table: "bulk_batches");

            migrationBuilder.DropCheckConstraint(
                name: "ck_distribution_bulk_batches_state",
                schema: "distribution",
                table: "bulk_batches");

            migrationBuilder.DropCheckConstraint(
                name: "ck_distribution_bulk_batches_total_items",
                schema: "distribution",
                table: "bulk_batches");

            migrationBuilder.DropColumn(
                name: "distribution_idempotency_key",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropColumn(
                name: "expires_at_utc",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropColumn(
                name: "failure_code",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropColumn(
                name: "failure_message",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropColumn(
                name: "is_divisible",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropColumn(
                name: "is_transferable",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropColumn(
                name: "issuance_idempotency_key",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropColumn(
                name: "recipient_contact",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropColumn(
                name: "settled_at_utc",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropColumn(
                name: "state",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropColumn(
                name: "valid_from_utc",
                schema: "distribution",
                table: "bulk_items");

            migrationBuilder.DropColumn(
                name: "failed_items",
                schema: "distribution",
                table: "bulk_batches");

            migrationBuilder.DropColumn(
                name: "retry_of_batch_id",
                schema: "distribution",
                table: "bulk_batches");

            migrationBuilder.DropColumn(
                name: "succeeded_items",
                schema: "distribution",
                table: "bulk_batches");

            migrationBuilder.AlterColumn<string>(
                name: "invitation_state",
                schema: "distribution",
                table: "bulk_items",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "invitation_id",
                schema: "distribution",
                table: "bulk_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "gift_card_state",
                schema: "distribution",
                table: "bulk_items",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "gift_card_public_reference",
                schema: "distribution",
                table: "bulk_items",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "gift_card_id",
                schema: "distribution",
                table: "bulk_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "distributed_at_utc",
                schema: "distribution",
                table: "bulk_items",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "amount",
                schema: "distribution",
                table: "bulk_items",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(20,4)",
                oldPrecision: 20,
                oldScale: 4);

            migrationBuilder.CreateIndex(
                name: "ux_distribution_bulk_item_card",
                schema: "distribution",
                table: "bulk_items",
                column: "gift_card_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_distribution_bulk_item_invitation",
                schema: "distribution",
                table: "bulk_items",
                column: "invitation_id",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_distribution_bulk_items_result_states",
                schema: "distribution",
                table: "bulk_items",
                sql: "\"gift_card_state\" = 'AwaitingClaim'\nAND \"invitation_state\" = 'Pending'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_distribution_bulk_batches_state",
                schema: "distribution",
                table: "bulk_batches",
                sql: "(\"state\" = 'Processing' AND \"completed_at_utc\" IS NULL)\nOR\n(\"state\" = 'Completed' AND \"completed_at_utc\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_distribution_bulk_batches_total_items",
                schema: "distribution",
                table: "bulk_batches",
                sql: "\"total_items\" between 1 and 100");

            migrationBuilder.Sql(
                """
                create function distribution.protect_bulk_batch()
                returns trigger
                language plpgsql
                as $$
                begin
                    if tg_op = 'DELETE' then
                        raise exception
                            'gift-card bulk batches are immutable history'
                            using errcode = '55000';
                    end if;

                    if old.state <> 'Processing'
                       or new.state <> 'Completed'
                       or new.id is distinct from old.id
                       or new.funding_organization_id is distinct from
                          old.funding_organization_id
                       or new.issuing_organization_id is distinct from
                          old.issuing_organization_id
                       or new.batch_reference is distinct from
                          old.batch_reference
                       or new.idempotency_key is distinct from
                          old.idempotency_key
                       or new.intent_hash is distinct from old.intent_hash
                       or new.total_items is distinct from old.total_items
                       or new.created_by_user_id is distinct from
                          old.created_by_user_id
                       or new.created_by_membership_id is distinct from
                          old.created_by_membership_id
                       or new.created_at_utc is distinct from old.created_at_utc
                       or new.completed_at_utc is null
                    then
                        raise exception
                            'gift-card bulk batch transition is invalid'
                            using errcode = '55000';
                    end if;

                    if (
                        select count(*)
                        from distribution.bulk_items item
                        where item.batch_id = old.id
                    ) <> old.total_items
                    then
                        raise exception
                            'gift-card bulk batch item count is incomplete'
                            using errcode = '55000';
                    end if;

                    return new;
                end;
                $$;

                create trigger distribution_bulk_batch_immutable
                    before update or delete on distribution.bulk_batches
                    for each row execute function distribution.protect_bulk_batch();

                create function distribution.validate_bulk_item()
                returns trigger
                language plpgsql
                as $$
                begin
                    if not exists (
                        select 1
                        from gift_cards.gift_cards card
                        where card.id = new.gift_card_id
                    )
                    then
                        raise exception
                            'gift-card bulk item card source is unavailable'
                            using errcode = '55000';
                    end if;

                    if not exists (
                        select 1
                        from distribution.bulk_batches batch
                        where batch.id = new.batch_id
                          and batch.state = 'Processing'
                          and batch.funding_organization_id =
                              new.funding_organization_id
                          and batch.issuing_organization_id =
                              new.issuing_organization_id
                          and new.position <= batch.total_items
                    )
                    then
                        raise exception
                            'gift-card bulk item does not match its processing batch'
                            using errcode = '55000';
                    end if;

                    if not exists (
                        select 1
                        from gift_cards.gift_cards card
                        where card.id = new.gift_card_id
                          and card.funding_organization_id =
                              new.funding_organization_id
                    )
                    then
                        raise exception
                            'gift-card bulk item funding tenant does not match card'
                            using errcode = '55000';
                    end if;

                    if not exists (
                        select 1
                        from gift_cards.gift_cards card
                        where card.id = new.gift_card_id
                          and card.issuing_organization_id =
                              new.issuing_organization_id
                    )
                    then
                        raise exception
                            'gift-card bulk item issuing organization does not match card'
                            using errcode = '55000';
                    end if;

                    if not exists (
                        select 1
                        from gift_cards.gift_cards card
                        where card.id = new.gift_card_id
                          and card.public_reference =
                              new.gift_card_public_reference
                    )
                    then
                        raise exception
                            'gift-card bulk item public reference does not match card'
                            using errcode = '55000';
                    end if;

                    if not exists (
                        select 1
                        from gift_cards.gift_cards card
                        where card.id = new.gift_card_id
                          and card.initial_value = new.amount
                          and card.currency = new.currency
                    )
                    then
                        raise exception
                            'gift-card bulk item amount does not match card'
                            using errcode = '55000';
                    end if;

                    if not exists (
                        select 1
                        from gift_cards.gift_cards card
                        where card.id = new.gift_card_id
                          and card.lifecycle_state = new.gift_card_state
                    )
                    then
                        raise exception
                            'gift-card bulk item state does not match card'
                            using errcode = '55000';
                    end if;

                    if not exists (
                        select 1
                        from gift_cards.gift_cards card
                        where card.id = new.gift_card_id
                          and card.distribution_invitation_id =
                              new.invitation_id
                    )
                    then
                        raise exception
                            'gift-card bulk item invitation does not match card'
                            using errcode = '55000';
                    end if;

                    if not exists (
                        select 1
                        from distribution.invitations invitation
                        where invitation.id = new.invitation_id
                          and invitation.gift_card_id = new.gift_card_id
                          and invitation.funding_organization_id =
                              new.funding_organization_id
                          and invitation.issuing_organization_id =
                              new.issuing_organization_id
                          and invitation.contact_type = new.contact_type
                          and invitation.masked_recipient_contact =
                              new.masked_recipient_contact
                          and invitation.state = new.invitation_state
                          and invitation.distributed_at_utc =
                              new.distributed_at_utc
                    )
                    then
                        raise exception
                            'gift-card bulk item invitation result does not match source'
                            using errcode = '55000';
                    end if;

                    return new;
                end;
                $$;

                create trigger distribution_bulk_item_valid
                    before insert on distribution.bulk_items
                    for each row execute function distribution.validate_bulk_item();

                create function distribution.reject_bulk_item_mutation()
                returns trigger
                language plpgsql
                as $$
                begin
                    raise exception
                        'gift-card bulk item results are append-only'
                        using errcode = '55000';
                end;
                $$;

                create trigger distribution_bulk_items_append_only
                    before update or delete on distribution.bulk_items
                    for each row execute function distribution.reject_bulk_item_mutation();
                """);
        }
    }
}
