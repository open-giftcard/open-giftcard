using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Authorization.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDistributionPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                insert into "authorization".permissions
                    (name, is_platform_permission)
                values
                    ('organization.gift_cards.distribute', false)
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
                    'organization.gift_cards.distribute'
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

                delete from "authorization".role_permissions
                where permission = 'organization.gift_cards.distribute';

                delete from "authorization".permissions
                where name = 'organization.gift_cards.distribute';
                """);
        }
    }
}
