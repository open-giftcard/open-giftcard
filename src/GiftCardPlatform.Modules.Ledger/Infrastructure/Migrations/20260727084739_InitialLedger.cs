using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Ledger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ledger");

            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.id);
                    table.CheckConstraint("ck_ledger_accounts_currency", "\"currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_ledger_accounts_scope", "(\"type\" = 'PlatformFunding' AND \"organization_id\" IS NULL)\nOR\n(\"type\" = 'OrganizationCorporateCredit' AND \"organization_id\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "transactions",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    business_reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    intent_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    initiated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    posted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transactions", x => x.id);
                    table.CheckConstraint("ck_ledger_transactions_organization", "\"organization_id\" <> '00000000-0000-0000-0000-000000000000'");
                });

            migrationBuilder.CreateTable(
                name: "entries",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entries", x => x.id);
                    table.CheckConstraint("ck_ledger_entries_amount", "\"amount\" > 0");
                    table.CheckConstraint("ck_ledger_entries_currency", "\"currency\" ~ '^[A-Z]{3}$'");
                    table.ForeignKey(
                        name: "FK_entries_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "ledger",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_entries_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalSchema: "ledger",
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_ledger_organization_account",
                schema: "ledger",
                table: "accounts",
                columns: new[] { "organization_id", "type", "currency" },
                unique: true,
                filter: "\"organization_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_ledger_platform_account",
                schema: "ledger",
                table: "accounts",
                columns: new[] { "type", "currency" },
                unique: true,
                filter: "\"organization_id\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_entries_account_id",
                schema: "ledger",
                table: "entries",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_organization_account",
                schema: "ledger",
                table: "entries",
                columns: new[] { "organization_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_transaction_direction",
                schema: "ledger",
                table: "entries",
                columns: new[] { "transaction_id", "direction" });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_transactions_organization_posted",
                schema: "ledger",
                table: "transactions",
                columns: new[] { "organization_id", "posted_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_ledger_transactions_operation_idempotency",
                schema: "ledger",
                table: "transactions",
                columns: new[] { "operation_type", "idempotency_key" },
                unique: true);

            migrationBuilder.Sql(
                """
                alter table ledger.accounts enable row level security;
                alter table ledger.accounts force row level security;
                create policy ledger_accounts_tenant_isolation on ledger.accounts
                    using (
                        current_setting('app.is_platform_operator', true) = 'true'
                        or organization_id = organizations.caller_root_organization_id()
                    )
                    with check (
                        current_setting('app.is_platform_operator', true) = 'true'
                        or organization_id = organizations.caller_root_organization_id()
                    );

                alter table ledger.transactions enable row level security;
                alter table ledger.transactions force row level security;
                create policy ledger_transactions_tenant_isolation on ledger.transactions
                    using (
                        current_setting('app.is_platform_operator', true) = 'true'
                        or organization_id = organizations.caller_root_organization_id()
                    )
                    with check (
                        current_setting('app.is_platform_operator', true) = 'true'
                        or organization_id = organizations.caller_root_organization_id()
                    );

                alter table ledger.entries enable row level security;
                alter table ledger.entries force row level security;
                create policy ledger_entries_tenant_isolation on ledger.entries
                    using (
                        current_setting('app.is_platform_operator', true) = 'true'
                        or organization_id = organizations.caller_root_organization_id()
                    )
                    with check (
                        current_setting('app.is_platform_operator', true) = 'true'
                        or organization_id = organizations.caller_root_organization_id()
                    );

                create function ledger.enforce_balanced_transaction()
                returns trigger
                language plpgsql
                as $$
                declare
                    affected_transaction_id uuid;
                begin
                    affected_transaction_id := coalesce(new.transaction_id, old.transaction_id);

                    if (
                        select count(*) < 2
                        from ledger.entries
                        where transaction_id = affected_transaction_id
                    ) then
                        raise exception 'ledger transaction requires at least two entries'
                            using errcode = '23514';
                    end if;

                    if exists (
                        select 1
                        from ledger.entries
                        where transaction_id = affected_transaction_id
                        group by currency
                        having sum(
                            case direction
                                when 'Credit' then amount
                                when 'Debit' then -amount
                                else 0
                            end) <> 0
                    ) then
                        raise exception 'ledger transaction is not balanced per currency'
                            using errcode = '23514';
                    end if;

                    if exists (
                        select 1
                        from ledger.entries entry
                        join ledger.accounts account on account.id = entry.account_id
                        join ledger.transactions ledger_transaction
                          on ledger_transaction.id = entry.transaction_id
                        where entry.transaction_id = affected_transaction_id
                          and (
                              entry.currency <> account.currency
                              or entry.organization_id <> ledger_transaction.organization_id
                              or (
                                  account.organization_id is not null
                                  and account.organization_id <> entry.organization_id
                              )
                          )
                    ) then
                        raise exception 'ledger entry does not match its transaction or account scope'
                            using errcode = '23514';
                    end if;

                    return null;
                end
                $$;

                create constraint trigger ledger_entries_must_balance
                    after insert or update or delete on ledger.entries
                    deferrable initially deferred
                    for each row execute function ledger.enforce_balanced_transaction();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop trigger if exists ledger_entries_must_balance on ledger.entries;
                drop function if exists ledger.enforce_balanced_transaction();
                """);

            migrationBuilder.DropTable(
                name: "entries",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "accounts",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "transactions",
                schema: "ledger");
        }
    }
}
