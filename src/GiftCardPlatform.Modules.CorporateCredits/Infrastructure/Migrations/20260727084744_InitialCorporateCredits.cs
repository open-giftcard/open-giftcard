using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.CorporateCredits.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCorporateCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "corporate_credits");

            migrationBuilder.CreateTable(
                name: "allocations",
                schema: "corporate_credits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ledger_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    business_reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    allocated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    allocated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_allocations", x => x.id);
                    table.CheckConstraint("ck_corporate_credit_allocations_amount", "\"amount\" > 0");
                    table.CheckConstraint("ck_corporate_credit_allocations_currency", "\"currency\" ~ '^[A-Z]{3}$'");
                });

            migrationBuilder.CreateIndex(
                name: "ix_corporate_credit_allocations_organization_allocated",
                schema: "corporate_credits",
                table: "allocations",
                columns: new[] { "organization_id", "allocated_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_corporate_credit_allocations_idempotency",
                schema: "corporate_credits",
                table: "allocations",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_corporate_credit_allocations_ledger_transaction",
                schema: "corporate_credits",
                table: "allocations",
                column: "ledger_transaction_id",
                unique: true);

            migrationBuilder.Sql(
                """
                alter table corporate_credits.allocations enable row level security;
                alter table corporate_credits.allocations force row level security;
                create policy corporate_credit_allocations_tenant_isolation
                    on corporate_credits.allocations
                    using (
                        current_setting('app.is_platform_operator', true) = 'true'
                        or organization_id = organizations.caller_root_organization_id()
                    )
                    with check (
                        current_setting('app.is_platform_operator', true) = 'true'
                        or organization_id = organizations.caller_root_organization_id()
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "allocations",
                schema: "corporate_credits");
        }
    }
}
