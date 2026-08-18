using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Identity.Contracts;
using GiftCardPlatform.Modules.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Identity.Application;

internal sealed class IdentityUserQuery(
    IdentityDbContext dbContext,
    ITransactionCoordinator transactionCoordinator) : IIdentityUserQuery
{
    public async Task<UserResult?> FindAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var result = await dbContext.Users
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new UserResult(
                x.Id,
                x.Email,
                x.PhoneNumber,
                x.Status.ToString(),
                x.CreatedAtUtc,
                x.DisabledAtUtc))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
