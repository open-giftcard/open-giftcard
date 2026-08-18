using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Distribution.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBulkGiftCardBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bulk_batches",
                schema: "distribution",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    funding_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuing_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    intent_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    total_items = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bulk_batches", x => x.id);
                    table.CheckConstraint("ck_distribution_bulk_batches_completion", "\"completed_at_utc\" IS NULL\nOR \"completed_at_utc\" >= \"created_at_utc\"");
                    table.CheckConstraint("ck_distribution_bulk_batches_state", "(\"state\" = 'Processing' AND \"completed_at_utc\" IS NULL)\nOR\n(\"state\" = 'Completed' AND \"completed_at_utc\" IS NOT NULL)");
                    table.CheckConstraint("ck_distribution_bulk_batches_total_items", "\"total_items\" between 1 and 100");
                });

            migrationBuilder.CreateTable(
                name: "bulk_items",
                schema: "distribution",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    funding_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuing_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    item_reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    gift_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gift_card_public_reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    invitation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    masked_recipient_contact = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    gift_card_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    invitation_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    distributed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bulk_items", x => x.id);
                    table.CheckConstraint("ck_distribution_bulk_items_amount", "\"amount\" > 0");
                    table.CheckConstraint("ck_distribution_bulk_items_currency", "\"currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_distribution_bulk_items_position", "\"position\" > 0");
                    table.CheckConstraint("ck_distribution_bulk_items_result_states", "\"gift_card_state\" = 'AwaitingClaim'\nAND \"invitation_state\" = 'Pending'");
                    table.ForeignKey(
                        name: "FK_bulk_items_bulk_batches_batch_id",
                        column: x => x.batch_id,
                        principalSchema: "distribution",
                        principalTable: "bulk_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_distribution_bulk_batch_history",
                schema: "distribution",
                table: "bulk_batches",
                columns: new[] { "issuing_organization_id", "created_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_distribution_bulk_batch_tenant_idempotency",
                schema: "distribution",
                table: "bulk_batches",
                columns: new[] { "funding_organization_id", "idempotency_key" },
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "ux_distribution_bulk_item_position",
                schema: "distribution",
                table: "bulk_items",
                columns: new[] { "batch_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_distribution_bulk_item_reference",
                schema: "distribution",
                table: "bulk_items",
                columns: new[] { "batch_id", "item_reference" },
                unique: true);

            migrationBuilder.Sql(
                """
                alter table distribution.bulk_batches
                    enable row level security;
                alter table distribution.bulk_batches
                    force row level security;
                alter table distribution.bulk_items
                    enable row level security;
                alter table distribution.bulk_items
                    force row level security;

                create policy distribution_bulk_batches_isolation
                    on distribution.bulk_batches
                    using (
                        coalesce(
                            nullif(
                                current_setting(
                                    'app.is_platform_operator',
                                    true),
                                ''),
                            'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(
                            funding_organization_id)
                    )
                    with check (
                        coalesce(
                            nullif(
                                current_setting(
                                    'app.is_platform_operator',
                                    true),
                                ''),
                            'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(
                            funding_organization_id)
                    );

                create policy distribution_bulk_items_isolation
                    on distribution.bulk_items
                    using (
                        coalesce(
                            nullif(
                                current_setting(
                                    'app.is_platform_operator',
                                    true),
                                ''),
                            'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(
                            funding_organization_id)
                    )
                    with check (
                        coalesce(
                            nullif(
                                current_setting(
                                    'app.is_platform_operator',
                                    true),
                                ''),
                            'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(
                            funding_organization_id)
                    );

                alter table distribution.bulk_items
                    add constraint fk_distribution_bulk_item_card
                    foreign key (gift_card_id)
                    references gift_cards.gift_cards (id)
                    on delete restrict;

                alter table distribution.bulk_items
                    add constraint fk_distribution_bulk_item_invitation
                    foreign key (invitation_id)
                    references distribution.invitations (id)
                    on delete restrict;

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
                    before update or delete
                    on distribution.bulk_batches
                    for each row execute function
                        distribution.protect_bulk_batch();

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
                    for each row execute function
                        distribution.validate_bulk_item();

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
                    for each row execute function
                        distribution.reject_bulk_item_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop function if exists
                    distribution.reject_bulk_item_mutation() cascade;
                drop function if exists
                    distribution.validate_bulk_item() cascade;
                drop function if exists
                    distribution.protect_bulk_batch() cascade;
                """);

            migrationBuilder.DropTable(
                name: "bulk_items",
                schema: "distribution");

            migrationBuilder.DropTable(
                name: "bulk_batches",
                schema: "distribution");
        }
    }
}
