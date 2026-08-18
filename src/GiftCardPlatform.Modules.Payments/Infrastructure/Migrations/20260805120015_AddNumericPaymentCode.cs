using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNumericPaymentCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "numeric_code_hash",
                schema: "payments",
                table: "payment_tokens",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_payment_tokens_numeric_code_hash",
                schema: "payments",
                table: "payment_tokens",
                column: "numeric_code_hash",
                unique: true,
                filter: "\"numeric_code_hash\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_tokens_numeric_code_hash",
                schema: "payments",
                table: "payment_tokens",
                sql: "\"numeric_code_hash\" IS NULL OR \"numeric_code_hash\" ~ '^[0-9A-F]{64}$'");

            migrationBuilder.Sql(
                """
                -- A numeric candidate grants SELECT of exactly the token row
                -- whose hash the server derived from a valid 12-digit input.
                -- It grants no write; consumption later uses the existing
                -- server-resolved token-ID candidate (ADR-050).
                create policy payments_tokens_numeric_candidate
                    on payments.payment_tokens for select
                    using (
                        numeric_code_hash is not null
                        and numeric_code_hash = nullif(
                            current_setting('app.payment_code_hash', true), '')
                    );

                create or replace function payments.protect_token_identity()
                returns trigger language plpgsql as $$
                begin
                    if new.id is distinct from old.id
                       or new.gift_card_id is distinct from old.gift_card_id
                       or new.funding_organization_id is distinct from old.funding_organization_id
                       or new.owner_user_id is distinct from old.owner_user_id
                       or new.secret_hash is distinct from old.secret_hash
                       or new.numeric_code_hash is distinct from old.numeric_code_hash
                       or new.issued_at_utc is distinct from old.issued_at_utc
                       or new.expires_at_utc is distinct from old.expires_at_utc
                    then
                        raise exception 'payment token identity is immutable' using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop policy if exists payments_tokens_numeric_candidate
                    on payments.payment_tokens;

                create or replace function payments.protect_token_identity()
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
                """);

            migrationBuilder.DropIndex(
                name: "ux_payment_tokens_numeric_code_hash",
                schema: "payments",
                table: "payment_tokens");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_tokens_numeric_code_hash",
                schema: "payments",
                table: "payment_tokens");

            migrationBuilder.DropColumn(
                name: "numeric_code_hash",
                schema: "payments",
                table: "payment_tokens");
        }
    }
}
