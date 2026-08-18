using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "payments");

            migrationBuilder.CreateTable(
                name: "payment_tokens",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    gift_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    funding_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_tokens", x => x.id);
                    table.CheckConstraint("ck_payment_tokens_consumption", "\"consumed_at_utc\" IS NULL OR \"consumed_at_utc\" >= \"issued_at_utc\"");
                    table.CheckConstraint("ck_payment_tokens_expiry", "\"expires_at_utc\" > \"issued_at_utc\"");
                    table.CheckConstraint("ck_payment_tokens_secret_hash", "\"secret_hash\" ~ '^[0-9A-F]{64}$'");
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_tokens_card_expiry",
                schema: "payments",
                table: "payment_tokens",
                columns: new[] { "gift_card_id", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_tokens_owner",
                schema: "payments",
                table: "payment_tokens",
                column: "owner_user_id");

            migrationBuilder.Sql(
                """
                alter table payments.payment_tokens enable row level security;
                alter table payments.payment_tokens force row level security;

                create policy payments_tokens_isolation on payments.payment_tokens
                    using (
                        coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(funding_organization_id)
                        or owner_user_id = nullif(current_setting('app.user_id', true), '')::uuid
                    )
                    with check (
                        coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(funding_organization_id)
                        or owner_user_id = nullif(current_setting('app.user_id', true), '')::uuid
                    );

                -- A payment credential's identity, binding, and validity window are
                -- fixed at issuance. Consumption is the only legitimate change, so
                -- every other column is immutable at the database rather than only
                -- in application code. Without this, widening expires_at_utc would
                -- silently extend a replay window (ADR-017).
                create function payments.protect_token_identity()
                returns trigger language plpgsql as $$
                begin
                    if new.id is distinct from old.id
                       or new.gift_card_id is distinct from old.gift_card_id
                       or new.funding_organization_id is distinct from old.funding_organization_id
                       or new.owner_user_id is distinct from old.owner_user_id
                       or new.secret_hash is distinct from old.secret_hash
                       or new.issued_at_utc is distinct from old.issued_at_utc
                       or new.expires_at_utc is distinct from old.expires_at_utc
                    then
                        raise exception 'payment token identity is immutable' using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;

                create trigger payments_token_identity_immutable
                    before update on payments.payment_tokens
                    for each row execute function payments.protect_token_identity();

                -- Single use is a financial invariant, so it is enforced by the
                -- database too: once consumed_at_utc is set it can never be
                -- cleared or moved, which is what makes a second redemption
                -- impossible rather than merely unlikely (ADR-018).
                create function payments.protect_token_consumption()
                returns trigger language plpgsql as $$
                begin
                    if old.consumed_at_utc is not null
                       and new.consumed_at_utc is distinct from old.consumed_at_utc
                    then
                        raise exception 'payment token is already consumed' using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;

                create trigger payments_token_consumption_final
                    before update on payments.payment_tokens
                    for each row execute function payments.protect_token_consumption();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop trigger if exists payments_token_consumption_final on payments.payment_tokens;
                drop trigger if exists payments_token_identity_immutable on payments.payment_tokens;
                drop function if exists payments.protect_token_consumption();
                drop function if exists payments.protect_token_identity();
                drop policy if exists payments_tokens_isolation on payments.payment_tokens;
                """);

            migrationBuilder.DropTable(
                name: "payment_tokens",
                schema: "payments");
        }
    }
}
