using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_refunds",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_provision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    redemption_ledger_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    refund_ledger_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    funding_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gift_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gift_card_public_reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    pos_client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pos_terminal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    pos_transaction_reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(20,4)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    refunded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_refunds", x => x.id);
                    table.CheckConstraint("ck_payment_refunds_amount", "\"amount\" > 0");
                    table.CheckConstraint("ck_payment_refunds_currency", "\"currency\" ~ '^[A-Z]{3}$'");
                    table.ForeignKey(
                        name: "FK_payment_refunds_payment_provisions_payment_provision_id",
                        column: x => x.payment_provision_id,
                        principalSchema: "payments",
                        principalTable: "payment_provisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_card_refunded",
                schema: "payments",
                table: "payment_refunds",
                columns: new[] { "gift_card_id", "refunded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_payment_refunds_ledger_transaction",
                schema: "payments",
                table: "payment_refunds",
                column: "refund_ledger_transaction_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_payment_refunds_provision_idempotency",
                schema: "payments",
                table: "payment_refunds",
                columns: new[] { "payment_provision_id", "idempotency_key" },
                unique: true);

            migrationBuilder.Sql(
                """
                alter table payments.payment_refunds enable row level security;
                alter table payments.payment_refunds force row level security;

                create policy payments_refunds_read on payments.payment_refunds
                    for select
                    using (
                        coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(funding_organization_id)
                        or pos_client_id = nullif(current_setting('app.pos_client_id', true), '')::uuid
                    );

                create policy payments_refunds_insert on payments.payment_refunds
                    for insert
                    with check (
                        pos_client_id = nullif(current_setting('app.pos_client_id', true), '')::uuid
                        and exists (
                            select 1
                            from payments.payment_provisions provision
                            where provision.id = payment_refunds.payment_provision_id
                              and provision.payment_token_id = nullif(
                                  current_setting('app.payment_token_id', true), '')::uuid
                              and provision.pos_client_id = payment_refunds.pos_client_id
                              and provision.state = 'Confirmed'
                        )
                    );

                -- This is the database backstop for partial-refund safety. It
                -- takes the same provision advisory lock as the application,
                -- verifies the immutable snapshot against the confirmed sale,
                -- and serializes the cumulative cap even for raw SQL writers.
                create function payments.validate_payment_refund()
                returns trigger language plpgsql as $$
                declare
                    provision payments.payment_provisions%rowtype;
                    already_refunded numeric(20,4);
                begin
                    perform pg_advisory_xact_lock(hashtextextended(
                        'payment-provision|' || new.payment_provision_id::text, 0));
                    select * into provision
                    from payments.payment_provisions
                    where id = new.payment_provision_id;

                    if provision.id is null
                       or provision.state <> 'Confirmed'
                       or provision.confirmed_amount is null
                       or provision.redemption_ledger_transaction_id is null
                       or new.redemption_ledger_transaction_id <> provision.redemption_ledger_transaction_id
                       or new.funding_organization_id <> provision.funding_organization_id
                       or new.gift_card_id <> provision.gift_card_id
                       or new.gift_card_public_reference <> provision.gift_card_public_reference
                       or new.pos_client_id <> provision.pos_client_id
                       or new.currency <> provision.currency
                    then
                        raise exception 'payment refund does not match confirmed provision'
                            using errcode = '23514';
                    end if;

                    select coalesce(sum(amount), 0) into already_refunded
                    from payments.payment_refunds
                    where payment_provision_id = new.payment_provision_id;
                    if already_refunded + new.amount > provision.confirmed_amount then
                        raise exception 'payment refund exceeds confirmed amount'
                            using errcode = '23514';
                    end if;
                    return new;
                end;
                $$;

                create trigger payment_refunds_validate
                    before insert on payments.payment_refunds
                    for each row execute function payments.validate_payment_refund();

                create function payments.refuse_payment_refund_mutation()
                returns trigger language plpgsql as $$
                begin
                    raise exception 'payment refunds are immutable' using errcode = '55000';
                end;
                $$;

                create trigger payment_refunds_immutable
                    before update or delete on payments.payment_refunds
                    for each row execute function payments.refuse_payment_refund_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop trigger if exists payment_refunds_immutable on payments.payment_refunds;
                drop trigger if exists payment_refunds_validate on payments.payment_refunds;
                drop function if exists payments.refuse_payment_refund_mutation();
                drop function if exists payments.validate_payment_refund();
                drop policy if exists payments_refunds_insert on payments.payment_refunds;
                drop policy if exists payments_refunds_read on payments.payment_refunds;
                """);

            migrationBuilder.DropTable(
                name: "payment_refunds",
                schema: "payments");
        }
    }
}
