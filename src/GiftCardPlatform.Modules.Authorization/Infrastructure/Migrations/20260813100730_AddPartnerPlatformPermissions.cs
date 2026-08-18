using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Authorization.Infrastructure.Migrations
{
    /// <summary>
    /// Adds <c>platform.partners.manage</c> to the permission catalogue and
    /// grants it to the built-in Platform Administrator (ADR-053).
    ///
    /// The catalogue insert is required rather than cosmetic: the permission
    /// column is foreign-keyed to <c>"authorization".permissions</c>, so a grant
    /// naming an uncatalogued permission is rejected by the database.
    ///
    /// The grant follows the same reasoning as the POS and payments backfill. A
    /// deployment bootstrapped after this slice receives every value in
    /// <c>PlatformPermissions.All</c> automatically; one bootstrapped before it
    /// would otherwise have an operator who cannot register a reseller, with no
    /// indication why, because the permission simply is not there.
    ///
    /// Idempotent, and safe on a database that already has it.
    /// </summary>
    public partial class AddPartnerPlatformPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                insert into "authorization".permissions
                    (name, is_platform_permission)
                values
                    ('platform.partners.manage', true)
                on conflict (name) do nothing;

                select
                    set_config('app.is_platform_operator', 'true', true),
                    set_config('app.is_initial_admin_bootstrap', 'true', true);

                insert into "authorization".platform_role_permissions
                    (id, role_id, permission)
                select
                    gen_random_uuid(),
                    role.id,
                    'platform.partners.manage'
                from "authorization".platform_roles role
                where role.is_system = true
                  and role.name = 'Platform Administrator'
                on conflict (role_id, permission) do nothing;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The catalogue row is left in place: the permission is still named
            // by the code, and removing it would break the foreign key from any
            // grant made outside this migration.
            migrationBuilder.Sql(
                """
                select
                    set_config('app.is_platform_operator', 'true', true),
                    set_config('app.is_initial_admin_bootstrap', 'true', true);

                delete from "authorization".platform_role_permissions
                where permission = 'platform.partners.manage';
                """);
        }
    }
}
