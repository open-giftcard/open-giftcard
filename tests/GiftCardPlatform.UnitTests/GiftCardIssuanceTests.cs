using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.GiftCards.Application;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.GiftCards.Domain;
using GiftCardPlatform.Modules.Ledger.Contracts;
using GiftCardPlatform.Modules.Partners.Contracts;

namespace GiftCardPlatform.UnitTests;

public sealed class GiftCardIssuanceTests
{
    private static readonly DateTimeOffset IssuedAt =
        new(2026, 7, 27, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Intent_normalizes_value_and_applies_conservative_policy_defaults()
    {
        var expiresAt = IssuedAt.AddYears(1);

        var intent = GiftCardIssuanceIntent.Create(
            new IssueGiftCardRequest(
                250.125m,
                " try ",
                ValidFromUtc: null,
                expiresAt,
                IsTransferable: null,
                IsDivisible: null,
                " EMPLOYEE-AWARD-42 ",
                " gift-card-award-42 "));

        Assert.Equal(250.125m, intent.Amount);
        Assert.Equal("TRY", intent.Currency);
        Assert.Null(intent.RequestedValidFromUtc);
        Assert.Equal(expiresAt, intent.ExpiresAtUtc);
        Assert.False(intent.IsTransferable);
        Assert.False(intent.IsDivisible);
        Assert.Equal("EMPLOYEE-AWARD-42", intent.BusinessReference);
        Assert.Equal("gift-card-award-42", intent.IdempotencyKey);
    }

    [Fact]
    public void Expiration_is_required_and_must_follow_validity_start()
    {
        var missing = Assert.Throws<ValidationFailedException>(() =>
            GiftCardIssuanceIntent.Create(
                Request(expiresAtUtc: null)));
        Assert.Equal("gift_card.expires_at.required", missing.Code);

        var invalid = Assert.Throws<ValidationFailedException>(() =>
            GiftCardIssuanceIntent.Create(
                Request(
                    expiresAtUtc: IssuedAt,
                    validFromUtc: IssuedAt)));
        Assert.Equal("gift_card.validity.invalid", invalid.Code);
    }

    [Fact]
    public void New_card_is_root_provenance_and_organization_inventory()
    {
        var cardId = Guid.CreateVersion7();
        var rootId = Guid.CreateVersion7();
        var issuingId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var transactionId = Guid.CreateVersion7();
        var actorId = Guid.CreateVersion7();
        var membershipId = Guid.CreateVersion7();
        var intent = GiftCardIssuanceIntent.Create(
            Request(expiresAtUtc: IssuedAt.AddYears(1)));

        var card = GiftCard.Create(
            cardId,
            "GC-0123456789ABCDEF0123",
            rootId,
            issuingId,
            intent,
            new GiftCardFundingResult(transactionId, accountId, IssuedAt),
            actorId,
            membershipId);

        Assert.Equal(GiftCardOwnershipState.OrganizationInventory, card.OwnershipState);
        Assert.Equal(GiftCardLifecycleState.Active, card.LifecycleState);
        Assert.Equal(issuingId, card.OwnerOrganizationId);
        Assert.Null(card.OwnerUserId);
        Assert.Equal(IssuedAt, card.ValidFromUtc);
        Assert.Null(card.SourceGiftCardId);
        Assert.Equal(cardId, card.RootGiftCardId);
        Assert.Equal(0, card.Generation);
        Assert.Equal(accountId, card.LedgerAccountId);
        Assert.Equal(transactionId, card.IssuanceLedgerTransactionId);
        Assert.Equal(membershipId, card.IssuedByMembershipId);
    }

    [Fact]
    public void Partner_minted_card_is_attributed_to_the_machine_credential()
    {
        var partnerClientId = Guid.CreateVersion7();
        var card = GiftCard.Create(
            Guid.CreateVersion7(),
            "GC-0123456789ABCDEF0123",
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            GiftCardIssuanceIntent.Create(Request(IssuedAt.AddYears(1))),
            new GiftCardFundingResult(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                IssuedAt),
            issuedByUserId: partnerClientId,
            issuedByMembershipId: null,
            issuedByPartnerClientId: partnerClientId);

        Assert.Null(card.IssuedByMembershipId);
        Assert.Equal(partnerClientId, card.IssuedByPartnerClientId);
    }

    [Fact]
    public void Partner_mint_requires_the_exact_live_principal_scope()
    {
        var context = new MutableExecutionContext();
        context.SetPartnerClient(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            []);

        var exception = Assert.Throws<ForbiddenException>(
            () => GiftCardIssuanceService.EnsurePartnerMayMint(context));
        Assert.Equal("partner.scope.gift_cards_mint.required", exception.Code);

        context.SetPartnerClient(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            [PartnerScopes.GiftCardsMint]);
        GiftCardIssuanceService.EnsurePartnerMayMint(context);
    }

    [Fact]
    public void Omitted_valid_from_is_idempotent_but_changed_financial_intent_is_not()
    {
        var rootId = Guid.CreateVersion7();
        var issuingId = Guid.CreateVersion7();
        var intent = GiftCardIssuanceIntent.Create(
            Request(expiresAtUtc: IssuedAt.AddYears(1)));
        var card = GiftCard.Create(
            Guid.CreateVersion7(),
            "GC-0123456789ABCDEF0123",
            rootId,
            issuingId,
            intent,
            new GiftCardFundingResult(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                IssuedAt),
            Guid.CreateVersion7(),
            Guid.CreateVersion7());

        Assert.True(card.Matches(rootId, issuingId, intent));
        Assert.False(
            card.Matches(
                rootId,
                issuingId,
                intent with { Amount = intent.Amount + 1m }));
    }

    [Fact]
    public void Distribution_and_claim_change_only_card_ownership_lifecycle()
    {
        var issuingOrganizationId = Guid.CreateVersion7();
        var card = GiftCard.Create(
            Guid.CreateVersion7(),
            "GC-0123456789ABCDEF0123",
            Guid.CreateVersion7(),
            issuingOrganizationId,
            GiftCardIssuanceIntent.Create(
                Request(expiresAtUtc: IssuedAt.AddYears(1))),
            new GiftCardFundingResult(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                IssuedAt),
            Guid.CreateVersion7(),
            Guid.CreateVersion7());
        var ledgerAccountId = card.LedgerAccountId;
        var ledgerTransactionId = card.IssuanceLedgerTransactionId;
        var invitationId = Guid.CreateVersion7();
        var ownerUserId = Guid.CreateVersion7();

        card.BeginDistribution(
            issuingOrganizationId,
            invitationId,
            IssuedAt.AddMinutes(1));
        Assert.Equal(GiftCardOwnershipState.AwaitingClaim, card.OwnershipState);
        Assert.Equal(GiftCardLifecycleState.AwaitingClaim, card.LifecycleState);
        Assert.Null(card.OwnerOrganizationId);
        Assert.Equal(invitationId, card.DistributionInvitationId);

        card.CompleteClaim(
            invitationId,
            ownerUserId,
            IssuedAt.AddMinutes(2));
        Assert.Equal(GiftCardOwnershipState.IdentityOwned, card.OwnershipState);
        Assert.Equal(GiftCardLifecycleState.Active, card.LifecycleState);
        Assert.Equal(ownerUserId, card.OwnerUserId);
        Assert.Equal(ledgerAccountId, card.LedgerAccountId);
        Assert.Equal(ledgerTransactionId, card.IssuanceLedgerTransactionId);
    }

    private static IssueGiftCardRequest Request(
        DateTimeOffset? expiresAtUtc,
        DateTimeOffset? validFromUtc = null) =>
        new(
            100m,
            "TRY",
            validFromUtc,
            expiresAtUtc,
            IsTransferable: null,
            IsDivisible: null,
            "AWARD-42",
            "gift-card-award-42");
}
