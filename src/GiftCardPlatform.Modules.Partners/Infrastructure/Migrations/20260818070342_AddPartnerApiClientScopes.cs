using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Partners.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPartnerApiClientScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "scopes",
                schema: "partners",
                table: "api_clients",
                type: "text[]",
                nullable: false,
                defaultValue: Array.Empty<string>());

            // Every client registered before this migration could only mint,
            // because minting was the only capability that existed. Backfilling
            // that scope is therefore a statement of what those keys already
            // did, not a guess, and it has to happen before the constraint is
            // added or each existing row would violate it on creation.
            migrationBuilder.Sql(
                """
                update partners.api_clients
                set scopes = array['partner.gift_cards.mint']
                where cardinality(scopes) = 0;
                """);

            // cardinality rather than array_length: array_length returns NULL for
            // an empty array, and a CHECK constraint is satisfied when its
            // expression is NULL, so the obvious spelling would let exactly the
            // rows it is meant to reject straight through.
            migrationBuilder.AddCheckConstraint(
                name: "ck_partner_api_clients_scopes",
                schema: "partners",
                table: "api_clients",
                sql: "cardinality(\"scopes\") >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_partner_api_clients_scopes",
                schema: "partners",
                table: "api_clients");

            migrationBuilder.DropColumn(
                name: "scopes",
                schema: "partners",
                table: "api_clients");
        }
    }
}
