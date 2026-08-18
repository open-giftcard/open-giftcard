using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.GiftCards.Domain;

namespace GiftCardPlatform.Modules.GiftCards.Application;

internal static class GiftCardMapping
{
    public static GiftCardResult ToResult(GiftCard card) =>
        new(
            card.Id,
            card.PublicReference,
            card.FundingOrganizationId,
            card.IssuingOrganizationId,
            card.OwnerOrganizationId,
            card.OwnerUserId,
            card.OwnershipState.ToString(),
            card.LifecycleState.ToString(),
            card.LedgerAccountId,
            card.IssuanceLedgerTransactionId,
            card.InitialValue,
            card.Currency,
            card.ValidFromUtc,
            card.ExpiresAtUtc,
            card.IsTransferable,
            card.IsDivisible,
            card.SourceGiftCardId,
            card.RootGiftCardId,
            card.Generation,
            card.DistributionInvitationId,
            card.DistributedAtUtc,
            card.ClaimedAtUtc,
            card.BusinessReference,
            card.IdempotencyKey,
            card.IssuedByUserId,
            card.IssuedByMembershipId,
            card.IssuedByPartnerClientId,
            card.IssuedAtUtc);

    public static GiftCardLifecycleEventResult ToResult(
        GiftCardLifecycleEvent lifecycleEvent) =>
        new(
            lifecycleEvent.Id,
            lifecycleEvent.GiftCardId,
            lifecycleEvent.FundingOrganizationId,
            lifecycleEvent.IssuingOrganizationId,
            lifecycleEvent.Action,
            lifecycleEvent.PreviousState,
            lifecycleEvent.NewState,
            lifecycleEvent.ActorType.ToString(),
            lifecycleEvent.ActorUserId,
            lifecycleEvent.ActorMembershipId,
            lifecycleEvent.CorrelationId,
            lifecycleEvent.Reason,
            lifecycleEvent.IdempotencyKey,
            lifecycleEvent.LedgerTransactionId,
            lifecycleEvent.ReturnedAmount,
            lifecycleEvent.Currency,
            lifecycleEvent.OccurredAtUtc);
}
