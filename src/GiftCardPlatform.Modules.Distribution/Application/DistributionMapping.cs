using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.Distribution.Domain;

namespace GiftCardPlatform.Modules.Distribution.Application;

internal static class DistributionMapping
{
    public static DistributionInvitationResult ToResult(
        DistributionInvitation invitation) =>
        new(
            invitation.Id,
            invitation.FundingOrganizationId,
            invitation.IssuingOrganizationId,
            invitation.GiftCardId,
            invitation.Kind,
            invitation.ContactType,
            invitation.MaskedRecipientContact,
            invitation.State.ToString(),
            invitation.ClaimExpiresAtUtc,
            invitation.FailedClaimAttempts,
            invitation.BusinessReference,
            invitation.IdempotencyKey,
            invitation.DistributedByUserId,
            invitation.DistributedByMembershipId,
            invitation.DistributedByPartnerClientId,
            invitation.DistributedAtUtc,
            invitation.ClaimedByUserId,
            invitation.ClaimedAtUtc);
}
