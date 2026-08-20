namespace GiftCardPlatform.Modules.Ledger.Contracts;

public sealed record RecordCorporateCreditRequest(
    Guid OrganizationId,
    decimal Amount,
    string Currency,
    string BusinessReference,
    string IdempotencyKey);

public sealed record RecordCorporateCreditReversalRequest(
    Guid OrganizationId,
    Guid OriginalTransactionId,
    decimal Amount,
    string Currency,
    string BusinessReference,
    string IdempotencyKey);

public sealed record RecordGiftCardIssuanceRequest(
    Guid FundingOrganizationId,
    Guid GiftCardId,
    decimal Amount,
    string Currency,
    string BusinessReference,
    string IdempotencyKey);

public sealed record LedgerTransactionResult(
    Guid TransactionId,
    DateTimeOffset PostedAtUtc);

public sealed record GiftCardFundingResult(
    Guid TransactionId,
    Guid LedgerAccountId,
    DateTimeOffset PostedAtUtc);

public enum GiftCardValueReturnReason
{
    Cancellation = 1,
    Expiration = 2,
}

public sealed record RecordGiftCardValueReturnRequest(
    Guid FundingOrganizationId,
    Guid GiftCardId,
    Guid IssuanceLedgerTransactionId,
    GiftCardValueReturnReason Reason,
    string BusinessReference,
    string IdempotencyKey);

public sealed record GiftCardValueReturnResult(
    Guid? TransactionId,
    decimal Amount,
    string Currency,
    DateTimeOffset ProcessedAtUtc);

public sealed record LedgerBalanceResult(string Currency, decimal Amount);

/// <summary>
/// Financial write boundary owned by Ledger. Callers describe business intent;
/// they never select ledger accounts or construct postings themselves.
/// </summary>
public interface ILedgerWriter
{
    Task<LedgerTransactionResult> RecordCorporateCreditAsync(
        RecordCorporateCreditRequest request,
        CancellationToken cancellationToken);

    Task<LedgerTransactionResult> RecordCorporateCreditReversalAsync(
        RecordCorporateCreditReversalRequest request,
        CancellationToken cancellationToken);

    Task<GiftCardFundingResult> RecordGiftCardIssuanceAsync(
        RecordGiftCardIssuanceRequest request,
        CancellationToken cancellationToken);

    Task<GiftCardValueReturnResult> RecordGiftCardValueReturnAsync(
        RecordGiftCardValueReturnRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Read boundary for balances derived from immutable ledger entries.
/// </summary>
public interface ILedgerBalanceQuery
{
    Task<IReadOnlyList<LedgerBalanceResult>> GetOrganizationCorporateCreditBalancesAsync(
        Guid organizationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Narrow Payments collaboration boundary. Separate from
/// <see cref="IGiftCardShareLedger"/> because that interface also prepares and
/// records share transfers, which payment provisioning must not be able to do:
/// a provision reserves value and posts nothing (ADR-033).
///
/// The implementation takes the same card-scoped advisory lock as sharing, so a
/// share and a payment cannot both read a stale balance and each spend it.
/// </summary>
public interface IGiftCardPaymentLedger
{
    Task<GiftCardLockedBalanceResult> GetLockedBalanceAsync(
        Guid giftCardId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the posted balance without taking the card's value lock.
    ///
    /// For answering "what is this worth", never for deciding what to reserve or
    /// post. <see cref="GetLockedBalanceAsync"/> exists because a share and a
    /// payment must serialise on one card; a balance inquiry has no such need,
    /// and taking that lock on a read a till can repeat would let an inquiry
    /// contend with every payment on the card.
    ///
    /// The number is therefore a snapshot that can be stale the moment it is
    /// returned, which is correct for a display value and wrong for a decision.
    /// </summary>
    Task<GiftCardLockedBalanceResult> GetBalanceAsync(
        Guid giftCardId,
        CancellationToken cancellationToken);

    Task<GiftCardRedemptionResult> RecordRedemptionAsync(
        RecordGiftCardRedemptionRequest request,
        CancellationToken cancellationToken);

    Task<GiftCardRefundLedgerResult> RecordRefundAsync(
        RecordGiftCardRefundRequest request,
        CancellationToken cancellationToken);
}

public sealed record RecordGiftCardRedemptionRequest(
    Guid PaymentTokenId,
    Guid ProvisionId,
    Guid FundingOrganizationId,
    Guid GiftCardId,
    decimal Amount,
    string Currency,
    string BusinessReference);

public sealed record GiftCardRedemptionResult(
    Guid TransactionId,
    Guid SettlementLedgerAccountId,
    DateTimeOffset PostedAtUtc);

public sealed record RecordGiftCardRefundRequest(
    Guid PaymentTokenId,
    Guid ProvisionId,
    Guid RefundId,
    Guid OriginalRedemptionTransactionId,
    Guid FundingOrganizationId,
    Guid GiftCardId,
    decimal Amount,
    string Currency,
    string BusinessReference);

public sealed record GiftCardRefundLedgerResult(
    Guid TransactionId,
    DateTimeOffset PostedAtUtc);

public sealed record GiftCardShareTransferPlan(
    Guid LedgerTransactionId,
    Guid ChildLedgerAccountId);

public sealed record GiftCardLockedBalanceResult(
    Guid GiftCardId,
    Guid LedgerAccountId,
    string Currency,
    decimal Amount);

public sealed record RecordGiftCardShareTransferRequest(
    Guid ShareId,
    Guid FundingOrganizationId,
    Guid SourceGiftCardId,
    Guid ChildGiftCardId,
    decimal Amount,
    string Currency,
    string BusinessReference,
    string IdempotencyKey,
    GiftCardShareTransferPlan Plan);

public sealed record GiftCardShareTransferResult(
    Guid TransactionId,
    Guid ChildLedgerAccountId,
    DateTimeOffset PostedAtUtc);

/// <summary>
/// Ledger-owned sharing boundary. Sharing may request a plan and describe
/// business intent, but Ledger selects the existing source account, derives
/// posted balance, constructs postings, and guarantees balancing.
/// </summary>
public interface IGiftCardShareLedger
{
    GiftCardShareTransferPlan PrepareTransfer();

    Task<GiftCardLockedBalanceResult> GetLockedBalanceAsync(
        Guid giftCardId,
        CancellationToken cancellationToken);

    Task<GiftCardShareTransferResult> RecordTransferAsync(
        RecordGiftCardShareTransferRequest request,
        CancellationToken cancellationToken);
}
