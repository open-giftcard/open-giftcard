using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.GiftCards.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialGiftCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "gift_cards");

            migrationBuilder.CreateTable(
                name: "gift_cards",
                schema: "gift_cards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    funding_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuing_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ownership_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    lifecycle_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ledger_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuance_ledger_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    initial_value = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    valid_from_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_transferable = table.Column<bool>(type: "boolean", nullable: false),
                    is_divisible = table.Column<bool>(type: "boolean", nullable: false),
                    source_gift_card_id = table.Column<Guid>(type: "uuid", nullable: true),
                    root_gift_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    business_reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    issued_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_by_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gift_cards", x => x.id);
                    table.CheckConstraint("ck_gift_cards_amount", "\"initial_value\" > 0");
                    table.CheckConstraint("ck_gift_cards_currency", "\"currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_gift_cards_ownership", "(\"ownership_state\" = 'OrganizationInventory'\n    AND \"owner_organization_id\" IS NOT NULL\n    AND \"owner_user_id\" IS NULL)\nOR\n(\"ownership_state\" = 'AwaitingClaim'\n    AND \"owner_organization_id\" IS NULL\n    AND \"owner_user_id\" IS NULL)\nOR\n(\"ownership_state\" = 'IdentityOwned'\n    AND \"owner_organization_id\" IS NULL\n    AND \"owner_user_id\" IS NOT NULL)");
                    table.CheckConstraint("ck_gift_cards_provenance", "(\"generation\" = 0\n    AND \"source_gift_card_id\" IS NULL\n    AND \"root_gift_card_id\" = \"id\")\nOR\n(\"generation\" > 0\n    AND \"source_gift_card_id\" IS NOT NULL)");
                    table.CheckConstraint("ck_gift_cards_validity", "\"expires_at_utc\" > \"valid_from_utc\"");
                    table.ForeignKey(
                        name: "FK_gift_cards_gift_cards_source_gift_card_id",
                        column: x => x.source_gift_card_id,
                        principalSchema: "gift_cards",
                        principalTable: "gift_cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_gift_cards_identity_owner",
                schema: "gift_cards",
                table: "gift_cards",
                columns: new[] { "owner_user_id", "issued_at_utc", "id" },
                filter: "\"owner_user_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_gift_cards_organization_inventory",
                schema: "gift_cards",
                table: "gift_cards",
                columns: new[] { "owner_organization_id", "ownership_state", "issued_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_gift_cards_root_generation",
                schema: "gift_cards",
                table: "gift_cards",
                columns: new[] { "root_gift_card_id", "generation" });

            migrationBuilder.CreateIndex(
                name: "ix_gift_cards_source",
                schema: "gift_cards",
                table: "gift_cards",
                column: "source_gift_card_id",
                filter: "\"source_gift_card_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_gift_cards_issuance_transaction",
                schema: "gift_cards",
                table: "gift_cards",
                column: "issuance_ledger_transaction_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_gift_cards_ledger_account",
                schema: "gift_cards",
                table: "gift_cards",
                column: "ledger_account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_gift_cards_public_reference",
                schema: "gift_cards",
                table: "gift_cards",
                column: "public_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_gift_cards_tenant_idempotency",
                schema: "gift_cards",
                table: "gift_cards",
                columns: new[] { "funding_organization_id", "idempotency_key" },
                unique: true);

            migrationBuilder.Sql(
                """
                alter table gift_cards.gift_cards enable row level security;
                alter table gift_cards.gift_cards force row level security;

                create policy gift_cards_tenant_and_owner_isolation
                    on gift_cards.gift_cards
                    using (
                        coalesce(
                            nullif(
                                current_setting('app.is_platform_operator', true),
                                ''),
                            'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(
                            funding_organization_id)
                        or (
                            owner_user_id is not null
                            and owner_user_id =
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
                        or organizations.organization_belongs_to_caller_tenant(
                            funding_organization_id)
                        or (
                            owner_user_id is not null
                            and owner_user_id =
                                nullif(
                                    current_setting('app.user_id', true),
                                    '')::uuid
                        )
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop policy if exists gift_cards_tenant_and_owner_isolation
                    on gift_cards.gift_cards;
                """);

            migrationBuilder.DropTable(
                name: "gift_cards",
                schema: "gift_cards");
        }
    }
}
