using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Organizations.Contracts;
using GiftCardPlatform.Modules.Organizations.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Organizations.Application;

/// <summary>
/// Answers hierarchy containment for other modules (ADR-006 <c>Subtree</c>
/// scope). Runs inside a module transaction so the RLS session context is
/// established first: an organization outside the caller's tenant is simply not
/// found, and the answer is false.
/// </summary>
internal sealed class OrganizationHierarchyQuery(
    OrganizationsDbContext dbContext,
    ITransactionCoordinator transactionCoordinator) : IOrganizationHierarchyQuery
{
    public async Task<bool> IsSelfOrDescendantAsync(
        Guid anchorOrganizationId,
        Guid targetOrganizationId,
        CancellationToken cancellationToken)
    {
        // An anchor equal to the target is not short-circuited to true: the
        // organization must still be visible to the caller, and an invisible one
        // must answer false.
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var anchorPath = await dbContext.Organizations
            .AsNoTracking()
            .Where(x => x.Id == anchorOrganizationId)
            .Select(x => x.HierarchyPath)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (anchorPath is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var targetPath = await dbContext.Organizations
            .AsNoTracking()
            .Where(x => x.Id == targetOrganizationId)
            .Select(x => x.HierarchyPath)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (targetPath is null)
        {
            return false;
        }

        // Descendant-or-self on the materialized path. Compared in memory on two
        // single-row lookups rather than with the ltree <@ operator, so the check
        // stays provider-agnostic; the paths are short and already fetched.
        return targetPath == anchorPath ||
               targetPath.StartsWith(anchorPath + ".", StringComparison.Ordinal);
    }
}
