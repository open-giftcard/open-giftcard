using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Authorization.Infrastructure.Migrations
{
    /// <summary>
    /// Grants the built-in Platform Administrator the two platform permissions
    /// added after it was created.
    ///
    /// Bootstrap grants every value in <c>PlatformPermissions.All</c>, so a
    /// deployment bootstrapped today is complete. One bootstrapped before
    /// IMPL-026 and IMPL-030 is not: those slices added
    /// <c>platform.pos.clients.manage</c> and <c>platform.payments.view</c> to
    /// the catalogue, and nothing granted them to the role that already
    /// existed. The symptom is a platform operator who cannot register a till or
    /// open payment reporting, with no indication why, because the permission
    /// simply is not there.
    ///
    /// Idempotent, and safe on a database that already has them.
    /// </summary>
    public partial class BackfillPosAndPaymentPlatformPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                insert into "authorization".permissions
                    (name, is_platform_permission)
                values
                    ('platform.pos.clients.manage', true),
                    ('platform.payments.view', true)
                on conflict (name) do nothing;

                select
                    set_config('app.is_platform_operator', 'true', true),
                    set_config('app.is_initial_admin_bootstrap', 'true', true);

                insert into "authorization".platform_role_permissions
                    (id, role_id, permission)
                select
                    gen_random_uuid(),
                    role.id,
                    granted.permission
                from "authorization".platform_roles role
                cross join (
                    values ('platform.pos.clients.manage'), ('platform.payments.view')
                ) as granted(permission)
                where role.is_system = true
                  and role.name = 'Platform Administrator'
                on conflict (role_id, permission) do nothing;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The catalogue rows are left in place: the permissions are still
            // named by the code, and removing them would break the foreign key
            // from any grant made outside this migration.
            migrationBuilder.Sql(
                """
                select
                    set_config('app.is_platform_operator', 'true', true),
                    set_config('app.is_initial_admin_bootstrap', 'true', true);

                delete from "authorization".platform_role_permissions
                where permission in (
                    'platform.pos.clients.manage',
                    'platform.payments.view'
                );
                """);
        }
    }
}
