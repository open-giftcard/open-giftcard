using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Authorization.Domain;
using GiftCardPlatform.Modules.Authorization.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Authorization.Application;

internal static class PermissionCatalogueSynchronizer
{
    public static async Task EnsureAsync(
        AuthorizationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.PermissionDefinitions
            .Select(x => x.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var known = new HashSet<string>(existing, StringComparer.Ordinal);

        var missing = OrganizationPermissions.All
            .Where(name => !known.Contains(name))
            .Select(name => PermissionDefinition.Create(name, isPlatformPermission: false))
            .Concat(PlatformPermissions.All
                .Where(name => !known.Contains(name))
                .Select(name => PermissionDefinition.Create(name, isPlatformPermission: true)))
            .ToList();

        if (missing.Count == 0)
        {
            await EnsureAdministratorPermissionsAsync(dbContext, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        dbContext.PermissionDefinitions.AddRange(missing);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await EnsureAdministratorPermissionsAsync(dbContext, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task EnsureAdministratorPermissionsAsync(
        AuthorizationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var administratorRole = await dbContext.PlatformRoles
            .Include(role => role.Permissions)
            .SingleOrDefaultAsync(
                role => role.IsSystem && role.Name == PlatformBootstrapService.AdministratorRoleName,
                cancellationToken)
            .ConfigureAwait(false);

        if (administratorRole is null)
        {
            return;
        }

        foreach (var permission in PlatformPermissions.All)
        {
            administratorRole.Grant(permission);
        }

        var companyAdministratorRoles = await dbContext.Roles
            .Include(role => role.Permissions)
            .Where(role =>
                role.IsSystem &&
                role.Name == InitialOrganizationAdministratorService.AdministratorRoleName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var role in companyAdministratorRoles)
        {
            foreach (var permission in OrganizationPermissions.All)
            {
                role.Grant(permission);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
