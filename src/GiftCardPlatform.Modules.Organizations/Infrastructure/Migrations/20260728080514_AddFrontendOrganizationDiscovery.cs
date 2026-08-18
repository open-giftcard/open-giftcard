using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Organizations.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the narrow pre-selection read path required by independent frontend
    /// clients. Identity context may discover only the current user's active
    /// memberships and their organizations. The policies are SELECT-only and
    /// are disabled whenever an organization context has already been selected.
    /// </summary>
    public partial class AddFrontendOrganizationDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_organization_memberships_user_status_organization",
                schema: "organizations",
                table: "organization_memberships",
                columns: new[] { "user_id", "status", "organization_id" });

            migrationBuilder.Sql(
                """
                CREATE POLICY organization_memberships_self_select
                    ON organizations.organization_memberships
                    FOR SELECT
                    USING (
                        nullif(current_setting('app.organization_id', true), '') IS NULL
                        AND user_id =
                            nullif(current_setting('app.user_id', true), '')::uuid
                        AND status = 'Active'
                    );

                CREATE POLICY organizations_self_membership_select
                    ON organizations.organizations
                    FOR SELECT
                    USING (
                        nullif(current_setting('app.organization_id', true), '') IS NULL
                        AND EXISTS (
                            SELECT 1
                            FROM organizations.organization_memberships membership
                            WHERE membership.organization_id = organizations.id
                              AND membership.user_id =
                                  nullif(current_setting('app.user_id', true), '')::uuid
                              AND membership.status = 'Active'
                        )
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS organizations_self_membership_select
                    ON organizations.organizations;

                DROP POLICY IF EXISTS organization_memberships_self_select
                    ON organizations.organization_memberships;
                """);

            migrationBuilder.DropIndex(
                name: "ix_organization_memberships_user_status_organization",
                schema: "organizations",
                table: "organization_memberships");
        }
    }
}
