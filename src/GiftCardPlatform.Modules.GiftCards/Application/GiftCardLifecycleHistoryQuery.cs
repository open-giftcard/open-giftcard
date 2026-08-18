using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.GiftCards.Domain;
using GiftCardPlatform.Modules.GiftCards.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.GiftCards.Application;

internal sealed class GiftCardLifecycleHistoryQuery(
    GiftCardsDbContext dbContext,
    IOrganizationPermissionAuthorizer organizationAuthorizer,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext) : IGiftCardLifecycleHistoryQuery
{
    public async Task<GiftCardLifecycleHistoryResult> GetForOrganizationAsync(
        Guid organizationId,
        Guid giftCardId,
        CancellationToken cancellationToken)
    {
        await organizationAuthorizer.RequirePermissionAsync(
            organizationId,
            OrganizationPermissions.GiftCardsView,
            cancellationToken).ConfigureAwait(false);
        return await GetAsync(
            giftCardId,
            card =>
                card.IssuingOrganizationId == organizationId &&
                card.FundingOrganizationId == executionContext.TenantRootOrganizationId,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<GiftCardLifecycleHistoryResult> GetForPlatformAsync(
        Guid giftCardId,
        CancellationToken cancellationToken)
    {
        if (!executionContext.IsPlatformOperator ||
            executionContext.IsSystem ||
            !executionContext.HasPlatformPermission(PlatformPermissions.GiftCardsView))
        {
            throw new ForbiddenException(
                "gift_card.view.platform_permission.required",
                $"Permission '{PlatformPermissions.GiftCardsView}' is required.");
        }

        return GetAsync(giftCardId, _ => true, cancellationToken);
    }

    public Task<GiftCardLifecycleHistoryResult> GetForOwnerAsync(
        Guid giftCardId,
        CancellationToken cancellationToken)
    {
        if (!executionContext.IsAuthenticated ||
            executionContext.IsPlatformOperator ||
            executionContext.UserId is null)
        {
            throw new ForbiddenException(
                "gift_card.owner.required",
                "An authenticated cardholder is required.");
        }

        return GetAsync(
            giftCardId,
            card =>
                card.OwnershipState == GiftCardOwnershipState.IdentityOwned &&
                card.OwnerUserId == executionContext.UserId,
            cancellationToken);
    }

    private async Task<GiftCardLifecycleHistoryResult> GetAsync(
        Guid giftCardId,
        Func<GiftCard, bool> scopePredicate,
        CancellationToken cancellationToken)
    {
        if (giftCardId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "gift_card.required",
                "A gift card identifier is required.");
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var card = await dbContext.GiftCards
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == giftCardId, cancellationToken)
            .ConfigureAwait(false);
        if (card is null || !scopePredicate(card))
        {
            throw new NotFoundException(
                "gift_card.not_found",
                "Gift card not found.");
        }

        var events = await dbContext.LifecycleEvents
            .AsNoTracking()
            .Where(lifecycleEvent => lifecycleEvent.GiftCardId == giftCardId)
            .OrderByDescending(lifecycleEvent => lifecycleEvent.OccurredAtUtc)
            .ThenByDescending(lifecycleEvent => lifecycleEvent.Id)
            .Take(200)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GiftCardLifecycleHistoryResult(
            GiftCardMapping.ToResult(card),
            [.. events.Select(GiftCardMapping.ToResult)]);
    }
}
