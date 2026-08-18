using GiftCardPlatform.Modules.Distribution.Contracts;

namespace GiftCardPlatform.Modules.Distribution.Domain;

internal enum DistributionEventType
{
    Distributed = 1,
    ClaimFailed = 2,
    Claimed = 3,
    ClaimExpired = 4,
    CardCancelled = 5,
    CardExpired = 6,
}

internal sealed class DistributionEvent
{
    private DistributionEvent()
    {
    }

    private DistributionEvent(
        Guid id,
        Guid fundingOrganizationId,
        Guid invitationId,
        Guid giftCardId,
        DistributionEventType type,
        Guid? actorUserId,
        Guid? actorMembershipId,
        DateTimeOffset occurredAtUtc)
    {
        Id = id;
        FundingOrganizationId = fundingOrganizationId;
        InvitationId = invitationId;
        GiftCardId = giftCardId;
        Type = type;
        ActorUserId = actorUserId;
        ActorMembershipId = actorMembershipId;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
    }

    public Guid Id { get; private set; }

    public Guid FundingOrganizationId { get; private set; }

    public Guid InvitationId { get; private set; }

    public Guid GiftCardId { get; private set; }

    public DistributionEventType Type { get; private set; }

    public Guid? ActorUserId { get; private set; }

    public Guid? ActorMembershipId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public static DistributionEvent Distributed(
        DistributionInvitation invitation,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return new DistributionEvent(
            Guid.CreateVersion7(),
            invitation.FundingOrganizationId,
            invitation.Id,
            invitation.GiftCardId,
            DistributionEventType.Distributed,
            invitation.DistributedByUserId,
            invitation.DistributedByMembershipId,
            occurredAtUtc);
    }

    public static DistributionEvent ClaimFailed(
        DistributionInvitation invitation,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return new DistributionEvent(
            Guid.CreateVersion7(),
            invitation.FundingOrganizationId,
            invitation.Id,
            invitation.GiftCardId,
            invitation.State == DistributionInvitationState.Expired
                ? DistributionEventType.ClaimExpired
                : DistributionEventType.ClaimFailed,
            actorUserId: null,
            actorMembershipId: null,
            occurredAtUtc: occurredAtUtc);
    }

    public static DistributionEvent Claimed(
        DistributionInvitation invitation,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return new DistributionEvent(
            Guid.CreateVersion7(),
            invitation.FundingOrganizationId,
            invitation.Id,
            invitation.GiftCardId,
            DistributionEventType.Claimed,
            invitation.ClaimedByUserId,
            actorMembershipId: null,
            occurredAtUtc: occurredAtUtc);
    }

    public static DistributionEvent CardLifecycleClosed(
        DistributionInvitation invitation,
        DistributionLifecycleClosure closure,
        Guid actorUserId,
        Guid? actorMembershipId,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return new DistributionEvent(
            Guid.CreateVersion7(),
            invitation.FundingOrganizationId,
            invitation.Id,
            invitation.GiftCardId,
            closure == DistributionLifecycleClosure.Cancelled
                ? DistributionEventType.CardCancelled
                : DistributionEventType.CardExpired,
            actorUserId,
            actorMembershipId,
            occurredAtUtc);
    }
}
