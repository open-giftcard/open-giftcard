using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.CorporateCredits.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCorporateCreditReversals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reversals",
                schema: "corporate_credits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    allocation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ledger_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    reason = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    reversed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reversed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reversals", x => x.id);
                    table.CheckConstraint("ck_corporate_credit_reversals_amount", "\"amount\" > 0");
                    table.CheckConstraint("ck_corporate_credit_reversals_currency", "\"currency\" ~ '^[A-Z]{3}$'");
                    table.ForeignKey(
                        name: "FK_reversals_allocations_allocation_id",
                        column: x => x.allocation_id,
                        principalSchema: "corporate_credits",
                        principalTable: "allocations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_corporate_credit_reversals_organization_reversed",
                schema: "corporate_credits",
                table: "reversals",
                columns: new[] { "organization_id", "reversed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_corporate_credit_reversals_allocation",
                schema: "corporate_credits",
                table: "reversals",
                column: "allocation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_corporate_credit_reversals_idempotency",
                schema: "corporate_credits",
                table: "reversals",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_corporate_credit_reversals_ledger_transaction",
                schema: "corporate_credits",
                table: "reversals",
                column: "ledger_transaction_id",
                unique: true);

            migrationBuilder.Sql(
                """
                alter table corporate_credits.reversals enable row level security;
                alter table corporate_credits.reversals force row level security;
                create policy corporate_credit_reversals_tenant_isolation
                    on corporate_credits.reversals
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
                name: "reversals",
                schema: "corporate_credits");
        }
    }
}
