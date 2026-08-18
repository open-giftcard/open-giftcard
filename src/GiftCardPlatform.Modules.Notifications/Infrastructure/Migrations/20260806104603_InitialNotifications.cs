using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Notifications.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notifications");

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    protected_recipient = table.Column<string>(type: "text", nullable: false),
                    masked_recipient = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    protected_body = table.Column<string>(type: "text", nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_failure_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_outbox_messages_attempts", "\"attempt_count\" >= 0");
                    table.CheckConstraint("ck_outbox_messages_payload_lifetime", "(\"state\" = 'Pending') = (\"protected_body\" IS NOT NULL)");
                    table.CheckConstraint("ck_outbox_messages_settlement", "(\"state\" = 'Pending') = (\"settled_at_utc\" IS NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_due",
                schema: "notifications",
                table: "outbox_messages",
                columns: new[] { "state", "next_attempt_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_outbox_messages_idempotency_key",
                schema: "notifications",
                table: "outbox_messages",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.Sql(
                """
                alter table notifications.outbox_messages enable row level security;
                alter table notifications.outbox_messages force row level security;

                -- The dispatcher settles messages and must see every tenant's
                -- queue, so it gets the full policy through the controlled
                -- platform path.
                create policy notifications_outbox_platform on notifications.outbox_messages
                    using (
                        coalesce(
                            nullif(current_setting('app.is_platform_operator', true), ''),
                            'false')::boolean
                    )
                    with check (
                        coalesce(
                            nullif(current_setting('app.is_platform_operator', true), ''),
                            'false')::boolean
                    );

                -- Enqueue happens inside the distributing organization's own
                -- transaction, which has no platform authority. It may queue and
                -- read back messages scoped to its own tenant, and nothing else.
                -- Settlement stays platform-only: a tenant can ask for a message
                -- to be sent, but cannot mark one delivered or clear its payload.
                create policy notifications_outbox_tenant_read on notifications.outbox_messages
                    for select
                    using (
                        organization_id is not null
                        and organizations.organization_belongs_to_caller_tenant(organization_id)
                    );

                create policy notifications_outbox_tenant_enqueue on notifications.outbox_messages
                    for insert
                    with check (
                        organization_id is not null
                        and organizations.organization_belongs_to_caller_tenant(organization_id)
                    );

                -- Identity, destination, and purpose are fixed at enqueue. Only
                -- delivery progress may move, so a queued message cannot be
                -- repointed at another recipient after the fact.
                create function notifications.protect_outbox_identity()
                returns trigger language plpgsql as $$
                begin
                    if new.id is distinct from old.id
                       or new.kind is distinct from old.kind
                       or new.channel is distinct from old.channel
                       or new.masked_recipient is distinct from old.masked_recipient
                       or new.subject is distinct from old.subject
                       or new.idempotency_key is distinct from old.idempotency_key
                       or new.organization_id is distinct from old.organization_id
                       or new.created_at_utc is distinct from old.created_at_utc
                       or new.expires_at_utc is distinct from old.expires_at_utc
                    then
                        raise exception 'notification identity is immutable' using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;

                create trigger notifications_outbox_identity_immutable
                    before update on notifications.outbox_messages
                    for each row execute function notifications.protect_outbox_identity();

                -- Pending is the only state a message may leave, and a settled
                -- message must never regain a credential. Without this, a defect
                -- could resurrect a delivered activation link.
                create function notifications.protect_outbox_settlement()
                returns trigger language plpgsql as $$
                begin
                    if old.state <> 'Pending' then
                        raise exception 'notification is already settled' using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;

                create trigger notifications_outbox_settlement_final
                    before update on notifications.outbox_messages
                    for each row execute function notifications.protect_outbox_settlement();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop trigger if exists notifications_outbox_settlement_final
                    on notifications.outbox_messages;
                drop trigger if exists notifications_outbox_identity_immutable
                    on notifications.outbox_messages;
                drop function if exists notifications.protect_outbox_settlement();
                drop function if exists notifications.protect_outbox_identity();
                drop policy if exists notifications_outbox_tenant_enqueue
                    on notifications.outbox_messages;
                drop policy if exists notifications_outbox_tenant_read
                    on notifications.outbox_messages;
                drop policy if exists notifications_outbox_platform
                    on notifications.outbox_messages;
                """);

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "notifications");
        }
    }
}
