using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Authorization.Infrastructure.Migrations
{
    /// <summary>
    /// Makes authorization-table RLS enforce the customer tenant root rather
    /// than the exact active organization. Application permission evaluation
    /// remains responsible for Organization, Subtree, and SelectedOrganizations
    /// scope.
    /// </summary>
    public partial class AlignHierarchyTenantBoundary : Migration
    {
        private static readonly string[] TenantTables =
        [
            "roles",
            "role_permissions",
            "membership_role_assignments",
            "membership_role_assignment_scopes",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                migrationBuilder.Sql(
                    $"""
                    DROP POLICY IF EXISTS {table}_tenant_isolation
                        ON "authorization".{table};

                    CREATE POLICY {table}_tenant_isolation
                        ON "authorization".{table}
                        USING (
                            organizations.organization_belongs_to_caller_tenant(
                                organization_id)
                            OR coalesce(
                                nullif(
                                    current_setting(
                                        'app.is_platform_operator',
                                        true),
                                    ''),
                                'false')::boolean
                        )
                        WITH CHECK (
                            organizations.organization_belongs_to_caller_tenant(
                                organization_id)
                            OR (
                                coalesce(
                                    nullif(
                                        current_setting(
                                            'app.is_platform_operator',
                                            true),
                                        ''),
                                    'false')::boolean
                                AND coalesce(
                                    nullif(
                                        current_setting(
                                            'app.is_initial_admin_bootstrap',
                                            true),
                                        ''),
                                    'false')::boolean
                            )
                        );
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                migrationBuilder.Sql(
                    $"""
                    DROP POLICY IF EXISTS {table}_tenant_isolation
                        ON "authorization".{table};

                    CREATE POLICY {table}_tenant_isolation
                        ON "authorization".{table}
                        USING (
                            organization_id =
                                nullif(
                                    current_setting('app.organization_id', true),
                                    '')::uuid
                            OR coalesce(
                                nullif(
                                    current_setting(
                                        'app.is_platform_operator',
                                        true),
                                    ''),
                                'false')::boolean
                        )
                        WITH CHECK (
                            organization_id =
                                nullif(
                                    current_setting('app.organization_id', true),
                                    '')::uuid
                        );
                    """);
            }
        }
    }
}
