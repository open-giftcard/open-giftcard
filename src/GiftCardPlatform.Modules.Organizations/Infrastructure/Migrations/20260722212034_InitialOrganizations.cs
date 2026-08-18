using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Organizations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialOrganizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "organizations");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:ltree", ",,");

            migrationBuilder.CreateTable(
                name: "organizations",
                schema: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    parent_organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hierarchy_path = table.Column<string>(type: "ltree", nullable: false),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.id);
                    table.CheckConstraint("ck_organizations_depth_non_negative", "\"depth\" >= 0");
                    table.CheckConstraint("ck_organizations_max_depth", "\"depth\" <= 4");
                    table.CheckConstraint("ck_organizations_no_self_parent", "\"parent_organization_id\" IS NULL OR \"parent_organization_id\" <> \"id\"");
                    table.CheckConstraint("ck_organizations_root_depth", "(\"parent_organization_id\" IS NULL AND \"depth\" = 0) OR (\"parent_organization_id\" IS NOT NULL AND \"depth\" > 0)");
                    table.ForeignKey(
                        name: "FK_organizations_organizations_parent_organization_id",
                        column: x => x.parent_organization_id,
                        principalSchema: "organizations",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_organizations_hierarchy_path",
                schema: "organizations",
                table: "organizations",
                column: "hierarchy_path");

            migrationBuilder.CreateIndex(
                name: "ix_organizations_parent_organization_id",
                schema: "organizations",
                table: "organizations",
                column: "parent_organization_id");

            migrationBuilder.CreateIndex(
                name: "ux_organizations_code",
                schema: "organizations",
                table: "organizations",
                column: "code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organizations",
                schema: "organizations");
        }
    }
}
