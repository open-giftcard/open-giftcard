using System.Data;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.GiftCards.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.GiftCards.Application;

internal sealed class GiftCardPaymentWriter(
    GiftCardsDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IGiftCardPaymentWriter
{
    public Task<GiftCardSpendableResult> GetOwnedSpendableAsync(
        Guid giftCardId,
        CancellationToken cancellationToken) =>
        GetSpendableAsync(giftCardId, requireExactOwner: true, cancellationToken);

    public Task<GiftCardSpendableResult> GetCredentialSpendableAsync(
        Guid giftCardId,
        CancellationToken cancellationToken) =>
        GetSpendableAsync(giftCardId, requireExactOwner: false, cancellationToken);

    public async Task<GiftCardRefundableResult> GetCredentialRefundableAsync(
        Guid giftCardId,
        CancellationToken cancellationToken)
    {
        if (giftCardId == Guid.Empty || executionContext.IsPlatformOperator ||
            executionContext.PaymentTokenId is null)
        {
            throw CardNotFound();
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var lockKey = $"gift-card|{giftCardId:D}";
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken).ConfigureAwait(false);
        var card = await dbContext.GiftCards.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == giftCardId, cancellationToken)
            .ConfigureAwait(false) ?? throw CardNotFound();
        card.EnsureRefundable();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GiftCardRefundableResult(
            card.Id, card.PublicReference, card.FundingOrganizationId,
            card.OwnerUserId!.Value, card.Currency);
    }

    private async Task<GiftCardSpendableResult> GetSpendableAsync(
        Guid giftCardId,
        bool requireExactOwner,
        CancellationToken cancellationToken)
    {
        if (giftCardId == Guid.Empty || executionContext.IsPlatformOperator)
        {
            throw CardNotFound();
        }

        if (requireExactOwner &&
            (!executionContext.IsAuthenticated || executionContext.UserId is null))
        {
            throw CardNotFound();
        }

        // A credential-scoped read is legal only inside one verified payment
        // candidate. Without it a POS principal would otherwise be able to name
        // any card identifier.
        if (!requireExactOwner && executionContext.PaymentTokenId is null)
        {
            throw CardNotFound();
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        // Lifecycle and sharing take the same lock before reading or changing a
        // card. Payment must join that order so eligibility cannot change
        // between validation and the value-account posting.
        var lockKey = $"gift-card|{giftCardId:D}";
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken).ConfigureAwait(false);
        // The filter mirrors tenant and owner visibility, neither of which a POS
        // principal has. RLS is the authoritative barrier here and admits
        // exactly the card the presented credential was issued against, so the
        // credential-scoped read defers to it, as the share-claim path does.
        var card = await dbContext.GiftCards
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == giftCardId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw CardNotFound();

        // Ownership is checked before eligibility so a stranger cannot learn a
        // card's lifecycle state from the error code.
        if (requireExactOwner && card.OwnerUserId != executionContext.UserId)
        {
            throw CardNotFound();
        }

        card.EnsureSpendable(timeProvider.GetUtcNow());
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GiftCardSpendableResult(
            card.Id,
            card.PublicReference,
            card.FundingOrganizationId,
            card.OwnerUserId!.Value,
            card.Currency,
            card.ExpiresAtUtc);
    }

    private static NotFoundException CardNotFound() =>
        new("gift_card.not_found", "The gift card was not found.");
}
