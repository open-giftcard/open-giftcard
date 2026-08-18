using System.Data;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.GiftCards.Infrastructure;
using GiftCardPlatform.Modules.Partners.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GiftCardPlatform.Modules.GiftCards.Application;

internal sealed class GiftCardOwnershipWriter(
    GiftCardsDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IGiftCardOwnershipWriter
{
    private const string SerializationFailure = "40001";

    public async Task<GiftCardResult> BeginDistributionAsync(
        BeginGiftCardDistributionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var verifiedOrganizationMember =
            executionContext.IsAuthenticated &&
            !executionContext.IsPlatformOperator &&
            executionContext.UserId is not null &&
            executionContext.ActiveMembershipId is not null &&
            executionContext.TenantRootOrganizationId is not null;
        var acceptedBulkProcessor =
            executionContext.IsSystem &&
            executionContext.UserId == SystemActorIds.BulkGiftCardBatch;
        if (!verifiedOrganizationMember && !acceptedBulkProcessor)
        {
            throw new ForbiddenException(
                "gift_card.organization_member.required",
                "A verified organization membership is required.");
        }

        ValidateIdentifiers(
            request.GiftCardId,
            request.OwnerOrganizationId,
            request.InvitationId);

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await AcquireCardLockAsync(request.GiftCardId, cancellationToken).ConfigureAwait(false);

        var card = await LoadAsync(request.GiftCardId, cancellationToken).ConfigureAwait(false);
        if (!acceptedBulkProcessor &&
            card.FundingOrganizationId != executionContext.TenantRootOrganizationId)
        {
            throw new NotFoundException(
                "gift_card.not_found",
                "Gift card not found.");
        }

        card.BeginDistribution(
            request.OwnerOrganizationId,
            request.InvitationId,
            timeProvider.GetUtcNow());

        await SaveAndCompleteAsync(transaction, cancellationToken).ConfigureAwait(false);
        return GiftCardMapping.ToResult(card);
    }

    public async Task<GiftCardResult> BeginPartnerEpinDistributionAsync(
        BeginPartnerEpinDistributionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!executionContext.IsAuthenticated ||
            !executionContext.IsPartnerClient ||
            executionContext.PartnerClientId is null ||
            executionContext.TenantRootOrganizationId is null ||
            !executionContext.HasPartnerScope(PartnerScopes.GiftCardsMint))
        {
            throw new ForbiddenException(
                "partner.scope.gift_cards_mint.required",
                "A partner API client with mint authority is required.");
        }

        ValidateIdentifiers(request.GiftCardId, request.InvitationId);
        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await AcquireCardLockAsync(request.GiftCardId, cancellationToken).ConfigureAwait(false);

        var card = await LoadAsync(request.GiftCardId, cancellationToken).ConfigureAwait(false);
        var tenantRoot = executionContext.TenantRootOrganizationId.Value;
        if (card.FundingOrganizationId != tenantRoot ||
            card.IssuingOrganizationId != tenantRoot ||
            card.IssuedByPartnerClientId != executionContext.PartnerClientId)
        {
            throw new NotFoundException("gift_card.not_found", "Gift card not found.");
        }

        card.BeginDistribution(tenantRoot, request.InvitationId, timeProvider.GetUtcNow());
        await SaveAndCompleteAsync(transaction, cancellationToken).ConfigureAwait(false);
        return GiftCardMapping.ToResult(card);
    }

    public async Task<GiftCardResult> CompleteClaimAsync(
        CompleteGiftCardClaimRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentifiers(
            request.GiftCardId,
            request.InvitationId,
            request.OwnerUserId);
        if (executionContext.ClaimInvitationId != request.InvitationId &&
            executionContext.UserId != request.OwnerUserId)
        {
            throw new ForbiddenException(
                "gift_card.claim_scope.required",
                "A verified claim invitation is required.");
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await AcquireCardLockAsync(request.GiftCardId, cancellationToken).ConfigureAwait(false);

        var card = await LoadAsync(request.GiftCardId, cancellationToken).ConfigureAwait(false);
        card.CompleteClaim(
            request.InvitationId,
            request.OwnerUserId,
            timeProvider.GetUtcNow());

        await SaveAndCompleteAsync(transaction, cancellationToken).ConfigureAwait(false);
        return GiftCardMapping.ToResult(card);
    }

    private async Task<Domain.GiftCard> LoadAsync(
        Guid giftCardId,
        CancellationToken cancellationToken) =>
        await dbContext.GiftCards
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(card => card.Id == giftCardId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new NotFoundException(
            "gift_card.not_found",
            "Gift card not found.");

    private Task<int> AcquireCardLockAsync(
        Guid giftCardId,
        CancellationToken cancellationToken)
    {
        var lockKey = $"gift-card|{giftCardId:D}";
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);
    }

    private async Task SaveAndCompleteAsync(
        IModuleTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (FindSqlState(exception) == SerializationFailure)
        {
            throw new ConflictException(
                "gift_card.concurrent_conflict",
                "A concurrent gift-card operation conflicted. Retry safely.");
        }
    }

    private static void ValidateIdentifiers(params Guid[] identifiers)
    {
        if (identifiers.Any(identifier => identifier == Guid.Empty))
        {
            throw new ValidationFailedException(
                "gift_card.scope.required",
                "Gift card, owner, invitation, and identity identifiers must be non-empty.");
        }
    }

    private static string? FindSqlState(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres.SqlState;
            }
        }

        return null;
    }
}
