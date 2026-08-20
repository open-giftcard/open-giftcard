using GiftCardPlatform.BuildingBlocks.Errors;

namespace GiftCardPlatform.Modules.Payments.Domain;

internal enum PaymentProvisionState
{
    Active = 1,
    Confirmed = 2,
    Cancelled = 3,
    Expired = 4,
}

/// <summary>
/// A time-bounded hold on card value taken before a sale is final (ADR-033).
///
/// An active provision reduces available value but posts nothing to the Ledger —
/// that only happens on confirmation (ADR-018). Cancellation and expiry release
/// the hold with no financial effect at all.
/// </summary>
internal sealed class PaymentProvision
{
    public const int PosTransactionReferenceMaxLength = 64;
    public const int GiftCardPublicReferenceMaxLength = 32;
    public const decimal MaximumAmount = 1_000_000_000m;
    public const int AmountScale = 4;

    public Guid Id { get; private init; }

    /// <summary>
    /// The credential this provision consumed. Unique, so one credential can
    /// never produce two holds (ADR-017 single use).
    /// </summary>
    public Guid PaymentTokenId { get; private init; }

    public Guid GiftCardId { get; private init; }

    public string GiftCardPublicReference { get; private init; } = string.Empty;

    public Guid FundingOrganizationId { get; private init; }

    public Guid OwnerUserId { get; private init; }

    public Guid PosClientId { get; private init; }

    public Guid PosTerminalId { get; private init; }

    public string StoreReference { get; private init; } = string.Empty;

    /// <summary>
    /// Recorded for reconciliation, receipts, and disputes only. It is
    /// deliberately not what prevents a double charge — that is the
    /// server-issued credential (ADR-018).
    /// </summary>
    public string? PosTransactionReference { get; private init; }

    /// <summary>The value actually held. Never above <see cref="RequestedAmount"/>.</summary>
    public decimal Amount { get; private init; }

    /// <summary>
    /// What the till asked for, which is the sale total it was trying to settle.
    ///
    /// Stored rather than derived because a partial approval is a fact about the
    /// sale that outlives the request: reconciliation, receipts, and disputes all
    /// need to know that a hold of 30 was the answer to a question about 50, and
    /// a later GET of this provision cannot reconstruct that from the hold alone.
    /// Equal to <see cref="Amount"/> whenever the card covered the whole sale.
    /// </summary>
    public decimal RequestedAmount { get; private init; }

    /// <summary>True when the card could not cover the whole sale.</summary>
    public bool IsPartialApproval => Amount < RequestedAmount;

    public string Currency { get; private init; } = string.Empty;

    public PaymentProvisionState State { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private init; }

    public DateTimeOffset ExpiresAtUtc { get; private init; }

    public DateTimeOffset? SettledAtUtc { get; private set; }

    /// <summary>The amount actually posted. It may be below <see cref="Amount"/>.</summary>
    public decimal? ConfirmedAmount { get; private set; }

    /// <summary>Immutable link to the one Ledger redemption created from this hold.</summary>
    public Guid? RedemptionLedgerTransactionId { get; private set; }

    public static PaymentProvision Create(
        Guid id,
        Guid paymentTokenId,
        Guid giftCardId,
        string giftCardPublicReference,
        Guid fundingOrganizationId,
        Guid ownerUserId,
        Guid posClientId,
        Guid posTerminalId,
        string storeReference,
        string? posTransactionReference,
        decimal amount,
        decimal requestedAmount,
        string currency,
        DateTimeOffset now,
        int windowSeconds)
    {
        if (id == Guid.Empty || paymentTokenId == Guid.Empty || giftCardId == Guid.Empty ||
            fundingOrganizationId == Guid.Empty || ownerUserId == Guid.Empty ||
            posClientId == Guid.Empty || posTerminalId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "payment.provision.scope.required",
                "Provision, credential, card, owner, and POS identifiers are required.");
        }

        var publicReference = giftCardPublicReference?.Trim() ?? string.Empty;
        if (publicReference.Length is 0 or > GiftCardPublicReferenceMaxLength)
        {
            throw new ValidationFailedException(
                "payment.provision.gift_card_reference.invalid",
                "A valid gift-card public reference is required.");
        }

        if (amount <= 0 || amount > MaximumAmount ||
            decimal.Round(amount, AmountScale) != amount)
        {
            throw new ValidationFailedException(
                "payment.provision.amount.invalid",
                "A provision amount must be positive with at most four decimal places.");
        }

        if (requestedAmount <= 0 || requestedAmount > MaximumAmount ||
            decimal.Round(requestedAmount, AmountScale) != requestedAmount)
        {
            throw new ValidationFailedException(
                "payment.provision.requested_amount.invalid",
                "A requested amount must be positive with at most four decimal places.");
        }

        // Holding more than the till asked for would let a partial approval
        // overcharge, which is the one way this feature could take money that
        // was never requested.
        if (amount > requestedAmount)
        {
            throw new ValidationFailedException(
                "payment.provision.amount.above_requested",
                "A hold can never exceed the amount the till requested.");
        }

        if (windowSeconds <= 0)
        {
            throw new ValidationFailedException(
                "payment.provision.window.invalid",
                "The provision window must be positive.");
        }

        var reference = posTransactionReference?.Trim();
        if (reference is { Length: 0 })
        {
            reference = null;
        }

        if (reference is not null && reference.Length > PosTransactionReferenceMaxLength)
        {
            throw new ValidationFailedException(
                "payment.provision.pos_reference.invalid",
                $"A POS transaction reference may be at most {PosTransactionReferenceMaxLength} characters.");
        }

        var createdAt = TruncateToPostgresPrecision(now);
        return new PaymentProvision
        {
            Id = id,
            PaymentTokenId = paymentTokenId,
            GiftCardId = giftCardId,
            GiftCardPublicReference = publicReference,
            FundingOrganizationId = fundingOrganizationId,
            OwnerUserId = ownerUserId,
            PosClientId = posClientId,
            PosTerminalId = posTerminalId,
            StoreReference = storeReference,
            PosTransactionReference = reference,
            Amount = amount,
            RequestedAmount = requestedAmount,
            Currency = currency,
            State = PaymentProvisionState.Active,
            CreatedAtUtc = createdAt,
            ExpiresAtUtc = createdAt.AddSeconds(windowSeconds),
        };
    }

    /// <summary>
    /// True while the hold still reserves value. Expiry is derived from the
    /// server clock, so a provision stops holding value at its deadline whether
    /// or not the sweep has run yet — the same shape as gift-card expiration
    /// under ADR-035.
    /// </summary>
    public bool IsHolding(DateTimeOffset now) =>
        State == PaymentProvisionState.Active &&
        ExpiresAtUtc > TruncateToPostgresPrecision(now);

    /// <summary>Releases the hold at the till's request. Posts nothing.</summary>
    public void Cancel(DateTimeOffset now)
    {
        EnsureActive("payment.provision.not_cancellable");
        State = PaymentProvisionState.Cancelled;
        SettledAtUtc = TruncateToPostgresPrecision(now);
    }

    /// <summary>Releases the hold because its window elapsed. Posts nothing.</summary>
    public void Expire(DateTimeOffset now)
    {
        EnsureActive("payment.provision.not_expirable");
        State = PaymentProvisionState.Expired;
        SettledAtUtc = TruncateToPostgresPrecision(now);
    }

    /// <summary>
    /// Settles this hold as one terminal redemption. A smaller charge releases
    /// the uncharged remainder; there is no partially consumed state.
    /// </summary>
    public void Confirm(
        decimal amount,
        Guid redemptionLedgerTransactionId,
        DateTimeOffset now)
    {
        EnsureCanConfirm(amount, now);

        if (redemptionLedgerTransactionId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "payment.confirmation.ledger_transaction.required",
                "A redemption ledger transaction is required.");
        }

        State = PaymentProvisionState.Confirmed;
        ConfirmedAmount = amount;
        RedemptionLedgerTransactionId = redemptionLedgerTransactionId;
        SettledAtUtc = TruncateToPostgresPrecision(now);
    }

    public bool MatchesConfirmation(decimal amount) =>
        State == PaymentProvisionState.Confirmed &&
        ConfirmedAmount == amount &&
        RedemptionLedgerTransactionId is not null;

    public void EnsureCanConfirm(decimal amount, DateTimeOffset now)
    {
        if (amount <= 0 || amount > MaximumAmount ||
            decimal.Round(amount, AmountScale) != amount)
        {
            throw new ValidationFailedException(
                "payment.confirmation.amount.invalid",
                "A confirmation amount must be positive with at most four decimal places.");
        }

        if (!IsHolding(now))
        {
            throw new ConflictException(
                "payment.provision.not_confirmable",
                "The payment provision cannot be confirmed.");
        }

        if (amount > Amount)
        {
            throw new ConflictException(
                "payment.confirmation.amount.exceeds_provision",
                "The confirmation amount exceeds the authorised payment provision.");
        }
    }

    private void EnsureActive(string code)
    {
        if (State != PaymentProvisionState.Active)
        {
            throw new ConflictException(
                code,
                "The payment provision is no longer active.");
        }
    }

    private static DateTimeOffset TruncateToPostgresPrecision(DateTimeOffset value) =>
        new(value.UtcDateTime.Ticks - (value.UtcDateTime.Ticks % 10), TimeSpan.Zero);
}
