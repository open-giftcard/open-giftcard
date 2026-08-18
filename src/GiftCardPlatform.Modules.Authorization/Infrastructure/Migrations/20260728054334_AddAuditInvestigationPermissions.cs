using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Authorization.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditInvestigationPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                insert into "authorization".permissions
                    (name, is_platform_permission)
                values
                    ('organization.audit.view', false),
                    ('platform.audit.view', true)
                on conflict (name) do nothing;

                select
                    set_config('app.is_platform_operator', 'true', true),
                    set_config('app.is_initial_admin_bootstrap', 'true', true);

                insert into "authorization".role_permissions
                    (id, role_id, organization_id, permission)
                select
                    gen_random_uuid(),
                    role.id,
                    role.organization_id,
                    'organization.audit.view'
                from "authorization".roles role
                where role.is_system = true
                  and role.name = 'Company Administrator'
                on conflict (role_id, permission) do nothing;

                insert into "authorization".platform_role_permissions
                    (id, role_id, permission)
                select
                    gen_random_uuid(),
                    role.id,
                    'platform.audit.view'
                from "authorization".platform_roles role
                where role.is_system = true
                  and role.name = 'Platform Administrator'
                on conflict (role_id, permission) do nothing;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                select
                    set_config('app.is_platform_operator', 'true', true),
                    set_config('app.is_initial_admin_bootstrap', 'true', true);

                delete from "authorization".role_permissions
                where permission = 'organization.audit.view';

                delete from "authorization".platform_role_permissions
                where permission = 'platform.audit.view';

                delete from "authorization".permissions
                where name in (
                    'organization.audit.view',
                    'platform.audit.view'
                );
                """);
        }
    }
}
