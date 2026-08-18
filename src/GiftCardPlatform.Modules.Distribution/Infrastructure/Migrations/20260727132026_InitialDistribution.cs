using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Distribution.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialDistribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "distribution");

            migrationBuilder.CreateTable(
                name: "events",
                schema: "distribution",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    funding_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invitation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gift_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_membership_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "invitations",
                schema: "distribution",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    funding_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuing_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gift_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    recipient_contact = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    masked_recipient_contact = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    claim_secret_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    claim_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    failed_claim_attempts = table.Column<int>(type: "integer", nullable: false),
                    business_reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    distributed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    distributed_by_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    distributed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claim_idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    identity_was_created_on_claim = table.Column<bool>(type: "boolean", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitations", x => x.id);
                    table.CheckConstraint("ck_distribution_invitations_contact_type", "\"contact_type\" in ('Email', 'Phone')");
                    table.CheckConstraint("ck_distribution_invitations_expiry", "\"claim_expires_at_utc\" > \"distributed_at_utc\"");
                    table.CheckConstraint("ck_distribution_invitations_failed_attempts", "\"failed_claim_attempts\" >= 0");
                    table.CheckConstraint("ck_distribution_invitations_state", "(\"state\" = 'Pending'\n    AND \"claimed_by_user_id\" IS NULL\n    AND \"claimed_at_utc\" IS NULL\n    AND \"claim_idempotency_key\" IS NULL\n    AND \"identity_was_created_on_claim\" IS NULL)\nOR\n(\"state\" = 'Claimed'\n    AND \"claimed_by_user_id\" IS NOT NULL\n    AND \"claimed_at_utc\" IS NOT NULL\n    AND \"claim_idempotency_key\" IS NOT NULL\n    AND \"identity_was_created_on_claim\" IS NOT NULL)\nOR\n(\"state\" IN ('Locked', 'Expired')\n    AND \"claimed_by_user_id\" IS NULL\n    AND \"claimed_at_utc\" IS NULL\n    AND \"claim_idempotency_key\" IS NULL\n    AND \"identity_was_created_on_claim\" IS NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_distribution_events_card_history",
                schema: "distribution",
                table: "events",
                columns: new[] { "gift_card_id", "occurred_at_utc", "id" });

            migrationBuilder.AddForeignKey(
                name: "FK_events_invitations_invitation_id",
                schema: "distribution",
                table: "events",
                column: "invitation_id",
                principalSchema: "distribution",
                principalTable: "invitations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateIndex(
                name: "ix_distribution_events_invitation_history",
                schema: "distribution",
                table: "events",
                columns: new[] { "invitation_id", "occurred_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_distribution_identity_history",
                schema: "distribution",
                table: "invitations",
                columns: new[] { "claimed_by_user_id", "claimed_at_utc" },
                filter: "\"claimed_by_user_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_distribution_organization_history",
                schema: "distribution",
                table: "invitations",
                columns: new[] { "issuing_organization_id", "distributed_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_distribution_gift_card",
                schema: "distribution",
                table: "invitations",
                column: "gift_card_id",
                unique: true,
                filter: "\"state\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ux_distribution_tenant_idempotency",
                schema: "distribution",
                table: "invitations",
                columns: new[] { "funding_organization_id", "idempotency_key" },
                unique: true);

            migrationBuilder.Sql(
                """
                alter table distribution.invitations enable row level security;
                alter table distribution.invitations force row level security;
                alter table distribution.events enable row level security;
                alter table distribution.events force row level security;

                create policy distribution_invitations_isolation
                    on distribution.invitations
                    using (
                        coalesce(
                            nullif(
                                current_setting('app.is_platform_operator', true),
                                ''),
                            'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(
                            funding_organization_id)
                        or (
                            claimed_by_user_id is not null
                            and claimed_by_user_id =
                                nullif(
                                    current_setting('app.user_id', true),
                                    '')::uuid
                        )
                        or id =
                            nullif(
                                current_setting(
                                    'app.claim_invitation_id',
                                    true),
                                '')::uuid
                    )
                    with check (
                        coalesce(
                            nullif(
                                current_setting('app.is_platform_operator', true),
                                ''),
                            'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(
                            funding_organization_id)
                        or (
                            claimed_by_user_id is not null
                            and claimed_by_user_id =
                                nullif(
                                    current_setting('app.user_id', true),
                                    '')::uuid
                        )
                        or id =
                            nullif(
                                current_setting(
                                    'app.claim_invitation_id',
                                    true),
                                '')::uuid
                    );

                create policy distribution_events_isolation
                    on distribution.events
                    using (
                        coalesce(
                            nullif(
                                current_setting('app.is_platform_operator', true),
                                ''),
                            'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(
                            funding_organization_id)
                        or (
                            actor_user_id is not null
                            and actor_user_id =
                                nullif(
                                    current_setting('app.user_id', true),
                                    '')::uuid
                        )
                        or invitation_id =
                            nullif(
                                current_setting(
                                    'app.claim_invitation_id',
                                    true),
                                '')::uuid
                    )
                    with check (
                        coalesce(
                            nullif(
                                current_setting('app.is_platform_operator', true),
                                ''),
                            'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(
                            funding_organization_id)
                        or (
                            actor_user_id is not null
                            and actor_user_id =
                                nullif(
                                    current_setting('app.user_id', true),
                                    '')::uuid
                        )
                        or invitation_id =
                            nullif(
                                current_setting(
                                    'app.claim_invitation_id',
                                    true),
                                '')::uuid
                    );

                create function distribution.reject_event_mutation()
                returns trigger
                language plpgsql
                as $$
                begin
                    raise exception 'distribution events are append-only'
                        using errcode = '55000';
                end;
                $$;

                create trigger distribution_events_append_only
                    before update or delete on distribution.events
                    for each row execute function
                        distribution.reject_event_mutation();

                create function distribution.protect_invitation_identity()
                returns trigger
                language plpgsql
                as $$
                begin
                    if new.id is distinct from old.id
                       or new.funding_organization_id is distinct from
                          old.funding_organization_id
                       or new.issuing_organization_id is distinct from
                          old.issuing_organization_id
                       or new.gift_card_id is distinct from old.gift_card_id
                       or new.contact_type is distinct from old.contact_type
                       or new.recipient_contact is distinct from
                          old.recipient_contact
                       or new.masked_recipient_contact is distinct from
                          old.masked_recipient_contact
                       or new.claim_secret_hash is distinct from
                          old.claim_secret_hash
                       or new.claim_expires_at_utc is distinct from
                          old.claim_expires_at_utc
                       or new.business_reference is distinct from
                          old.business_reference
                       or new.idempotency_key is distinct from
                          old.idempotency_key
                       or new.distributed_by_user_id is distinct from
                          old.distributed_by_user_id
                       or new.distributed_by_membership_id is distinct from
                          old.distributed_by_membership_id
                       or new.distributed_at_utc is distinct from
                          old.distributed_at_utc
                    then
                        raise exception
                            'distribution invitation identity is immutable'
                            using errcode = '55000';
                    end if;

                    return new;
                end;
                $$;

                create trigger distribution_invitation_identity_immutable
                    before update on distribution.invitations
                    for each row execute function
                        distribution.protect_invitation_identity();

                drop policy if exists audit_records_tenant_isolation
                    on audit.audit_records;

                create policy audit_records_tenant_and_cardholder_isolation
                    on audit.audit_records
                    using (
                        coalesce(
                            nullif(
                                current_setting('app.is_platform_operator', true),
                                ''),
                            'false')::boolean
                        or (
                            organization_scope_id is not null
                            and organizations.organization_belongs_to_caller_tenant(
                                organization_scope_id)
                        )
                        or (
                            organization_scope_id is null
                            and actor_user_id =
                                nullif(
                                    current_setting('app.user_id', true),
                                    '')::uuid
                        )
                        or (
                            organization_scope_id is not null
                            and actor_type = 'IdentityUser'
                            and actor_user_id =
                                nullif(
                                    current_setting('app.user_id', true),
                                    '')::uuid
                            and exists (
                                select 1
                                from gift_cards.gift_cards card
                                where card.owner_user_id = actor_user_id
                                  and card.funding_organization_id =
                                      organization_scope_id
                            )
                        )
                    )
                    with check (
                        coalesce(
                            nullif(
                                current_setting('app.is_platform_operator', true),
                                ''),
                            'false')::boolean
                        or organization_scope_id is null
                        or organizations.organization_belongs_to_caller_tenant(
                            organization_scope_id)
                        or (
                            actor_type = 'IdentityUser'
                            and actor_user_id =
                                nullif(
                                    current_setting('app.user_id', true),
                                    '')::uuid
                            and exists (
                                select 1
                                from gift_cards.gift_cards card
                                where card.owner_user_id = actor_user_id
                                  and card.funding_organization_id =
                                      organization_scope_id
                            )
                        )
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop policy if exists
                    audit_records_tenant_and_cardholder_isolation
                    on audit.audit_records;

                create policy audit_records_tenant_isolation
                    on audit.audit_records
                    using (
                        coalesce(
                            nullif(
                                current_setting('app.is_platform_operator', true),
                                ''),
                            'false')::boolean
                        or (
                            organization_scope_id is not null
                            and organizations.organization_belongs_to_caller_tenant(
                                organization_scope_id)
                        )
                        or (
                            organization_scope_id is null
                            and actor_user_id =
                                nullif(
                                    current_setting('app.user_id', true),
                                    '')::uuid
                        )
                    )
                    with check (
                        coalesce(
                            nullif(
                                current_setting('app.is_platform_operator', true),
                                ''),
                            'false')::boolean
                        or organization_scope_id is null
                        or organizations.organization_belongs_to_caller_tenant(
                            organization_scope_id)
                    );

                drop function if exists
                    distribution.reject_event_mutation() cascade;
                drop function if exists
                    distribution.protect_invitation_identity() cascade;
                """);

            migrationBuilder.DropTable(
                name: "events",
                schema: "distribution");

            migrationBuilder.DropTable(
                name: "invitations",
                schema: "distribution");
        }
    }
}
