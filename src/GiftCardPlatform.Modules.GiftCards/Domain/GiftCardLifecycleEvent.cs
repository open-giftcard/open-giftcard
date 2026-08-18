using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.Ledger.Contracts;

namespace GiftCardPlatform.Modules.GiftCards.Domain;

internal enum GiftCardLifecycleActorType
{
    PlatformOperator = 1,
    OrganizationMember = 2,
    IdentityOwner = 3,
    System = 4,
}

internal sealed class GiftCardLifecycleEvent
{
    private GiftCardLifecycleEvent()
    {
        PreviousState = null!;
        NewState = null!;
        Reason = null!;
        IdempotencyKey = null!;
    }

    private GiftCardLifecycleEvent(
        Guid id,
        GiftCard card,
        GiftCardLifecycleIntent intent,
        GiftCardLifecycleState previousState,
        GiftCardLifecycleActorType actorType,
        Guid actorUserId,
        Guid? actorMembershipId,
        Guid correlationId,
        GiftCardValueReturnResult? valueReturn,
        DateTimeOffset occurredAtUtc)
    {
        Id = id;
        GiftCardId = card.Id;
        FundingOrganizationId = card.FundingOrganizationId;
        IssuingOrganizationId = card.IssuingOrganizationId;
        Action = intent.Action;
        PreviousState = previousState.ToString();
        NewState = card.LifecycleState.ToString();
        ActorType = actorType;
        ActorUserId = actorUserId;
        ActorMembershipId = actorMembershipId;
        CorrelationId = correlationId;
        Reason = intent.Reason;
        IdempotencyKey = intent.IdempotencyKey;
        LedgerTransactionId = valueReturn?.TransactionId;
        ReturnedAmount = valueReturn?.Amount;
        Currency = valueReturn?.Currency;
        OccurredAtUtc = Truncate(occurredAtUtc);
    }

    public Guid Id { get; private set; }

    public Guid GiftCardId { get; private set; }

    public Guid FundingOrganizationId { get; private set; }

    public Guid IssuingOrganizationId { get; private set; }

    public GiftCardLifecycleAction Action { get; private set; }

    public string PreviousState { get; private set; }

    public string NewState { get; private set; }

    public GiftCardLifecycleActorType ActorType { get; private set; }

    public Guid ActorUserId { get; private set; }

    public Guid? ActorMembershipId { get; private set; }

    public Guid CorrelationId { get; private set; }

    public string Reason { get; private set; }

    public string IdempotencyKey { get; private set; }

    public Guid? LedgerTransactionId { get; private set; }

    public decimal? ReturnedAmount { get; private set; }

    public string? Currency { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public static GiftCardLifecycleEvent Create(
        GiftCard card,
        GiftCardLifecycleIntent intent,
        GiftCardLifecycleState previousState,
        GiftCardLifecycleActorType actorType,
        Guid actorUserId,
        Guid? actorMembershipId,
        Guid correlationId,
        GiftCardValueReturnResult? valueReturn,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(intent);
        if (actorUserId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "gift_card.lifecycle.actor.required",
                "A lifecycle actor is required.");
        }

        if (correlationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "gift_card.lifecycle.correlation.required",
                "A lifecycle correlation identifier is required.");
        }

        if (actorType == GiftCardLifecycleActorType.OrganizationMember &&
            actorMembershipId is null)
        {
            throw new ValidationFailedException(
                "gift_card.lifecycle.membership.required",
                "An organization lifecycle event requires its authorizing membership.");
        }

        var isTerminal = intent.Action is
            GiftCardLifecycleAction.Cancel or GiftCardLifecycleAction.Expire;
        if (isTerminal != (valueReturn is not null))
        {
            throw new ValidationFailedException(
                "gift_card.lifecycle.value_return.invalid",
                "Terminal lifecycle events require a value-return result.");
        }

        return new GiftCardLifecycleEvent(
            Guid.CreateVersion7(),
            card,
            intent,
            previousState,
            actorType,
            actorUserId,
            actorMembershipId,
            correlationId,
            valueReturn,
            occurredAtUtc);
    }

    public bool Matches(
        GiftCardLifecycleIntent intent,
        Guid actorUserId) =>
        Action == intent.Action &&
        Reason == intent.Reason &&
        ActorUserId == actorUserId;

    private static DateTimeOffset Truncate(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }
}
