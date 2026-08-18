using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.GiftCards.Domain;
using GiftCardPlatform.Modules.GiftCards.Infrastructure;
using GiftCardPlatform.Modules.Ledger.Contracts;
using GiftCardPlatform.Modules.Sharing.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GiftCardPlatform.Modules.GiftCards.Application;

internal sealed class GiftCardLifecycleService(
    GiftCardsDbContext dbContext,
    IOrganizationPermissionAuthorizer organizationAuthorizer,
    IDistributionLifecycleWriter distributionLifecycleWriter,
    IShareLifecycleWriter shareLifecycleWriter,
    ILedgerWriter ledgerWriter,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IGiftCardLifecycleService
{
    private const string UniqueViolation = "23505";
    private const string SerializationFailure = "40001";

    public async Task<GiftCardLifecycleOperationResult> ExecuteForOrganizationAsync(
        Guid organizationId,
        Guid giftCardId,
        GiftCardLifecycleAction action,
        AdministerGiftCardLifecycleRequest request,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(organizationId, "organization");
        ValidateIdentifier(giftCardId, "gift card");
        var intent = GiftCardLifecycleIntent.CreateAdministrative(action, request);
        await organizationAuthorizer.RequirePermissionAsync(
            organizationId,
            OrganizationPermissions.GiftCardsManageLifecycle,
            cancellationToken).ConfigureAwait(false);

        if (executionContext.UserId is null ||
            executionContext.ActiveMembershipId is null ||
            executionContext.TenantRootOrganizationId is null)
        {
            throw new ForbiddenException(
                "gift_card.lifecycle.organization_actor.required",
                "A verified organization membership is required.");
        }

        return await ExecuteAsync(
            giftCardId,
            intent,
            new LifecycleActor(
                GiftCardLifecycleActorType.OrganizationMember,
                executionContext.UserId.Value,
                executionContext.ActiveMembershipId,
                organizationId,
                executionContext.TenantRootOrganizationId,
                AllowIdentityOwnedCancellation: false),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<GiftCardLifecycleOperationResult> ExecuteForPlatformAsync(
        Guid giftCardId,
        GiftCardLifecycleAction action,
        AdministerGiftCardLifecycleRequest request,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(giftCardId, "gift card");
        if (!executionContext.IsPlatformOperator ||
            executionContext.IsSystem ||
            executionContext.UserId is null ||
            !executionContext.HasPlatformPermission(
                PlatformPermissions.GiftCardsManageLifecycle))
        {
            throw new ForbiddenException(
                "gift_card.lifecycle.platform_permission.required",
                $"Permission '{PlatformPermissions.GiftCardsManageLifecycle}' is required.");
        }

        var intent = GiftCardLifecycleIntent.CreateAdministrative(action, request);
        return ExecuteAsync(
            giftCardId,
            intent,
            new LifecycleActor(
                GiftCardLifecycleActorType.PlatformOperator,
                executionContext.UserId.Value,
                MembershipId: null,
                TargetOrganizationId: null,
                TenantRootOrganizationId: null,
                AllowIdentityOwnedCancellation: true),
            cancellationToken);
    }

    public Task<GiftCardLifecycleOperationResult> ExecuteForOwnerAsync(
        Guid giftCardId,
        GiftCardLifecycleAction action,
        OwnGiftCardLifecycleRequest request,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(giftCardId, "gift card");
        if (!executionContext.IsAuthenticated ||
            executionContext.IsPlatformOperator ||
            executionContext.UserId is null)
        {
            throw new ForbiddenException(
                "gift_card.lifecycle.owner.required",
                "An authenticated cardholder is required.");
        }

        var intent = GiftCardLifecycleIntent.CreateOwner(action, request);
        return ExecuteAsync(
            giftCardId,
            intent,
            new LifecycleActor(
                GiftCardLifecycleActorType.IdentityOwner,
                executionContext.UserId.Value,
                MembershipId: null,
                TargetOrganizationId: null,
                TenantRootOrganizationId: null,
                AllowIdentityOwnedCancellation: false),
            cancellationToken);
    }

    internal Task<GiftCardLifecycleOperationResult> ExecuteSystemExpirationAsync(
        Guid giftCardId,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(giftCardId, "gift card");
        if (!executionContext.IsSystem ||
            !executionContext.IsPlatformOperator ||
            executionContext.UserId is null ||
            !executionContext.HasPlatformPermission(
                PlatformPermissions.GiftCardsManageLifecycle))
        {
            throw new ForbiddenException(
                "gift_card.lifecycle.system.required",
                "The trusted expiration-system context is required.");
        }

        return ExecuteAsync(
            giftCardId,
            GiftCardLifecycleIntent.CreateSystemExpiration(giftCardId),
            new LifecycleActor(
                GiftCardLifecycleActorType.System,
                executionContext.UserId.Value,
                MembershipId: null,
                TargetOrganizationId: null,
                TenantRootOrganizationId: null,
                AllowIdentityOwnedCancellation: true),
            cancellationToken);
    }

    private async Task<GiftCardLifecycleOperationResult> ExecuteAsync(
        Guid giftCardId,
        GiftCardLifecycleIntent intent,
        LifecycleActor actor,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await transactionCoordinator
                .BeginAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

            var cardHint = await dbContext.GiftCards
                .AsNoTracking()
                .SingleOrDefaultAsync(card => card.Id == giftCardId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw CardNotFound();
            ValidateActorScope(cardHint, actor, intent.Action);

            var existing = await dbContext.LifecycleEvents
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    lifecycleEvent =>
                        lifecycleEvent.GiftCardId == giftCardId &&
                        lifecycleEvent.IdempotencyKey == intent.IdempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (!existing.Matches(intent, actor.UserId))
                {
                    throw new ConflictException(
                        "gift_card.lifecycle.idempotency_key.reused",
                        "The idempotency key was already used for different lifecycle intent.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new GiftCardLifecycleOperationResult(
                    GiftCardMapping.ToResult(existing));
            }

            if (intent.Action is
                    (GiftCardLifecycleAction.Cancel or GiftCardLifecycleAction.Expire) &&
                cardHint.OwnershipState == GiftCardOwnershipState.AwaitingClaim &&
                cardHint.LifecycleState is not GiftCardLifecycleState.Cancelled and
                    not GiftCardLifecycleState.Expired &&
                cardHint.DistributionInvitationId is not null)
            {
                await distributionLifecycleWriter.CloseForCardLifecycleAsync(
                    new CloseDistributionForLifecycleRequest(
                        cardHint.DistributionInvitationId.Value,
                        cardHint.Id,
                        intent.Action == GiftCardLifecycleAction.Cancel
                            ? DistributionLifecycleClosure.Cancelled
                            : DistributionLifecycleClosure.Expired),
                    cancellationToken).ConfigureAwait(false);
            }

            await AcquireCardLockAsync(giftCardId, cancellationToken).ConfigureAwait(false);
            var card = await dbContext.GiftCards
                .SingleOrDefaultAsync(item => item.Id == giftCardId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw CardNotFound();
            ValidateActorScope(card, actor, intent.Action);

            var now = timeProvider.GetUtcNow();
            var previousState = card.LifecycleState;
            GiftCardValueReturnResult? valueReturn = null;
            if (intent.Action is GiftCardLifecycleAction.Cancel or GiftCardLifecycleAction.Expire)
            {
                await shareLifecycleWriter.CloseForSourceLifecycleAsync(
                    new CloseSharesForSourceLifecycleRequest(
                        card.Id,
                        intent.Action == GiftCardLifecycleAction.Cancel
                            ? ShareSourceLifecycleClosure.Cancelled
                            : ShareSourceLifecycleClosure.Expired),
                    cancellationToken).ConfigureAwait(false);
            }

            switch (intent.Action)
            {
                case GiftCardLifecycleAction.Suspend:
                    card.Suspend(now);
                    break;
                case GiftCardLifecycleAction.Reactivate:
                    card.Reactivate(now);
                    break;
                case GiftCardLifecycleAction.Cancel:
                    card.Cancel(now);
                    valueReturn = await ReturnValueAsync(
                        card,
                        GiftCardValueReturnReason.Cancellation,
                        intent,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case GiftCardLifecycleAction.Expire:
                    card.Expire(now);
                    valueReturn = await ReturnValueAsync(
                        card,
                        GiftCardValueReturnReason.Expiration,
                        intent,
                        cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ValidationFailedException(
                        "gift_card.lifecycle.action.invalid",
                        "The requested lifecycle action is invalid.");
            }

            var lifecycleEvent = GiftCardLifecycleEvent.Create(
                card,
                intent,
                previousState,
                actor.Type,
                actor.UserId,
                actor.MembershipId,
                executionContext.CorrelationId,
                valueReturn,
                now);
            dbContext.LifecycleEvents.Add(lifecycleEvent);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await RecordAuditAsync(card, lifecycleEvent, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new GiftCardLifecycleOperationResult(
                GiftCardMapping.ToResult(lifecycleEvent));
        }
        catch (Exception exception) when (IsConcurrencyConflict(exception))
        {
            throw new ConflictException(
                "gift_card.lifecycle.concurrent_conflict",
                "The gift card changed concurrently. Retry safely with the same idempotency key.");
        }
    }

    private async Task<GiftCardValueReturnResult> ReturnValueAsync(
        GiftCard card,
        GiftCardValueReturnReason reason,
        GiftCardLifecycleIntent intent,
        CancellationToken cancellationToken)
    {
        var operation = reason == GiftCardValueReturnReason.Cancellation
            ? "CANCEL"
            : "EXPIRE";
        return await ledgerWriter.RecordGiftCardValueReturnAsync(
            new RecordGiftCardValueReturnRequest(
                card.FundingOrganizationId,
                card.Id,
                card.IssuanceLedgerTransactionId,
                reason,
                $"GC-{operation}-{card.PublicReference}",
                CreateLedgerIdempotencyKey(card.Id, intent.IdempotencyKey)),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordAuditAsync(
        GiftCard card,
        GiftCardLifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken)
    {
        var actorType = lifecycleEvent.ActorType switch
        {
            GiftCardLifecycleActorType.OrganizationMember =>
                AuditActorType.OrganizationMember,
            GiftCardLifecycleActorType.IdentityOwner =>
                AuditActorType.IdentityUser,
            GiftCardLifecycleActorType.System => AuditActorType.System,
            _ => AuditActorType.PlatformOperator,
        };
        var operation = lifecycleEvent.Action switch
        {
            GiftCardLifecycleAction.Suspend => AuditOperations.GiftCardSuspended,
            GiftCardLifecycleAction.Reactivate => AuditOperations.GiftCardReactivated,
            GiftCardLifecycleAction.Cancel => AuditOperations.GiftCardCancelled,
            GiftCardLifecycleAction.Expire => AuditOperations.GiftCardExpired,
            _ => throw new InvalidOperationException("Unsupported lifecycle action."),
        };
        var metadata = new Dictionary<string, string>
        {
            ["fundingOrganizationId"] = card.FundingOrganizationId.ToString(),
            ["issuingOrganizationId"] = card.IssuingOrganizationId.ToString(),
            ["previousState"] = lifecycleEvent.PreviousState,
            ["newState"] = lifecycleEvent.NewState,
            ["reason"] = lifecycleEvent.Reason,
        };
        if (lifecycleEvent.ReturnedAmount is not null)
        {
            metadata["returnedAmount"] = lifecycleEvent.ReturnedAmount.Value.ToString(
                CultureInfo.InvariantCulture);
            metadata["currency"] = lifecycleEvent.Currency!;
            if (lifecycleEvent.LedgerTransactionId is not null)
            {
                metadata["ledgerTransactionId"] =
                    lifecycleEvent.LedgerTransactionId.Value.ToString();
            }
        }

        await auditRecorder.RecordAsync(
            new AuditEntry(
                lifecycleEvent.ActorUserId,
                actorType,
                card.FundingOrganizationId,
                operation,
                nameof(GiftCard),
                card.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                metadata,
                lifecycleEvent.ActorMembershipId),
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateActorScope(
        GiftCard card,
        LifecycleActor actor,
        GiftCardLifecycleAction action)
    {
        if (actor.Type == GiftCardLifecycleActorType.OrganizationMember &&
            (card.IssuingOrganizationId != actor.TargetOrganizationId ||
             card.FundingOrganizationId != actor.TenantRootOrganizationId))
        {
            throw CardNotFound();
        }

        if (actor.Type == GiftCardLifecycleActorType.IdentityOwner &&
            (card.OwnershipState != GiftCardOwnershipState.IdentityOwned ||
             card.OwnerUserId != actor.UserId))
        {
            throw CardNotFound();
        }

        if (action == GiftCardLifecycleAction.Cancel &&
            card.OwnershipState == GiftCardOwnershipState.IdentityOwned &&
            !actor.AllowIdentityOwnedCancellation)
        {
            throw new ForbiddenException(
                "gift_card.lifecycle.post_claim_cancellation.forbidden",
                "A company cannot cancel a card after recipient claim.");
        }
    }

    private Task<int> AcquireCardLockAsync(
        Guid giftCardId,
        CancellationToken cancellationToken)
    {
        var lockKey = $"gift-card|{giftCardId:D}";
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);
    }

    private static string CreateLedgerIdempotencyKey(
        Guid giftCardId,
        string lifecycleIdempotencyKey)
    {
        var canonical = $"{giftCardId:D}|{lifecycleIdempotencyKey}";
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return $"gift-card-return-{hash}";
    }

    private static bool IsConcurrencyConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException
                {
                    SqlState: UniqueViolation or SerializationFailure,
                })
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateIdentifier(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new ValidationFailedException(
                "gift_card.lifecycle.scope.required",
                $"A non-empty {name} identifier is required.");
        }
    }

    private static NotFoundException CardNotFound() =>
        new("gift_card.not_found", "Gift card not found.");

    private sealed record LifecycleActor(
        GiftCardLifecycleActorType Type,
        Guid UserId,
        Guid? MembershipId,
        Guid? TargetOrganizationId,
        Guid? TenantRootOrganizationId,
        bool AllowIdentityOwnedCancellation);
}
