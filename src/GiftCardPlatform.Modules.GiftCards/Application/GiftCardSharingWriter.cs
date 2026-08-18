using System.Data;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.GiftCards.Domain;
using GiftCardPlatform.Modules.GiftCards.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.GiftCards.Application;

internal sealed class GiftCardSharingWriter(
    GiftCardsDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IGiftCardSharingWriter
{
    public async Task<IReadOnlyDictionary<Guid, string>> GetVisiblePublicReferencesAsync(
        IReadOnlyCollection<Guid> giftCardIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(giftCardIds);
        if (giftCardIds.Count > 200 || giftCardIds.Any(id => id == Guid.Empty))
        {
            throw new ValidationFailedException(
                "gift_card.share.references.invalid",
                "At most 200 valid gift-card references may be requested.");
        }

        if (giftCardIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = giftCardIds.Distinct().ToArray();
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var rows = await dbContext.GiftCards
            .AsNoTracking()
            .Where(card => ids.Contains(card.Id))
            .Select(card => new { card.Id, card.PublicReference })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(row => row.Id, row => row.PublicReference);
    }

    public Task<GiftCardShareSourceResult> GetOwnedSourceAsync(
        Guid sourceGiftCardId,
        CancellationToken cancellationToken) =>
        GetSourceAsync(sourceGiftCardId, requireExactOwner: true, cancellationToken);

    public Task<GiftCardShareSourceResult> GetClaimSourceAsync(
        Guid sourceGiftCardId,
        CancellationToken cancellationToken) =>
        GetSourceAsync(sourceGiftCardId, requireExactOwner: false, cancellationToken);

    public async Task<GiftCardResult> CreateChildAsync(
        CreateSharedGiftCardChildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAuthenticatedRecipient(request.RecipientUserId);

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await AcquireCardLockAsync(request.SourceGiftCardId, cancellationToken).ConfigureAwait(false);

        var existing = await dbContext.GiftCards
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(card => card.Id == request.ChildGiftCardId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.MatchesSharedChild(
                    request.SourceGiftCardId,
                    request.RecipientUserId,
                    request.Amount,
                    request.LedgerAccountId,
                    request.LedgerTransactionId,
                    request.ShareId))
            {
                throw new ConflictException(
                    "gift_card.share.child.conflict",
                    "The child gift-card identifier was already used for different intent.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return GiftCardMapping.ToResult(existing);
        }

        var source = await dbContext.GiftCards
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(card => card.Id == request.SourceGiftCardId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw CardNotFound();
        source.EnsureShareEligible(timeProvider.GetUtcNow());

        var child = GiftCard.CreateSharedChild(
            source,
            request.ChildGiftCardId,
            GiftCardPublicReferenceGenerator.Create(),
            request.RecipientUserId,
            request.Amount,
            request.LedgerAccountId,
            request.LedgerTransactionId,
            request.ShareId,
            request.PostedAtUtc);
        dbContext.GiftCards.Add(child);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return GiftCardMapping.ToResult(child);
    }

    public async Task<GiftCardResult> GetChildAsync(
        Guid childGiftCardId,
        CancellationToken cancellationToken)
    {
        EnsureAuthenticatedRecipient(executionContext.UserId ?? Guid.Empty);
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var child = await dbContext.GiftCards
            .SingleOrDefaultAsync(card => card.Id == childGiftCardId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw CardNotFound();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return GiftCardMapping.ToResult(child);
    }

    private async Task<GiftCardShareSourceResult> GetSourceAsync(
        Guid sourceGiftCardId,
        bool requireExactOwner,
        CancellationToken cancellationToken)
    {
        if (sourceGiftCardId == Guid.Empty ||
            (executionContext.IsPlatformOperator && !executionContext.IsSystem) ||
            (requireExactOwner &&
                (!executionContext.IsAuthenticated || executionContext.UserId is null)))
        {
            throw CardNotFound();
        }

        if (!requireExactOwner && executionContext.ShareId is null)
        {
            throw CardNotFound();
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await AcquireCardLockAsync(sourceGiftCardId, cancellationToken).ConfigureAwait(false);
        var card = await dbContext.GiftCards
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(item => item.Id == sourceGiftCardId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw CardNotFound();
        card.EnsureShareEligible(timeProvider.GetUtcNow());
        if (requireExactOwner && card.OwnerUserId != executionContext.UserId)
        {
            throw CardNotFound();
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GiftCardShareSourceResult(
            card.Id,
            card.PublicReference,
            card.FundingOrganizationId,
            card.IssuingOrganizationId,
            card.OwnerUserId!.Value,
            card.LifecycleState.ToString(),
            card.Currency,
            card.ValidFromUtc,
            card.ExpiresAtUtc,
            card.IsTransferable,
            card.IsDivisible,
            card.RootGiftCardId,
            card.Generation);
    }

    private void EnsureAuthenticatedRecipient(Guid recipientUserId)
    {
        if (!executionContext.IsAuthenticated || executionContext.IsPlatformOperator ||
            executionContext.UserId != recipientUserId || recipientUserId == Guid.Empty ||
            executionContext.ShareId is null)
        {
            throw new ForbiddenException(
                "gift_card.share.recipient.required",
                "An authenticated share recipient is required.");
        }
    }

    private Task<int> AcquireCardLockAsync(Guid giftCardId, CancellationToken cancellationToken)
    {
        var lockKey = $"gift-card|{giftCardId:D}";
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);
    }

    private static NotFoundException CardNotFound() =>
        new("gift_card.not_found", "Gift card not found.");
}
