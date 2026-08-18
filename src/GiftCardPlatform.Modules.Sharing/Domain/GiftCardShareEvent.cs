namespace GiftCardPlatform.Modules.Sharing.Domain;

internal enum GiftCardShareEventType
{
    Created = 1,
    PinFailed = 2,
    Locked = 3,
    Claimed = 4,
    Cancelled = 5,
    Expired = 6,
}

internal sealed class GiftCardShareEvent
{
    private GiftCardShareEvent() { }

    private GiftCardShareEvent(
        GiftCardShare share,
        GiftCardShareEventType type,
        Guid actorUserId,
        DateTimeOffset occurredAtUtc)
    {
        Id = Guid.CreateVersion7();
        ShareId = share.Id;
        FundingOrganizationId = share.FundingOrganizationId;
        Type = type;
        ActorUserId = actorUserId;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
    }

    public Guid Id { get; private set; }

    public Guid ShareId { get; private set; }

    public Guid FundingOrganizationId { get; private set; }

    public GiftCardShareEventType Type { get; private set; }

    public Guid ActorUserId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public static GiftCardShareEvent Create(
        GiftCardShare share,
        GiftCardShareEventType type,
        Guid actorUserId,
        DateTimeOffset occurredAtUtc) =>
        new(share, type, actorUserId, occurredAtUtc);
}
