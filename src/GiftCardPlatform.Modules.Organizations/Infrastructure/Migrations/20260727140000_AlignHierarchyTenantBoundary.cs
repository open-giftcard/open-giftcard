using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Organizations.Infrastructure.Migrations
{
    /// <summary>
    /// Aligns tenant isolation with the root customer boundary. An active
    /// membership still selects one operational organization, while RLS admits
    /// rows from that organization's customer hierarchy. Permission scope
    /// decides which admitted rows the caller may act on.
    /// </summary>
    public partial class AlignHierarchyTenantBoundary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION organizations.organization_belongs_to_caller_tenant(
                    candidate_organization_id uuid)
                RETURNS boolean
                LANGUAGE sql
                STABLE
                SECURITY DEFINER
                SET search_path = pg_catalog, organizations
                AS $fn$
                    SELECT EXISTS (
                        SELECT 1
                        FROM organizations.organizations candidate
                        WHERE candidate.id = candidate_organization_id
                          AND candidate.root_organization_id =
                              organizations.caller_root_organization_id()
                    )
                $fn$;

                DROP POLICY IF EXISTS organization_memberships_tenant_isolation
                    ON organizations.organization_memberships;

                CREATE POLICY organization_memberships_tenant_isolation
                    ON organizations.organization_memberships
                    USING (
                        organizations.organization_belongs_to_caller_tenant(organization_id)
                        OR coalesce(
                            nullif(current_setting('app.is_platform_operator', true), ''),
                            'false')::boolean
                    )
                    WITH CHECK (
                        organizations.organization_belongs_to_caller_tenant(organization_id)
                        OR (
                            coalesce(
                                nullif(current_setting('app.is_platform_operator', true), ''),
                                'false')::boolean
                            AND coalesce(
                                nullif(current_setting('app.is_initial_admin_bootstrap', true), ''),
                                'false')::boolean
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
                        organization_id =
                            nullif(current_setting('app.organization_id', true), '')::uuid
                        OR coalesce(
                            nullif(current_setting('app.is_platform_operator', true), ''),
                            'false')::boolean
                    )
                    WITH CHECK (
                        organization_id =
                            nullif(current_setting('app.organization_id', true), '')::uuid
                        OR (
                            coalesce(
                                nullif(current_setting('app.is_platform_operator', true), ''),
                                'false')::boolean
                            AND coalesce(
                                nullif(current_setting('app.is_initial_admin_bootstrap', true), ''),
                                'false')::boolean
                        )
                    );

                DROP FUNCTION IF EXISTS
                    organizations.organization_belongs_to_caller_tenant(uuid);
                """);
        }
    }
}
