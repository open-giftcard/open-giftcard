using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Authorization.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCorporateCreditReversalPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                insert into "authorization".permissions (name, is_platform_permission)
                values ('platform.corporate_credits.reverse', true)
                on conflict (name) do nothing;

                insert into "authorization".platform_role_permissions (id, role_id, permission)
                select gen_random_uuid(), role.id, 'platform.corporate_credits.reverse'
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
                delete from "authorization".platform_role_permissions
                where permission = 'platform.corporate_credits.reverse';

                delete from "authorization".permissions
                where name = 'platform.corporate_credits.reverse';
                """);
        }
    }
}
