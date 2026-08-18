using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentProvisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_provisions",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gift_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    funding_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pos_client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pos_terminal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    pos_transaction_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(20,4)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_provisions", x => x.id);
                    table.CheckConstraint("ck_payment_provisions_amount", "\"amount\" > 0 AND \"amount\" <= 1000000000");
                    table.CheckConstraint("ck_payment_provisions_currency", "\"currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_payment_provisions_settlement", "(\"state\" = 'Active') = (\"settled_at_utc\" IS NULL)");
                    table.CheckConstraint("ck_payment_provisions_window", "\"expires_at_utc\" > \"created_at_utc\"");
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_provisions_card_state",
                schema: "payments",
                table: "payment_provisions",
                columns: new[] { "gift_card_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_provisions_due",
                schema: "payments",
                table: "payment_provisions",
                columns: new[] { "state", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_payment_provisions_token",
                schema: "payments",
                table: "payment_provisions",
                column: "payment_token_id",
                unique: true);

            migrationBuilder.Sql(
                """
                alter table payments.payment_provisions enable row level security;
                alter table payments.payment_provisions force row level security;

                create policy payments_provisions_isolation on payments.payment_provisions
                    using (
                        coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(funding_organization_id)
                        or owner_user_id = nullif(current_setting('app.user_id', true), '')::uuid
                        or pos_client_id = nullif(current_setting('app.pos_client_id', true), '')::uuid
                        or payment_token_id = nullif(current_setting('app.payment_token_id', true), '')::uuid
                    )
                    with check (
                        coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(funding_organization_id)
                        or owner_user_id = nullif(current_setting('app.user_id', true), '')::uuid
                        or pos_client_id = nullif(current_setting('app.pos_client_id', true), '')::uuid
                        or payment_token_id = nullif(current_setting('app.payment_token_id', true), '')::uuid
                    );

                -- What a hold is against, for whom, and for how long is fixed at
                -- creation. Only the state and its settlement stamp may move, so
                -- a hold cannot be quietly repointed at another card or widened
                -- past its ADR-044 window.
                create function payments.protect_provision_identity()
                returns trigger language plpgsql as $$
                begin
                    if new.id is distinct from old.id
                       or new.payment_token_id is distinct from old.payment_token_id
                       or new.gift_card_id is distinct from old.gift_card_id
                       or new.funding_organization_id is distinct from old.funding_organization_id
                       or new.owner_user_id is distinct from old.owner_user_id
                       or new.pos_client_id is distinct from old.pos_client_id
                       or new.pos_terminal_id is distinct from old.pos_terminal_id
                       or new.amount is distinct from old.amount
                       or new.currency is distinct from old.currency
                       or new.created_at_utc is distinct from old.created_at_utc
                       or new.expires_at_utc is distinct from old.expires_at_utc
                    then
                        raise exception 'payment provision identity is immutable' using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;

                create trigger payments_provision_identity_immutable
                    before update on payments.payment_provisions
                    for each row execute function payments.protect_provision_identity();

                -- Active is the only state a provision may leave. Once settled it
                -- is terminal, so a released hold can never be revived to reserve
                -- value a second time.
                create function payments.protect_provision_settlement()
                returns trigger language plpgsql as $$
                begin
                    if old.state <> 'Active' and new.state is distinct from old.state then
                        raise exception 'payment provision is already settled' using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;

                create trigger payments_provision_settlement_final
                    before update on payments.payment_provisions
                    for each row execute function payments.protect_provision_settlement();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop trigger if exists payments_provision_settlement_final on payments.payment_provisions;
                drop trigger if exists payments_provision_identity_immutable on payments.payment_provisions;
                drop function if exists payments.protect_provision_settlement();
                drop function if exists payments.protect_provision_identity();
                drop policy if exists payments_provisions_isolation on payments.payment_provisions;
                """);

            migrationBuilder.DropTable(
                name: "payment_provisions",
                schema: "payments");
        }
    }
}
