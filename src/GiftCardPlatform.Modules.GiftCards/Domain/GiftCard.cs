using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Ledger.Contracts;

namespace GiftCardPlatform.Modules.GiftCards.Domain;

internal enum GiftCardOwnershipState
{
    OrganizationInventory = 1,
    AwaitingClaim = 2,
    IdentityOwned = 3,
}

internal enum GiftCardLifecycleState
{
    Active = 1,
    AwaitingClaim = 2,
    Suspended = 3,
    Cancelled = 4,
    Expired = 5,
}

internal sealed class GiftCard
{
    public const int CurrencyLength = 3;
    public const int AmountScale = 4;
    public const decimal MaximumAmount = 999_999_999_999_999.9999m;
    public const int PublicReferenceMaxLength = 32;
    public const int BusinessReferenceMaxLength = 120;
    public const int IdempotencyKeyMaxLength = 128;

    private GiftCard()
    {
        PublicReference = null!;
        Currency = null!;
        BusinessReference = null!;
        IdempotencyKey = null!;
    }

    private GiftCard(
        Guid id,
        string publicReference,
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        Guid ledgerAccountId,
        Guid issuanceLedgerTransactionId,
        decimal initialValue,
        string currency,
        DateTimeOffset validFromUtc,
        DateTimeOffset expiresAtUtc,
        bool isTransferable,
        bool isDivisible,
        string businessReference,
        string idempotencyKey,
        Guid issuedByUserId,
        Guid? issuedByMembershipId,
        Guid? issuedByPartnerClientId,
        DateTimeOffset issuedAtUtc)
    {
        Id = id;
        PublicReference = publicReference;
        FundingOrganizationId = fundingOrganizationId;
        IssuingOrganizationId = issuingOrganizationId;
        OwnerOrganizationId = issuingOrganizationId;
        OwnerUserId = null;
        OwnershipState = GiftCardOwnershipState.OrganizationInventory;
        LifecycleState = GiftCardLifecycleState.Active;
        LedgerAccountId = ledgerAccountId;
        IssuanceLedgerTransactionId = issuanceLedgerTransactionId;
        InitialValue = initialValue;
        Currency = currency;
        ValidFromUtc = TruncateToPostgresPrecision(validFromUtc);
        ExpiresAtUtc = TruncateToPostgresPrecision(expiresAtUtc);
        IsTransferable = isTransferable;
        IsDivisible = isDivisible;
        SourceGiftCardId = null;
        RootGiftCardId = id;
        Generation = 0;
        BusinessReference = businessReference;
        IdempotencyKey = idempotencyKey;
        IssuedByUserId = issuedByUserId;
        IssuedByMembershipId = issuedByMembershipId;
        IssuedByPartnerClientId = issuedByPartnerClientId;
        IssuedAtUtc = TruncateToPostgresPrecision(issuedAtUtc);
    }

    public Guid Id { get; private set; }

    public string PublicReference { get; private set; }

    public Guid FundingOrganizationId { get; private set; }

    public Guid IssuingOrganizationId { get; private set; }

    public Guid? OwnerOrganizationId { get; private set; }

    public Guid? OwnerUserId { get; private set; }

    public GiftCardOwnershipState OwnershipState { get; private set; }

    public GiftCardLifecycleState LifecycleState { get; private set; }

    public Guid LedgerAccountId { get; private set; }

    public Guid IssuanceLedgerTransactionId { get; private set; }

    public decimal InitialValue { get; private set; }

    public string Currency { get; private set; }

    public DateTimeOffset ValidFromUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public bool IsTransferable { get; private set; }

    public bool IsDivisible { get; private set; }

    public Guid? SourceGiftCardId { get; private set; }

    public Guid RootGiftCardId { get; private set; }

    public int Generation { get; private set; }

    public Guid? DistributionInvitationId { get; private set; }

    public DateTimeOffset? DistributedAtUtc { get; private set; }

    public DateTimeOffset? ClaimedAtUtc { get; private set; }

    public string BusinessReference { get; private set; }

    public string IdempotencyKey { get; private set; }

    public Guid IssuedByUserId { get; private set; }

    /// <summary>
    /// Null exactly when a partner minted the card: a machine credential has no
    /// organization membership to attribute to (ADR-053).
    /// </summary>
    public Guid? IssuedByMembershipId { get; private set; }

    /// <summary>
    /// The partner API client that minted the card, and null for every card a
    /// person issued. Exactly one of this and <see cref="IssuedByMembershipId"/>
    /// is set, enforced in the database.
    /// </summary>
    public Guid? IssuedByPartnerClientId { get; private set; }

    public DateTimeOffset IssuedAtUtc { get; private set; }

    public static GiftCard Create(
        Guid id,
        string publicReference,
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        GiftCardIssuanceIntent intent,
        GiftCardFundingResult funding,
        Guid issuedByUserId,
        Guid? issuedByMembershipId,
        Guid? issuedByPartnerClientId = null)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(funding);

        if (id == Guid.Empty ||
            fundingOrganizationId == Guid.Empty ||
            issuingOrganizationId == Guid.Empty ||
            funding.LedgerAccountId == Guid.Empty ||
            funding.TransactionId == Guid.Empty ||
            issuedByUserId == Guid.Empty ||
            // Exactly one attribution shape: a person acting through a
            // membership, or a partner credential. Neither, both, or an empty
            // identifier would leave a minted card with no traceable issuer.
            (issuedByMembershipId is null) == (issuedByPartnerClientId is null) ||
            issuedByMembershipId == Guid.Empty ||
            issuedByPartnerClientId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "gift_card.scope.required",
                "Card, organization, ledger, user, and membership identifiers are required.");
        }

        var normalizedReference = publicReference?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalizedReference.Length == 0 ||
            normalizedReference.Length > PublicReferenceMaxLength)
        {
            throw new ValidationFailedException(
                "gift_card.public_reference.invalid",
                "The public card reference is invalid.");
        }

        var validFrom = intent.RequestedValidFromUtc ?? funding.PostedAtUtc;
        if (intent.ExpiresAtUtc <= validFrom)
        {
            throw new ValidationFailedException(
                "gift_card.validity.invalid",
                "Expiration must be later than the effective validity start.");
        }

        return new GiftCard(
            id,
            normalizedReference,
            fundingOrganizationId,
            issuingOrganizationId,
            funding.LedgerAccountId,
            funding.TransactionId,
            intent.Amount,
            intent.Currency,
            validFrom,
            intent.ExpiresAtUtc,
            intent.IsTransferable,
            intent.IsDivisible,
            intent.BusinessReference,
            intent.IdempotencyKey,
            issuedByUserId,
            issuedByMembershipId,
            issuedByPartnerClientId,
            funding.PostedAtUtc);
    }

    public static GiftCard CreateSharedChild(
        GiftCard source,
        Guid childId,
        string publicReference,
        Guid recipientUserId,
        decimal amount,
        Guid ledgerAccountId,
        Guid ledgerTransactionId,
        Guid shareId,
        DateTimeOffset postedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (childId == Guid.Empty || recipientUserId == Guid.Empty ||
            ledgerAccountId == Guid.Empty || ledgerTransactionId == Guid.Empty ||
            shareId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "gift_card.share.scope.required",
                "Child, recipient, ledger, and share identifiers are required.");
        }

        var now = TruncateToPostgresPrecision(postedAtUtc);
        source.EnsureShareEligible(now);
        if (amount <= 0 || amount > MaximumAmount ||
            decimal.Round(amount, AmountScale) != amount)
        {
            throw new ValidationFailedException(
                "gift_card.share.amount.invalid",
                "Shared value must be positive and have no more than four decimal places.");
        }

        var normalizedReference = publicReference?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalizedReference.Length == 0 || normalizedReference.Length > PublicReferenceMaxLength)
        {
            throw new ValidationFailedException(
                "gift_card.public_reference.invalid",
                "The public card reference is invalid.");
        }

        return new GiftCard
        {
            Id = childId,
            PublicReference = normalizedReference,
            FundingOrganizationId = source.FundingOrganizationId,
            IssuingOrganizationId = source.IssuingOrganizationId,
            OwnerOrganizationId = null,
            OwnerUserId = recipientUserId,
            OwnershipState = GiftCardOwnershipState.IdentityOwned,
            LifecycleState = GiftCardLifecycleState.Active,
            LedgerAccountId = ledgerAccountId,
            IssuanceLedgerTransactionId = ledgerTransactionId,
            InitialValue = amount,
            Currency = source.Currency,
            ValidFromUtc = now,
            ExpiresAtUtc = source.ExpiresAtUtc,
            IsTransferable = source.IsTransferable,
            IsDivisible = source.IsDivisible,
            SourceGiftCardId = source.Id,
            RootGiftCardId = source.RootGiftCardId,
            Generation = checked(source.Generation + 1),
            DistributionInvitationId = null,
            DistributedAtUtc = null,
            ClaimedAtUtc = now,
            BusinessReference = $"SHARE-{shareId:N}",
            IdempotencyKey = $"share:{shareId:N}",
            IssuedByUserId = source.IssuedByUserId,
            IssuedByMembershipId = source.IssuedByMembershipId,
            IssuedByPartnerClientId = source.IssuedByPartnerClientId,
            IssuedAtUtc = now,
        };
    }

    public void EnsureShareEligible(DateTimeOffset now)
    {
        var occurredAt = TruncateToPostgresPrecision(now);
        if (OwnershipState != GiftCardOwnershipState.IdentityOwned ||
            LifecycleState != GiftCardLifecycleState.Active ||
            OwnerUserId is null || OwnerOrganizationId is not null ||
            !IsTransferable || !IsDivisible)
        {
            throw new ConflictException(
                "gift_card.share.ineligible",
                "The gift card is not eligible for partial sharing.");
        }

        EnsureWithinValidity(occurredAt);
    }

    /// <summary>
    /// Spending eligibility, deliberately distinct from
    /// <see cref="EnsureShareEligible"/>. Sharing additionally requires the card
    /// to be transferable and divisible, but both default to false (ADR-030), so
    /// reusing that check here would make an ordinarily issued card impossible
    /// to pay with. Those are policies about splitting value, not about spending
    /// it.
    /// </summary>
    public void EnsureSpendable(DateTimeOffset now)
    {
        var occurredAt = TruncateToPostgresPrecision(now);
        if (OwnershipState != GiftCardOwnershipState.IdentityOwned ||
            LifecycleState != GiftCardLifecycleState.Active ||
            OwnerUserId is null || OwnerOrganizationId is not null)
        {
            throw new ConflictException(
                "gift_card.payment.ineligible",
                "The gift card cannot be used for payment.");
        }

        EnsureWithinValidity(occurredAt);
    }

    public void EnsureRefundable()
    {
        if (OwnershipState != GiftCardOwnershipState.IdentityOwned ||
            LifecycleState is not GiftCardLifecycleState.Active and
                not GiftCardLifecycleState.Suspended ||
            OwnerUserId is null || OwnerOrganizationId is not null)
        {
            throw new ConflictException(
                "gift_card.refund.ineligible",
                "The gift card cannot receive a payment refund.");
        }
    }

    public bool MatchesSharedChild(
        Guid sourceGiftCardId,
        Guid recipientUserId,
        decimal amount,
        Guid ledgerAccountId,
        Guid ledgerTransactionId,
        Guid shareId) =>
        SourceGiftCardId == sourceGiftCardId &&
        OwnerUserId == recipientUserId &&
        InitialValue == amount &&
        LedgerAccountId == ledgerAccountId &&
        IssuanceLedgerTransactionId == ledgerTransactionId &&
        IdempotencyKey == $"share:{shareId:N}";

    public bool Matches(
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        GiftCardIssuanceIntent intent) =>
        FundingOrganizationId == fundingOrganizationId &&
        IssuingOrganizationId == issuingOrganizationId &&
        InitialValue == intent.Amount &&
        Currency == intent.Currency &&
        ExpiresAtUtc == TruncateToPostgresPrecision(intent.ExpiresAtUtc) &&
        (intent.RequestedValidFromUtc is null ||
         ValidFromUtc == TruncateToPostgresPrecision(intent.RequestedValidFromUtc.Value)) &&
        IsTransferable == intent.IsTransferable &&
        IsDivisible == intent.IsDivisible &&
        BusinessReference == intent.BusinessReference;

    public void BeginDistribution(
        Guid ownerOrganizationId,
        Guid invitationId,
        DateTimeOffset now)
    {
        if (ownerOrganizationId == Guid.Empty || invitationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "gift_card.distribution.scope.required",
                "Owner organization and invitation identifiers are required.");
        }

        var occurredAt = TruncateToPostgresPrecision(now);
        if (OwnershipState == GiftCardOwnershipState.AwaitingClaim &&
            LifecycleState == GiftCardLifecycleState.AwaitingClaim &&
            DistributionInvitationId == invitationId)
        {
            return;
        }

        if (OwnershipState != GiftCardOwnershipState.OrganizationInventory ||
            LifecycleState != GiftCardLifecycleState.Active ||
            OwnerOrganizationId != ownerOrganizationId ||
            OwnerUserId is not null ||
            DistributionInvitationId is not null)
        {
            throw new ConflictException(
                "gift_card.distribution.ineligible",
                "The gift card is not eligible for distribution from this organization inventory.");
        }

        EnsureWithinValidity(occurredAt);
        OwnerOrganizationId = null;
        OwnerUserId = null;
        OwnershipState = GiftCardOwnershipState.AwaitingClaim;
        LifecycleState = GiftCardLifecycleState.AwaitingClaim;
        DistributionInvitationId = invitationId;
        DistributedAtUtc = occurredAt;
        ClaimedAtUtc = null;
    }

    public void CompleteClaim(
        Guid invitationId,
        Guid ownerUserId,
        DateTimeOffset now)
    {
        if (invitationId == Guid.Empty || ownerUserId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "gift_card.claim.scope.required",
                "Invitation and recipient identity identifiers are required.");
        }

        var occurredAt = TruncateToPostgresPrecision(now);
        if (OwnershipState == GiftCardOwnershipState.IdentityOwned &&
            LifecycleState == GiftCardLifecycleState.Active &&
            DistributionInvitationId == invitationId &&
            OwnerUserId == ownerUserId)
        {
            return;
        }

        if (OwnershipState != GiftCardOwnershipState.AwaitingClaim ||
            LifecycleState != GiftCardLifecycleState.AwaitingClaim ||
            DistributionInvitationId != invitationId ||
            OwnerOrganizationId is not null ||
            OwnerUserId is not null)
        {
            throw new ConflictException(
                "gift_card.claim.ineligible",
                "The gift card is not awaiting this claim.");
        }

        EnsureWithinValidity(occurredAt);
        OwnerUserId = ownerUserId;
        OwnershipState = GiftCardOwnershipState.IdentityOwned;
        LifecycleState = GiftCardLifecycleState.Active;
        ClaimedAtUtc = occurredAt;
    }

    public void Suspend(DateTimeOffset now)
    {
        var occurredAt = TruncateToPostgresPrecision(now);
        EnsureNotExpired(occurredAt);
        if (LifecycleState == GiftCardLifecycleState.Suspended)
        {
            throw new ConflictException(
                "gift_card.lifecycle.already_suspended",
                "The gift card is already suspended.");
        }

        EnsureNonTerminal();
        if (LifecycleState is not GiftCardLifecycleState.Active and
            not GiftCardLifecycleState.AwaitingClaim)
        {
            throw InvalidTransition();
        }

        LifecycleState = GiftCardLifecycleState.Suspended;
    }

    public void Reactivate(DateTimeOffset now)
    {
        var occurredAt = TruncateToPostgresPrecision(now);
        EnsureNotExpired(occurredAt);
        EnsureNonTerminal();
        if (LifecycleState != GiftCardLifecycleState.Suspended)
        {
            throw InvalidTransition();
        }

        LifecycleState = OwnershipState == GiftCardOwnershipState.AwaitingClaim
            ? GiftCardLifecycleState.AwaitingClaim
            : GiftCardLifecycleState.Active;
    }

    public void Cancel(DateTimeOffset now)
    {
        var occurredAt = TruncateToPostgresPrecision(now);
        EnsureNotExpired(occurredAt);
        EnsureNonTerminal();
        LifecycleState = GiftCardLifecycleState.Cancelled;
    }

    public void Expire(DateTimeOffset now)
    {
        var occurredAt = TruncateToPostgresPrecision(now);
        EnsureNonTerminal();
        if (ExpiresAtUtc > occurredAt)
        {
            throw new ConflictException(
                "gift_card.lifecycle.not_expired",
                "The gift card has not reached its expiration time.");
        }

        LifecycleState = GiftCardLifecycleState.Expired;
    }

    private void EnsureWithinValidity(DateTimeOffset occurredAt)
    {
        if (ValidFromUtc > occurredAt)
        {
            throw new ConflictException(
                "gift_card.not_yet_valid",
                "The gift card is not yet valid.");
        }

        EnsureNotExpired(occurredAt);
    }

    private void EnsureNotExpired(DateTimeOffset occurredAt)
    {
        if (ExpiresAtUtc <= occurredAt)
        {
            throw new ConflictException(
                "gift_card.expired",
                "The gift card has expired.");
        }
    }

    private void EnsureNonTerminal()
    {
        if (LifecycleState is GiftCardLifecycleState.Cancelled or
            GiftCardLifecycleState.Expired)
        {
            throw new ConflictException(
                "gift_card.lifecycle.terminal",
                "The gift card is already in a terminal lifecycle state.");
        }
    }

    private static ConflictException InvalidTransition() =>
        new(
            "gift_card.lifecycle.transition.invalid",
            "The requested gift-card lifecycle transition is invalid.");

    private static DateTimeOffset TruncateToPostgresPrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }
}
