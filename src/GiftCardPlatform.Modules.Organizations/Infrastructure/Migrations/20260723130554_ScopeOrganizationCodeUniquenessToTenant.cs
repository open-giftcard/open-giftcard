using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Organizations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScopeOrganizationCodeUniquenessToTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_organizations_code",
                schema: "organizations",
                table: "organizations");

            migrationBuilder.AddColumn<Guid>(
                name: "root_organization_id",
                schema: "organizations",
                table: "organizations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill before the unique indexes are built, so existing rows carry
            // their real tenant root rather than the placeholder default. A root
            // organization is its own root; a descendant takes the first label of
            // its ltree path, which is the root's identifier.
            migrationBuilder.Sql(
                """
                UPDATE organizations.organizations
                SET root_organization_id = id
                WHERE parent_organization_id IS NULL;

                UPDATE organizations.organizations
                SET root_organization_id = replace(subltree(hierarchy_path, 0, 1)::text, 'org_', '')::uuid
                WHERE parent_organization_id IS NOT NULL;
                """);

            // The default existed only to populate existing rows; inserts must
            // supply the value explicitly from here on.
            migrationBuilder.Sql(
                "ALTER TABLE organizations.organizations ALTER COLUMN root_organization_id DROP DEFAULT;");

            migrationBuilder.CreateIndex(
                name: "ix_organizations_root_organization_id",
                schema: "organizations",
                table: "organizations",
                column: "root_organization_id");

            migrationBuilder.CreateIndex(
                name: "ux_organizations_root_code",
                schema: "organizations",
                table: "organizations",
                column: "code",
                unique: true,
                filter: "parent_organization_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_organizations_tenant_code",
                schema: "organizations",
                table: "organizations",
                columns: new[] { "root_organization_id", "code" },
                unique: true,
                filter: "parent_organization_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_organizations_root_organization_id",
                schema: "organizations",
                table: "organizations");

            migrationBuilder.DropIndex(
                name: "ux_organizations_root_code",
                schema: "organizations",
                table: "organizations");

            migrationBuilder.DropIndex(
                name: "ux_organizations_tenant_code",
                schema: "organizations",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "root_organization_id",
                schema: "organizations",
                table: "organizations");

            migrationBuilder.CreateIndex(
                name: "ux_organizations_code",
                schema: "organizations",
                table: "organizations",
                column: "code",
                unique: true);
        }
    }
}
