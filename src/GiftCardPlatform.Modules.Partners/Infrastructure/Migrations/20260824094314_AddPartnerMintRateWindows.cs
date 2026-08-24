using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Partners.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPartnerMintRateWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mint_rate_windows",
                schema: "partners",
                columns: table => new
                {
                    partner_api_client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    window_started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    request_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mint_rate_windows", x => x.partner_api_client_id);
                    table.CheckConstraint("ck_partner_mint_rate_windows_request_count", "request_count > 0");
                    table.ForeignKey(
                        name: "FK_mint_rate_windows_api_clients_partner_api_client_id",
                        column: x => x.partner_api_client_id,
                        principalSchema: "partners",
                        principalTable: "api_clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                """
                alter table partners.mint_rate_windows enable row level security;
                alter table partners.mint_rate_windows force row level security;

                create policy partner_mint_rate_windows_isolation
                    on partners.mint_rate_windows
                    using (
                        partner_api_client_id = nullif(
                            current_setting('app.partner_client_id', true),
                            '')::uuid
                    )
                    with check (
                        partner_api_client_id = nullif(
                            current_setting('app.partner_client_id', true),
                            '')::uuid
                    );

                revoke delete on partners.mint_rate_windows from public;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mint_rate_windows",
                schema: "partners");
        }
    }
}
