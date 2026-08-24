using System.Data;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.Distribution.Domain;
using GiftCardPlatform.Modules.Distribution.Infrastructure;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GiftCardPlatform.Modules.Distribution.Application;

internal sealed record PreparedGiftCardDistribution(
    DistributionInvitationResult Result,
    GiftCardClaimNotification? Notification);

internal sealed class GiftCardDistributionService(
    DistributionDbContext dbContext,
    IGiftCardOwnershipWriter giftCardOwnershipWriter,
    IOrganizationPermissionAuthorizer organizationAuthorizer,
    IAuditRecorder auditRecorder,
    IGiftCardClaimNotifier notifier,
    INotificationChannelAvailability notificationChannels,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    IOptions<DistributionOptions> options,
    TimeProvider timeProvider) : IGiftCardDistributionService
{
    private const string UniqueViolation = "23505";
    private const string SerializationFailure = "40001";
    private readonly DistributionOptions distributionOptions = options.Value;

    public async Task<DistributionInvitationResult> DistributeAsync(
        Guid organizationId,
        DistributeGiftCardRequest request,
        CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(
                organizationId,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        return prepared.Result;
    }

    internal async Task<PreparedGiftCardDistribution> PrepareAsync(
        Guid organizationId,
        DistributeGiftCardRequest request,
        CancellationToken cancellationToken)
    {
        EnsureOrganization(organizationId);
        var intent = DistributionIntent.Create(request);
        await organizationAuthorizer
            .RequirePermissionAsync(
                organizationId,
                OrganizationPermissions.GiftCardsDistribute,
                cancellationToken)
            .ConfigureAwait(false);
        var fundingOrganizationId = executionContext.TenantRootOrganizationId
            ?? throw new ForbiddenException(
                "auth.unauthenticated",
                "A verified organization membership is required.");
        var actorUserId = executionContext.UserId!.Value;
        var actorMembershipId = executionContext.ActiveMembershipId!.Value;
        return await PrepareCoreAsync(
                organizationId,
                fundingOrganizationId,
                actorUserId,
                actorMembershipId,
                intent,
                actorUserId,
                AuditActorType.OrganizationMember,
                actorMembershipId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<PreparedGiftCardDistribution> PrepareAcceptedBatchItemAsync(
        Guid organizationId,
        Guid fundingOrganizationId,
        Guid acceptedByUserId,
        Guid acceptedByMembershipId,
        DistributeGiftCardRequest request,
        CancellationToken cancellationToken)
    {
        if (!executionContext.IsSystem ||
            executionContext.UserId != SystemActorIds.BulkGiftCardBatch)
        {
            throw new ForbiddenException(
                "bulk.processor.system_required",
                "Only the bulk-batch system processor may distribute an accepted item.");
        }

        EnsureOrganization(organizationId);
        if (fundingOrganizationId == Guid.Empty ||
            acceptedByUserId == Guid.Empty ||
            acceptedByMembershipId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "bulk.processor.scope.required",
                "Accepted batch funding and actor attribution are required.");
        }
        return await PrepareCoreAsync(
                organizationId,
                fundingOrganizationId,
                acceptedByUserId,
                acceptedByMembershipId,
                DistributionIntent.Create(request),
                SystemActorIds.BulkGiftCardBatch,
                AuditActorType.System,
                auditMembershipId: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PreparedGiftCardDistribution> PrepareCoreAsync(
        Guid organizationId,
        Guid fundingOrganizationId,
        Guid distributedByUserId,
        Guid distributedByMembershipId,
        DistributionIntent intent,
        Guid auditActorId,
        AuditActorType auditActorType,
        Guid? auditMembershipId,
        CancellationToken cancellationToken)
    {
        ClaimTokenCodec.IssuedClaimToken? claimToken = null;
        DistributionInvitation invitation;

        await using (var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            var existing = await dbContext.Invitations
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item =>
                        item.FundingOrganizationId == fundingOrganizationId &&
                        item.IdempotencyKey == intent.IdempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (!existing.Matches(fundingOrganizationId, organizationId, intent))
                {
                    throw new ConflictException(
                        "distribution.idempotency_key.reused",
                        "The idempotency key was already used for different distribution intent.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PreparedGiftCardDistribution(
                    DistributionMapping.ToResult(existing),
                    Notification: null);
            }

            notificationChannels.RequireAvailable(
                intent.ContactType == RecipientContactType.Email
                    ? NotificationChannel.Email
                    : NotificationChannel.Sms);

            var now = timeProvider.GetUtcNow();
            var invitationId = Guid.CreateVersion7();
            claimToken = ClaimTokenCodec.Create(invitationId);
            invitation = DistributionInvitation.Create(
                invitationId,
                fundingOrganizationId,
                organizationId,
                intent,
                claimToken.SecretHash,
                now.AddHours(distributionOptions.ClaimTokenLifetimeHours),
                distributedByUserId,
                distributedByMembershipId,
                now);

            var card = await giftCardOwnershipWriter
                .BeginDistributionAsync(
                    new BeginGiftCardDistributionRequest(
                        intent.GiftCardId,
                        organizationId,
                        invitation.Id),
                    cancellationToken)
                .ConfigureAwait(false);
            if (card.FundingOrganizationId != fundingOrganizationId ||
                card.IssuingOrganizationId != organizationId ||
                card.DistributionInvitationId != invitation.Id ||
                !string.Equals(card.OwnershipState, "AwaitingClaim", StringComparison.Ordinal) ||
                !string.Equals(card.LifecycleState, "AwaitingClaim", StringComparison.Ordinal))
            {
                throw new ConflictException(
                    "distribution.gift_card.ineligible",
                    "The gift card did not enter the expected awaiting-claim state.");
            }

            dbContext.Invitations.Add(invitation);
            dbContext.Events.Add(DistributionEvent.Distributed(invitation, now));

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await auditRecorder.RecordAsync(
                    new AuditEntry(
                        ActorUserId: auditActorId,
                        ActorType: auditActorType,
                        OrganizationScopeId: organizationId,
                        Operation: AuditOperations.GiftCardDistributed,
                        EntityType: nameof(DistributionInvitation),
                        EntityId: invitation.Id.ToString(),
                        Outcome: AuditOutcome.Success,
                        CorrelationId: executionContext.CorrelationId,
                        Metadata: new Dictionary<string, string>
                        {
                            ["giftCardId"] = invitation.GiftCardId.ToString(),
                            ["fundingOrganizationId"] =
                                invitation.FundingOrganizationId.ToString(),
                            ["contactType"] = invitation.ContactType!.Value.ToString(),
                            ["maskedRecipient"] = invitation.MaskedRecipientContact!,
                            ["businessReference"] = invitation.BusinessReference,
                            ["acceptedByUserId"] = distributedByUserId.ToString(),
                            ["acceptedByMembershipId"] =
                                distributedByMembershipId.ToString(),
                        },
                        ActorMembershipId: auditMembershipId),
                    cancellationToken).ConfigureAwait(false);
                await notifier
                    .SendAsync(
                        new GiftCardClaimNotification(
                            invitation.Id,
                            invitation.IssuingOrganizationId,
                            invitation.ContactType!.Value,
                            invitation.RecipientContact!,
                            claimToken.RawToken,
                            invitation.ClaimExpiresAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsConcurrencyConflict(exception))
            {
                throw new ConflictException(
                    "distribution.concurrent_conflict",
                    "A concurrent distribution conflicted. Retry safely with the same idempotency key.");
            }
        }

        return new PreparedGiftCardDistribution(
            DistributionMapping.ToResult(invitation),
            Notification: null);
    }

    private static void EnsureOrganization(Guid organizationId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "distribution.organization.required",
                "An issuing organization is required.");
        }
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
}
