using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.GiftCards.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGiftCardLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_gift_cards_distribution_state",
                schema: "gift_cards",
                table: "gift_cards");

            migrationBuilder.AddCheckConstraint(
                name: "ck_gift_cards_distribution_state",
                schema: "gift_cards",
                table: "gift_cards",
                sql: """
                    ("ownership_state" = 'OrganizationInventory'
                        AND "lifecycle_state" IN (
                            'Active', 'Suspended', 'Cancelled', 'Expired')
                        AND "distribution_invitation_id" IS NULL
                        AND "distributed_at_utc" IS NULL
                        AND "claimed_at_utc" IS NULL)
                    OR
                    ("ownership_state" = 'AwaitingClaim'
                        AND "lifecycle_state" IN (
                            'AwaitingClaim', 'Suspended', 'Cancelled', 'Expired')
                        AND "distribution_invitation_id" IS NOT NULL
                        AND "distributed_at_utc" IS NOT NULL
                        AND "claimed_at_utc" IS NULL)
                    OR
                    ("ownership_state" = 'IdentityOwned'
                        AND "lifecycle_state" IN (
                            'Active', 'Suspended', 'Cancelled', 'Expired')
                        AND "distribution_invitation_id" IS NOT NULL
                        AND "distributed_at_utc" IS NOT NULL
                        AND "claimed_at_utc" IS NOT NULL)
                    """);

            migrationBuilder.CreateTable(
                name: "lifecycle_events",
                schema: "gift_cards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    gift_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    funding_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuing_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    previous_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    new_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_membership_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ledger_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    returned_amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lifecycle_events", x => x.id);
                    table.CheckConstraint("ck_gift_card_lifecycle_event_actor", "(\"actor_type\" = 'OrganizationMember'\n    AND \"actor_membership_id\" IS NOT NULL)\nOR\n(\"actor_type\" <> 'OrganizationMember'\n    AND \"actor_membership_id\" IS NULL)");
                    table.CheckConstraint("ck_gift_card_lifecycle_event_financial", "(\n    \"action\" IN ('Cancel', 'Expire')\n    AND \"returned_amount\" IS NOT NULL\n    AND \"returned_amount\" >= 0\n    AND \"currency\" IS NOT NULL\n    AND (\n        (\"returned_amount\" = 0\n            AND \"ledger_transaction_id\" IS NULL)\n        OR\n        (\"returned_amount\" > 0\n            AND \"ledger_transaction_id\" IS NOT NULL)\n    )\n)\nOR\n(\n    \"action\" IN ('Suspend', 'Reactivate')\n    AND \"returned_amount\" IS NULL\n    AND \"currency\" IS NULL\n    AND \"ledger_transaction_id\" IS NULL\n)");
                    table.CheckConstraint("ck_gift_card_lifecycle_event_transition", "(\"action\" = 'Suspend' AND \"new_state\" = 'Suspended')\nOR\n(\"action\" = 'Reactivate' AND \"new_state\" IN ('Active', 'AwaitingClaim'))\nOR\n(\"action\" = 'Cancel' AND \"new_state\" = 'Cancelled')\nOR\n(\"action\" = 'Expire' AND \"new_state\" = 'Expired')");
                    table.ForeignKey(
                        name: "FK_lifecycle_events_gift_cards_gift_card_id",
                        column: x => x.gift_card_id,
                        principalSchema: "gift_cards",
                        principalTable: "gift_cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_gift_card_lifecycle_history",
                schema: "gift_cards",
                table: "lifecycle_events",
                columns: new[] { "gift_card_id", "occurred_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_gift_card_lifecycle_idempotency",
                schema: "gift_cards",
                table: "lifecycle_events",
                columns: new[] { "gift_card_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_gift_card_lifecycle_ledger_transaction",
                schema: "gift_cards",
                table: "lifecycle_events",
                column: "ledger_transaction_id",
                unique: true,
                filter: "\"ledger_transaction_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_gift_card_terminal_lifecycle",
                schema: "gift_cards",
                table: "lifecycle_events",
                column: "gift_card_id",
                unique: true,
                filter: "\"action\" IN ('Cancel', 'Expire')");

            migrationBuilder.Sql(
                """
                alter table gift_cards.lifecycle_events
                    enable row level security;
                alter table gift_cards.lifecycle_events
                    force row level security;

                create policy gift_card_lifecycle_events_isolation
                    on gift_cards.lifecycle_events
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
                        or exists (
                            select 1
                            from gift_cards.gift_cards card
                            where card.id = gift_card_id
                              and card.owner_user_id =
                                  nullif(
                                      current_setting(
                                          'app.user_id',
                                          true),
                                      '')::uuid
                        )
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
                        or exists (
                            select 1
                            from gift_cards.gift_cards card
                            where card.id = gift_card_id
                              and card.owner_user_id =
                                  nullif(
                                      current_setting(
                                          'app.user_id',
                                          true),
                                      '')::uuid
                        )
                    );

                create function gift_cards.reject_lifecycle_event_mutation()
                returns trigger
                language plpgsql
                as $$
                begin
                    raise exception 'gift-card lifecycle events are append-only'
                        using errcode = '55000';
                end;
                $$;

                create trigger gift_card_lifecycle_events_append_only
                    before update or delete
                    on gift_cards.lifecycle_events
                    for each row execute function
                        gift_cards.reject_lifecycle_event_mutation();

                create function gift_cards.protect_terminal_card()
                returns trigger
                language plpgsql
                as $$
                begin
                    if old.lifecycle_state in ('Cancelled', 'Expired')
                       and new is distinct from old
                    then
                        raise exception
                            'terminal gift cards are immutable'
                            using errcode = '55000';
                    end if;

                    return new;
                end;
                $$;

                create trigger gift_cards_terminal_state_immutable
                    before update on gift_cards.gift_cards
                    for each row execute function
                        gift_cards.protect_terminal_card();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                do $$
                begin
                    if exists (
                        select 1
                        from gift_cards.gift_cards
                        where lifecycle_state in (
                            'Suspended', 'Cancelled', 'Expired')
                    )
                    then
                        raise exception
                            'cannot remove lifecycle migration while managed cards exist'
                            using errcode = '55000';
                    end if;
                end;
                $$;

                drop function if exists
                    gift_cards.reject_lifecycle_event_mutation() cascade;
                drop function if exists
                    gift_cards.protect_terminal_card() cascade;
                """);

            migrationBuilder.DropTable(
                name: "lifecycle_events",
                schema: "gift_cards");

            migrationBuilder.DropCheckConstraint(
                name: "ck_gift_cards_distribution_state",
                schema: "gift_cards",
                table: "gift_cards");

            migrationBuilder.AddCheckConstraint(
                name: "ck_gift_cards_distribution_state",
                schema: "gift_cards",
                table: "gift_cards",
                sql: """
                    ("ownership_state" = 'OrganizationInventory'
                        AND "distribution_invitation_id" IS NULL
                        AND "distributed_at_utc" IS NULL
                        AND "claimed_at_utc" IS NULL)
                    OR
                    ("ownership_state" = 'AwaitingClaim'
                        AND "lifecycle_state" = 'AwaitingClaim'
                        AND "distribution_invitation_id" IS NOT NULL
                        AND "distributed_at_utc" IS NOT NULL
                        AND "claimed_at_utc" IS NULL)
                    OR
                    ("ownership_state" = 'IdentityOwned'
                        AND "distribution_invitation_id" IS NOT NULL
                        AND "distributed_at_utc" IS NOT NULL
                        AND "claimed_at_utc" IS NOT NULL)
                    """);
        }
    }
}
