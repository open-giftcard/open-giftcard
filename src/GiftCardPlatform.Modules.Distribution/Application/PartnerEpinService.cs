using System.Data;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.Distribution.Domain;
using GiftCardPlatform.Modules.Distribution.Infrastructure;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.Partners.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GiftCardPlatform.Modules.Distribution.Application;

internal sealed class PartnerEpinService(
    DistributionDbContext dbContext,
    IPartnerGiftCardIssuanceService issuanceService,
    IGiftCardOwnershipWriter ownershipWriter,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    IOptions<PartnersOptions> options,
    TimeProvider timeProvider) : IPartnerEpinService
{
    private const string UniqueViolation = "23505";
    private const string SerializationFailure = "40001";
    private readonly PartnersOptions partnerOptions = options.Value;

    public async Task<MintedPartnerEpinResult> MintAsync(
        MintPartnerEpinRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Issuance);

        if (request.Issuance.Amount > partnerOptions.MaximumEpinAmount)
        {
            throw new ValidationFailedException(
                "partner.epin.amount.limit_exceeded",
                $"An e-pin amount cannot exceed {partnerOptions.MaximumEpinAmount} currency units.");
        }

        var deliveryKey = Convert.FromBase64String(partnerOptions.EpinDeliveryKey);
        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        // Issuance performs the authoritative principal/scope validation and
        // normalizes all monetary and idempotency fields inside this same
        // serializable transaction.
        var card = await issuanceService
            .MintAsync(request.Issuance, cancellationToken)
            .ConfigureAwait(false);
        var partnerClientId = executionContext.PartnerClientId
            ?? throw new ForbiddenException(
                "partner.principal.required",
                "An authenticated partner API client is required.");

        var existing = await dbContext.Invitations
            .SingleOrDefaultAsync(
                invitation =>
                    invitation.FundingOrganizationId == card.FundingOrganizationId &&
                    invitation.IdempotencyKey == card.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.MatchesOrphanMint(
                    card.FundingOrganizationId,
                    card.Id,
                    partnerClientId,
                    card.BusinessReference))
            {
                throw new ConflictException(
                    "partner.epin.idempotency_key.reused",
                    "The idempotency key was already used for a different e-pin mint.");
            }

            var replay = EpinCredentialCodec.Create(existing.Id, deliveryKey);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToResult(card, existing, replay);
        }

        var now = timeProvider.GetUtcNow();
        var invitationId = Guid.CreateVersion7(now);
        var credential = EpinCredentialCodec.Create(invitationId, deliveryKey);
        var invitation = DistributionInvitation.CreateOrphanPin(
            invitationId,
            card.FundingOrganizationId,
            card.Id,
            credential.ClaimSecretHash,
            credential.PinHash,
            now.AddDays(partnerOptions.OrphanClaimLifetimeDays),
            card.BusinessReference,
            card.IdempotencyKey,
            partnerClientId,
            now);

        card = await ownershipWriter
            .BeginPartnerEpinDistributionAsync(
                new BeginPartnerEpinDistributionRequest(card.Id, invitation.Id),
                cancellationToken)
            .ConfigureAwait(false);
        if (card.DistributionInvitationId != invitation.Id ||
            !string.Equals(card.OwnershipState, "AwaitingClaim", StringComparison.Ordinal) ||
            !string.Equals(card.LifecycleState, "AwaitingClaim", StringComparison.Ordinal))
        {
            throw new ConflictException(
                "partner.epin.gift_card.ineligible",
                "The minted card did not enter the expected awaiting-claim state.");
        }

        dbContext.Invitations.Add(invitation);
        dbContext.Events.Add(DistributionEvent.Distributed(invitation, now));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await auditRecorder.RecordAsync(
                new AuditEntry(
                    ActorUserId: partnerClientId,
                    ActorType: AuditActorType.PartnerClient,
                    OrganizationScopeId: card.FundingOrganizationId,
                    Operation: AuditOperations.GiftCardDistributed,
                    EntityType: nameof(DistributionInvitation),
                    EntityId: invitation.Id.ToString(),
                    Outcome: AuditOutcome.Success,
                    CorrelationId: executionContext.CorrelationId,
                    Metadata: new Dictionary<string, string>
                    {
                        ["giftCardId"] = card.Id.ToString(),
                        ["fundingOrganizationId"] = card.FundingOrganizationId.ToString(),
                        ["invitationKind"] = DistributionInvitationKind.OrphanPin.ToString(),
                        ["partnerClientId"] = partnerClientId.ToString(),
                        ["businessReference"] = card.BusinessReference,
                    }),
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (FindSqlState(exception) is UniqueViolation or SerializationFailure)
        {
            throw new ConflictException(
                "partner.epin.concurrent_conflict",
                "A concurrent e-pin mint conflicted. Retry safely with the same idempotency key.");
        }

        return ToResult(card, invitation, credential);
    }

    private MintedPartnerEpinResult ToResult(
        GiftCardResult card,
        DistributionInvitation invitation,
        IssuedEpinCredential credential)
    {
        var separator = partnerOptions.ClaimBaseUrl.Contains('?', StringComparison.Ordinal)
            ? '&'
            : '?';
        var claimUrl =
            $"{partnerOptions.ClaimBaseUrl}{separator}token={Uri.EscapeDataString(credential.ClaimToken)}";
        return new MintedPartnerEpinResult(
            card,
            invitation.Id,
            claimUrl,
            credential.Pin,
            invitation.ClaimExpiresAtUtc);
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
