using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Organizations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AllowInitialAdministratorBootstrap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS organization_memberships_tenant_isolation
                    ON organizations.organization_memberships;

                CREATE POLICY organization_memberships_tenant_isolation
                    ON organizations.organization_memberships
                    USING (
                        organization_id = nullif(current_setting('app.organization_id', true), '')::uuid
                        OR coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                    )
                    WITH CHECK (
                        organization_id = nullif(current_setting('app.organization_id', true), '')::uuid
                        OR (
                            coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                            AND coalesce(nullif(current_setting('app.is_initial_admin_bootstrap', true), ''), 'false')::boolean
                        )
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS organization_memberships_tenant_isolation
                    ON organizations.organization_memberships;

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
    }
}
