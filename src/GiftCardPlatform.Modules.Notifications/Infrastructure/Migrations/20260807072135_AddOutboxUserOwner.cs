using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Notifications.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxUserOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "owner_user_id",
                schema: "notifications",
                table: "outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                -- A share is between two people. The sender is a cardholder with
                -- no organization and no platform authority, so neither existing
                -- policy admitted their message and sharing by email silently
                -- queued nothing. The row now carries whoever it belongs to.
                --
                -- Exactly one owner, enforced here as well as in the domain: a
                -- row owned by nobody would be invisible to every policy, and a
                -- row owned by both would blur which one the policy checked.
                alter table notifications.outbox_messages
                    add constraint ck_outbox_messages_owner
                    check (num_nonnulls(organization_id, owner_user_id) = 1);

                create index ix_outbox_messages_owner
                    on notifications.outbox_messages (owner_user_id)
                    where owner_user_id is not null;

                -- A person may queue and read back their own messages, and
                -- nothing else. Settlement stays platform-only, exactly as it is
                -- for an organization: a sender can ask for an invitation to be
                -- sent, but cannot mark it delivered or clear its payload.
                create policy notifications_outbox_owner_read on notifications.outbox_messages
                    for select
                    using (
                        owner_user_id is not null
                        and owner_user_id = nullif(current_setting('app.user_id', true), '')::uuid
                    );

                create policy notifications_outbox_owner_enqueue on notifications.outbox_messages
                    for insert
                    with check (
                        owner_user_id is not null
                        and owner_user_id = nullif(current_setting('app.user_id', true), '')::uuid
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop policy if exists notifications_outbox_owner_enqueue
                    on notifications.outbox_messages;
                drop policy if exists notifications_outbox_owner_read
                    on notifications.outbox_messages;
                drop index if exists notifications.ix_outbox_messages_owner;
                alter table notifications.outbox_messages
                    drop constraint if exists ck_outbox_messages_owner;
                """);

            migrationBuilder.DropColumn(
                name: "owner_user_id",
                schema: "notifications",
                table: "outbox_messages");
        }
    }
}
