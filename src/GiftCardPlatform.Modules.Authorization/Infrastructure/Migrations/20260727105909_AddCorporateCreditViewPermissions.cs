using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Authorization.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCorporateCreditViewPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                insert into "authorization".permissions (name, is_platform_permission)
                values
                    ('platform.corporate_credits.view', true),
                    ('organization.corporate_credits.view', false)
                on conflict (name) do nothing;

                insert into "authorization".platform_role_permissions (id, role_id, permission)
                select gen_random_uuid(), role.id, 'platform.corporate_credits.view'
                from "authorization".platform_roles role
                where role.is_system = true
                  and role.name = 'Platform Administrator'
                on conflict (role_id, permission) do nothing;

                select
                    set_config('app.is_platform_operator', 'true', true),
                    set_config('app.is_initial_admin_bootstrap', 'true', true);

                insert into "authorization".role_permissions
                    (id, role_id, organization_id, permission)
                select
                    gen_random_uuid(),
                    role.id,
                    role.organization_id,
                    'organization.corporate_credits.view'
                from "authorization".roles role
                where role.is_system = true
                  and role.name = 'Company Administrator'
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

                delete from "authorization".platform_role_permissions
                where permission = 'platform.corporate_credits.view';

                delete from "authorization".role_permissions
                where permission = 'organization.corporate_credits.view';

                delete from "authorization".permissions
                where name in (
                    'platform.corporate_credits.view',
                    'organization.corporate_credits.view'
                );
                """);
        }
    }
}
