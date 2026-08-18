using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Sharing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sharing");

            migrationBuilder.CreateTable(
                name: "shares",
                schema: "sharing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_gift_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    funding_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claimed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    child_gift_card_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ledger_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    claim_secret_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    pin_hash = table.Column<string>(type: "character varying(104)", maxLength: 104, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    failed_pin_attempts = table.Column<int>(type: "integer", nullable: false),
                    create_idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    claim_idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    cancel_idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shares", x => x.id);
                    table.CheckConstraint("ck_sharing_amount", "\"amount\" > 0");
                    table.CheckConstraint("ck_sharing_currency", "\"currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_sharing_expiry", "\"expires_at_utc\" > \"created_at_utc\"");
                    table.CheckConstraint("ck_sharing_failed_attempts", "\"failed_pin_attempts\" >= 0");
                    table.CheckConstraint("ck_sharing_state", "(\"state\" = 'Pending'\n    AND \"claimed_by_user_id\" IS NULL\n    AND \"child_gift_card_id\" IS NULL\n    AND \"ledger_transaction_id\" IS NULL\n    AND \"claim_idempotency_key\" IS NULL\n    AND \"claimed_at_utc\" IS NULL\n    AND \"closed_at_utc\" IS NULL)\nOR\n(\"state\" = 'Claiming'\n    AND \"claimed_by_user_id\" IS NOT NULL\n    AND \"child_gift_card_id\" IS NOT NULL\n    AND \"ledger_transaction_id\" IS NOT NULL\n    AND \"claim_idempotency_key\" IS NOT NULL\n    AND \"claimed_at_utc\" IS NULL\n    AND \"closed_at_utc\" IS NULL)\nOR\n(\"state\" = 'Claimed'\n    AND \"claimed_by_user_id\" IS NOT NULL\n    AND \"child_gift_card_id\" IS NOT NULL\n    AND \"ledger_transaction_id\" IS NOT NULL\n    AND \"claim_idempotency_key\" IS NOT NULL\n    AND \"claimed_at_utc\" IS NOT NULL\n    AND \"closed_at_utc\" IS NOT NULL)\nOR\n(\"state\" IN ('Cancelled', 'Expired', 'Locked')\n    AND \"claimed_by_user_id\" IS NULL\n    AND \"child_gift_card_id\" IS NULL\n    AND \"ledger_transaction_id\" IS NULL\n    AND \"claim_idempotency_key\" IS NULL\n    AND \"claimed_at_utc\" IS NULL\n    AND \"closed_at_utc\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "events",
                schema: "sharing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    share_id = table.Column<Guid>(type: "uuid", nullable: false),
                    funding_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_events_shares_share_id",
                        column: x => x.share_id,
                        principalSchema: "sharing",
                        principalTable: "shares",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sharing_events_history",
                schema: "sharing",
                table: "events",
                columns: new[] { "share_id", "occurred_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_sharing_expiration",
                schema: "sharing",
                table: "shares",
                columns: new[] { "state", "expires_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_sharing_recipient_history",
                schema: "sharing",
                table: "shares",
                columns: new[] { "claimed_by_user_id", "claimed_at_utc", "id" },
                filter: "\"claimed_by_user_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sharing_sender_history",
                schema: "sharing",
                table: "shares",
                columns: new[] { "sender_user_id", "created_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_sharing_source_active",
                schema: "sharing",
                table: "shares",
                columns: new[] { "source_gift_card_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ux_sharing_child_card",
                schema: "sharing",
                table: "shares",
                column: "child_gift_card_id",
                unique: true,
                filter: "\"child_gift_card_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_sharing_ledger_transaction",
                schema: "sharing",
                table: "shares",
                column: "ledger_transaction_id",
                unique: true,
                filter: "\"ledger_transaction_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_sharing_sender_idempotency",
                schema: "sharing",
                table: "shares",
                columns: new[] { "sender_user_id", "create_idempotency_key" },
                unique: true);

            migrationBuilder.Sql(
                """
                alter table sharing.shares enable row level security;
                alter table sharing.shares force row level security;
                alter table sharing.events enable row level security;
                alter table sharing.events force row level security;

                create policy sharing_shares_isolation on sharing.shares
                    using (
                        coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(funding_organization_id)
                        or sender_user_id = nullif(current_setting('app.user_id', true), '')::uuid
                        or claimed_by_user_id = nullif(current_setting('app.user_id', true), '')::uuid
                        or id = nullif(current_setting('app.share_id', true), '')::uuid
                    )
                    with check (
                        coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(funding_organization_id)
                        or sender_user_id = nullif(current_setting('app.user_id', true), '')::uuid
                        or claimed_by_user_id = nullif(current_setting('app.user_id', true), '')::uuid
                        or id = nullif(current_setting('app.share_id', true), '')::uuid
                    );

                create policy sharing_events_isolation on sharing.events
                    using (
                        coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(funding_organization_id)
                        or exists (
                            select 1 from sharing.shares share
                            where share.id = sharing.events.share_id
                        )
                    )
                    with check (
                        coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(funding_organization_id)
                        or exists (
                            select 1 from sharing.shares share
                            where share.id = sharing.events.share_id
                        )
                    );

                create function sharing.reject_event_mutation()
                returns trigger language plpgsql as $$
                begin
                    raise exception 'sharing events are append-only' using errcode = '55000';
                end;
                $$;

                create trigger sharing_events_append_only
                    before update or delete on sharing.events
                    for each row execute function sharing.reject_event_mutation();

                create function sharing.protect_share_identity()
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

                create trigger sharing_share_identity_immutable
                    before update on sharing.shares
                    for each row execute function sharing.protect_share_identity();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop trigger if exists sharing_share_identity_immutable on sharing.shares;
                drop function if exists sharing.protect_share_identity();
                drop trigger if exists sharing_events_append_only on sharing.events;
                drop function if exists sharing.reject_event_mutation();
                """);

            migrationBuilder.DropTable(
                name: "events",
                schema: "sharing");

            migrationBuilder.DropTable(
                name: "shares",
                schema: "sharing");
        }
    }
}
