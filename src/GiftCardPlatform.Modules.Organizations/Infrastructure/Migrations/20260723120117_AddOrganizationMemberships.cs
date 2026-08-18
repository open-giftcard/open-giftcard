using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Organizations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organization_memberships",
                schema: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_memberships", x => x.id);
                    table.ForeignKey(
                        name: "FK_organization_memberships_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "organizations",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_organization_memberships_organization_id",
                schema: "organizations",
                table: "organization_memberships",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ux_organization_memberships_organization_user",
                schema: "organizations",
                table: "organization_memberships",
                columns: new[] { "organization_id", "user_id" },
                unique: true);

            // Row-Level Security is the authoritative tenant-isolation barrier
            // (ADR-005). FORCE also subjects the table owner to the policy, so a
            // missed application filter cannot leak across tenants even for the
            // migration owner.
            //
            // USING governs which rows are visible (SELECT/UPDATE/DELETE); a
            // platform operator may read across tenants through this path. WITH
            // CHECK governs written values (INSERT/UPDATE) and deliberately omits
            // the platform path: no caller may write across tenants — a write
            // must always match the caller's active organization. Settings are
            // read with missing_ok, and an empty string (an unset/anonymous
            // context) maps to NULL so it matches no organization.
            migrationBuilder.Sql(
                """
                ALTER TABLE organizations.organization_memberships ENABLE ROW LEVEL SECURITY;
                ALTER TABLE organizations.organization_memberships FORCE ROW LEVEL SECURITY;

                CREATE POLICY organization_memberships_tenant_isolation
                    ON organizations.organization_memberships
                    USING (
                        organization_id = nullif(current_setting('app.organization_id', true), '')::uuid
                        OR coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                    )
                    WITH CHECK (
                        organization_id = nullif(current_setting('app.organization_id', true), '')::uuid
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS organization_memberships_tenant_isolation ON organizations.organization_memberships;");

            migrationBuilder.DropTable(
                name: "organization_memberships",
                schema: "organizations");
        }
    }
}
