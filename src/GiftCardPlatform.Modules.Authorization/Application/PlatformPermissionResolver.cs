using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Authorization.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Authorization.Application;

internal sealed class PlatformPermissionResolver(
    AuthorizationDbContext dbContext,
    ITransactionCoordinator transactionCoordinator) : IPlatformPermissionResolver
{
    public async Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var permissions = await (
                from assignment in dbContext.PlatformRoleAssignments
                join permission in dbContext.PlatformRolePermissions
                    on assignment.RoleId equals permission.RoleId
                where assignment.UserId == userId
                select permission.Permission)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return permissions.ToHashSet(StringComparer.Ordinal);
    }
}
