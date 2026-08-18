using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.GiftCards.Domain;
using GiftCardPlatform.Modules.Ledger.Contracts;

namespace GiftCardPlatform.UnitTests;

public sealed class GiftCardLifecycleTests
{
    private static readonly DateTimeOffset IssuedAt =
        new(2026, 7, 27, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Administrative_intent_normalizes_reason_and_idempotency_key()
    {
        var intent = GiftCardLifecycleIntent.CreateAdministrative(
            GiftCardLifecycleAction.Suspend,
            new AdministerGiftCardLifecycleRequest(
                "  Suspected recipient compromise.  ",
                "  lifecycle-command-42  "));

        Assert.Equal(GiftCardLifecycleAction.Suspend, intent.Action);
        Assert.Equal("Suspected recipient compromise.", intent.Reason);
        Assert.Equal("lifecycle-command-42", intent.IdempotencyKey);
    }

    [Fact]
    public void Owner_intent_allows_only_suspend_and_reactivate()
    {
        var suspend = GiftCardLifecycleIntent.CreateOwner(
            GiftCardLifecycleAction.Suspend,
            new OwnGiftCardLifecycleRequest("owner-suspend-42"));

        Assert.Equal(
            GiftCardLifecycleIntent.OwnerSuspendReason,
            suspend.Reason);

        var exception = Assert.Throws<ForbiddenException>(() =>
            GiftCardLifecycleIntent.CreateOwner(
                GiftCardLifecycleAction.Cancel,
                new OwnGiftCardLifecycleRequest("owner-cancel-42")));
        Assert.Equal(
            "gift_card.lifecycle.owner_action.forbidden",
            exception.Code);
    }

    [Fact]
    public void Inventory_card_can_be_suspended_and_reactivated()
    {
        var card = CreateCard();

        card.Suspend(IssuedAt.AddMinutes(1));
        Assert.Equal(GiftCardLifecycleState.Suspended, card.LifecycleState);
        Assert.Equal(
            GiftCardOwnershipState.OrganizationInventory,
            card.OwnershipState);

        card.Reactivate(IssuedAt.AddMinutes(2));
        Assert.Equal(GiftCardLifecycleState.Active, card.LifecycleState);
        Assert.Equal(
            GiftCardOwnershipState.OrganizationInventory,
            card.OwnershipState);
    }

    [Fact]
    public void Awaiting_claim_reactivation_restores_awaiting_claim_state()
    {
        var card = CreateCard();
        var invitationId = Guid.CreateVersion7();
        card.BeginDistribution(
            card.IssuingOrganizationId,
            invitationId,
            IssuedAt.AddMinutes(1));

        card.Suspend(IssuedAt.AddMinutes(2));
        card.Reactivate(IssuedAt.AddMinutes(3));

        Assert.Equal(GiftCardOwnershipState.AwaitingClaim, card.OwnershipState);
        Assert.Equal(GiftCardLifecycleState.AwaitingClaim, card.LifecycleState);
        Assert.Equal(invitationId, card.DistributionInvitationId);
    }

    [Fact]
    public void Cancellation_and_expiration_are_terminal()
    {
        var cancelled = CreateCard();
        cancelled.Cancel(IssuedAt.AddMinutes(1));
        Assert.Equal(GiftCardLifecycleState.Cancelled, cancelled.LifecycleState);

        var cancelledError = Assert.Throws<ConflictException>(() =>
            cancelled.Reactivate(IssuedAt.AddMinutes(2)));
        Assert.Equal("gift_card.lifecycle.terminal", cancelledError.Code);

        var expired = CreateCard(expiresAt: IssuedAt.AddMinutes(5));
        var premature = Assert.Throws<ConflictException>(() =>
            expired.Expire(IssuedAt.AddMinutes(4)));
        Assert.Equal("gift_card.lifecycle.not_expired", premature.Code);

        expired.Expire(IssuedAt.AddMinutes(5));
        Assert.Equal(GiftCardLifecycleState.Expired, expired.LifecycleState);

        var expiredError = Assert.Throws<ConflictException>(() =>
            expired.Suspend(IssuedAt.AddMinutes(6)));
        Assert.Equal("gift_card.expired", expiredError.Code);
    }

    [Fact]
    public void Distribution_and_claim_respect_the_card_validity_window()
    {
        var notYetValid = CreateCard(
            validFrom: IssuedAt.AddMinutes(5),
            expiresAt: IssuedAt.AddHours(1));

        var earlyDistribution = Assert.Throws<ConflictException>(() =>
            notYetValid.BeginDistribution(
                notYetValid.IssuingOrganizationId,
                Guid.CreateVersion7(),
                IssuedAt.AddMinutes(4)));
        Assert.Equal("gift_card.not_yet_valid", earlyDistribution.Code);

        var expiring = CreateCard(expiresAt: IssuedAt.AddMinutes(5));
        var invitationId = Guid.CreateVersion7();
        expiring.BeginDistribution(
            expiring.IssuingOrganizationId,
            invitationId,
            IssuedAt.AddMinutes(4));

        var lateClaim = Assert.Throws<ConflictException>(() =>
            expiring.CompleteClaim(
                invitationId,
                Guid.CreateVersion7(),
                IssuedAt.AddMinutes(5)));
        Assert.Equal("gift_card.expired", lateClaim.Code);
    }

    [Fact]
    public void Terminal_event_requires_and_records_the_value_return()
    {
        var card = CreateCard();
        var intent = GiftCardLifecycleIntent.CreateAdministrative(
            GiftCardLifecycleAction.Cancel,
            new AdministerGiftCardLifecycleRequest(
                "Recipient request.",
                "lifecycle-cancel-42"));
        var previous = card.LifecycleState;
        card.Cancel(IssuedAt.AddMinutes(1));

        var transactionId = Guid.CreateVersion7();
        var lifecycleEvent = GiftCardLifecycleEvent.Create(
            card,
            intent,
            previous,
            GiftCardLifecycleActorType.OrganizationMember,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new GiftCardValueReturnResult(
                transactionId,
                100m,
                "TRY",
                IssuedAt.AddMinutes(1)),
            IssuedAt.AddMinutes(1));

        Assert.Equal("Active", lifecycleEvent.PreviousState);
        Assert.Equal("Cancelled", lifecycleEvent.NewState);
        Assert.Equal(transactionId, lifecycleEvent.LedgerTransactionId);
        Assert.Equal(100m, lifecycleEvent.ReturnedAmount);
        Assert.Equal("TRY", lifecycleEvent.Currency);
    }

    [Fact]
    public void Zero_balance_terminal_event_has_no_fabricated_ledger_transaction()
    {
        var card = CreateCard();
        var intent = GiftCardLifecycleIntent.CreateAdministrative(
            GiftCardLifecycleAction.Cancel,
            new AdministerGiftCardLifecycleRequest(
                "Close consumed card.",
                "lifecycle-zero-cancel"));
        var previous = card.LifecycleState;
        card.Cancel(IssuedAt.AddMinutes(1));

        var lifecycleEvent = GiftCardLifecycleEvent.Create(
            card,
            intent,
            previous,
            GiftCardLifecycleActorType.OrganizationMember,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new GiftCardValueReturnResult(
                TransactionId: null,
                Amount: 0m,
                Currency: "TRY",
                ProcessedAtUtc: IssuedAt.AddMinutes(1)),
            IssuedAt.AddMinutes(1));

        Assert.Equal(0m, lifecycleEvent.ReturnedAmount);
        Assert.Null(lifecycleEvent.LedgerTransactionId);
        Assert.Equal("TRY", lifecycleEvent.Currency);
    }

    private static GiftCard CreateCard(
        DateTimeOffset? validFrom = null,
        DateTimeOffset? expiresAt = null)
    {
        var fundingOrganizationId = Guid.CreateVersion7();
        var issuingOrganizationId = Guid.CreateVersion7();
        var intent = GiftCardIssuanceIntent.Create(
            new IssueGiftCardRequest(
                100m,
                "TRY",
                validFrom,
                expiresAt ?? IssuedAt.AddYears(1),
                IsTransferable: null,
                IsDivisible: null,
                "LIFECYCLE-TEST",
                "lifecycle-test-card"));

        return GiftCard.Create(
            Guid.CreateVersion7(),
            "GC-0123456789ABCDEF0123",
            fundingOrganizationId,
            issuingOrganizationId,
            intent,
            new GiftCardFundingResult(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                IssuedAt),
            Guid.CreateVersion7(),
            Guid.CreateVersion7());
    }
}
