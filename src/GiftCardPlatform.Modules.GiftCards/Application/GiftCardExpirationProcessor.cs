using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.GiftCards.Domain;
using GiftCardPlatform.Modules.GiftCards.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.GiftCards.Application;

internal sealed class GiftCardExpirationProcessor(
    GiftCardsDbContext dbContext,
    GiftCardLifecycleService lifecycleService,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IGiftCardExpirationProcessor
{
    public async Task<GiftCardExpirationBatchResult> ProcessDueAsync(
        int maximumItems,
        CancellationToken cancellationToken)
    {
        if (maximumItems is < 1 or > 100)
        {
            throw new ValidationFailedException(
                "gift_card.expiration.batch_size.invalid",
                "Expiration batch size must be between 1 and 100.");
        }

        if (!executionContext.IsSystem ||
            !executionContext.HasPlatformPermission(
                PlatformPermissions.GiftCardsManageLifecycle))
        {
            throw new ForbiddenException(
                "gift_card.expiration.system.required",
                "The trusted expiration-system context is required.");
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var dueIds = await dbContext.GiftCards
            .AsNoTracking()
            .Where(card =>
                card.ExpiresAtUtc <= now &&
                card.LifecycleState != GiftCardLifecycleState.Cancelled &&
                card.LifecycleState != GiftCardLifecycleState.Expired)
            .OrderBy(card => card.ExpiresAtUtc)
            .ThenBy(card => card.Id)
            .Select(card => card.Id)
            .Take(maximumItems)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var expired = 0;
        var conflicted = 0;
        foreach (var giftCardId in dueIds)
        {
            try
            {
                await lifecycleService.ExecuteSystemExpirationAsync(
                    giftCardId,
                    cancellationToken).ConfigureAwait(false);
                expired++;
            }
            catch (ConflictException)
            {
                conflicted++;
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        }

        return new GiftCardExpirationBatchResult(
            dueIds.Count,
            expired,
            conflicted);
    }
}
