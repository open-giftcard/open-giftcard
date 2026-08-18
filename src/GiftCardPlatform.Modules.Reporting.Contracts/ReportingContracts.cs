namespace GiftCardPlatform.Modules.Reporting.Contracts;

public sealed record ReportingPageRequest(int Limit, string? Cursor)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
}

public sealed record OrganizationFinancialHistoryRequest(
    int Limit,
    string? Cursor,
    string? Category,
    string? Operation,
    string? Currency,
    string? Reference,
    DateTimeOffset? OccurredFromUtc,
    DateTimeOffset? OccurredBeforeUtc);

public sealed record OrganizationFinancialCurrencySummary(
    string Currency,
    decimal Granted,
    decimal Reversed,
    decimal Issued,
    decimal Distributed,
    decimal RemainingCorporateCredit,
    decimal RemainingGiftCardValue,
    decimal CancelledReturned,
    decimal ExpiredReturned,
    decimal Spent,
    decimal Refunded,
    decimal NetSpent);

public sealed record OrganizationFinancialSummary(
    Guid OrganizationId,
    DateTimeOffset AsOfUtc,
    IReadOnlyList<OrganizationFinancialCurrencySummary> Currencies);

public sealed record FinancialHistoryItem(
    string EventKey,
    string Category,
    string Operation,
    Guid EntityId,
    Guid? GiftCardId,
    string? GiftCardPublicReference,
    string? BusinessReference,
    decimal? Amount,
    string? Currency,
    string FinancialDirection,
    string? State,
    Guid? ActorUserId,
    DateTimeOffset OccurredAtUtc);

public sealed record FinancialHistoryPage(
    IReadOnlyList<FinancialHistoryItem> Items,
    int Limit,
    string? NextCursor);

public sealed record OwnedGiftCardSummary(
    Guid Id,
    string PublicReference,
    string LifecycleState,
    decimal FundedAmount,
    decimal Balance,
    decimal ReservedBalance,
    decimal AvailableBalance,
    string Currency,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? ClaimedAtUtc,
    DateTimeOffset IssuedAtUtc);

public sealed record OwnedGiftCardPage(
    IReadOnlyList<OwnedGiftCardSummary> Items,
    int Limit,
    string? NextCursor);

public sealed record OwnedGiftCardDetail(
    Guid Id,
    string PublicReference,
    Guid FundingOrganizationId,
    Guid IssuingOrganizationId,
    string OwnershipState,
    string LifecycleState,
    decimal FundedAmount,
    decimal Balance,
    decimal ReservedBalance,
    decimal AvailableBalance,
    string Currency,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ExpiresAtUtc,
    bool IsTransferable,
    bool IsDivisible,
    Guid RootGiftCardId,
    int Generation,
    Guid? DistributionInvitationId,
    DateTimeOffset? DistributedAtUtc,
    DateTimeOffset? ClaimedAtUtc,
    DateTimeOffset IssuedAtUtc);

public enum ReconciliationSeverity
{
    Error = 1,
    Warning = 2,
}

public sealed record ReconciliationFinding(
    string Code,
    ReconciliationSeverity Severity,
    string EntityType,
    string EntityId,
    string? Currency,
    decimal? ExpectedAmount,
    decimal? ActualAmount,
    string Message);

public sealed record OrganizationReconciliationResult(
    Guid OrganizationId,
    DateTimeOffset CheckedAtUtc,
    bool IsConsistent,
    int TransactionsChecked,
    int GiftCardsChecked,
    int SharesChecked,
    int ActiveReservationsChecked,
    IReadOnlyList<ReconciliationFinding> Findings);

/// <summary>
/// One card the organization funded, as the organization is permitted to see
/// it (ADR-052).
///
/// <paramref name="RemainingBalance"/> is null exactly when an identity owns
/// the card. The company funded it and does receive the remainder back on
/// cancellation or expiry, but a live per-card balance keyed to a named
/// employee is a spending monitor, and the aggregate finance actually needs is
/// already reported per currency by the financial summary. While the card is
/// still in organization inventory or awaiting claim nobody else owns that
/// money, so the balance is returned.
/// </summary>
public sealed record OrganizationCardRegisterItem(
    Guid GiftCardId,
    string PublicReference,
    string LifecycleState,
    string OwnershipState,
    decimal FundedAmount,
    string Currency,
    decimal? RemainingBalance,
    Guid IssuingOrganizationId,
    string? MaskedRecipientContact,
    bool IsTransferable,
    bool IsDivisible,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset? DistributedAtUtc,
    DateTimeOffset? ClaimedAtUtc);

/// <summary>
/// Bounded, exact-match filters. Every value is normalized and parameterized
/// server-side; <paramref name="Reference"/> is a literal case-insensitive
/// match with PostgreSQL wildcards escaped, never a caller-supplied pattern.
/// </summary>
public sealed record OrganizationCardRegisterRequest(
    int Limit,
    string? Cursor,
    string? LifecycleState = null,
    string? OwnershipState = null,
    string? Currency = null,
    string? Reference = null)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
}

public sealed record OrganizationCardRegisterPage(
    IReadOnlyList<OrganizationCardRegisterItem> Items,
    int Limit,
    string? NextCursor);

/// <summary>
/// The organization's own register of what it has issued.
///
/// Distinct from gift-card inventory, which lists only cards still in
/// organization ownership and therefore loses sight of a card at the moment it
/// reaches the person it was issued for.
/// </summary>
public interface IOrganizationCardRegisterQuery
{
    Task<OrganizationCardRegisterPage> GetRegisterAsync(
        Guid organizationId,
        OrganizationCardRegisterRequest request,
        CancellationToken cancellationToken);
}

public interface IFinancialReportingQuery
{
    Task<OrganizationFinancialSummary> GetOrganizationSummaryAsync(
        Guid organizationId,
        CancellationToken cancellationToken);

    Task<FinancialHistoryPage> GetOrganizationHistoryAsync(
        Guid organizationId,
        OrganizationFinancialHistoryRequest request,
        CancellationToken cancellationToken);

    Task<OrganizationReconciliationResult> ReconcileOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken);

    Task<OwnedGiftCardPage> GetMyGiftCardsAsync(
        ReportingPageRequest request,
        CancellationToken cancellationToken);

    Task<OwnedGiftCardDetail> GetMyGiftCardAsync(
        Guid giftCardId,
        CancellationToken cancellationToken);

    Task<FinancialHistoryPage> GetMyGiftCardHistoryAsync(
        Guid giftCardId,
        ReportingPageRequest request,
        CancellationToken cancellationToken);
}

public sealed record PaymentReportRequest(
    int Limit,
    string? Cursor,
    Guid? PosClientId,
    Guid? PosTerminalId,
    Guid? FundingOrganizationId,
    string? StoreReference,
    string? State,
    string? Currency,
    string? Reference,
    DateTimeOffset? OccurredFromUtc,
    DateTimeOffset? OccurredBeforeUtc);

public sealed record PaymentReportItem(
    Guid PaymentProvisionId,
    Guid FundingOrganizationId,
    Guid GiftCardId,
    string GiftCardPublicReference,
    Guid PosClientId,
    string PosClientCode,
    string PosClientDisplayName,
    Guid PosTerminalId,
    string PosTerminalCode,
    string StoreReference,
    string? PosTransactionReference,
    decimal ProvisionedAmount,
    decimal? ConfirmedAmount,
    decimal RefundedAmount,
    decimal NetAmount,
    string Currency,
    string State,
    bool IsFullyReversed,
    int RefundCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? SettledAtUtc,
    Guid? RedemptionLedgerTransactionId);

public sealed record PaymentReportCurrencyTotals(
    string Currency,
    long PaymentCount,
    long ConfirmedPaymentCount,
    long RefundCount,
    long FullyReversedPaymentCount,
    decimal ProvisionedAmount,
    decimal ConfirmedAmount,
    decimal RefundedAmount,
    decimal NetAmount);

public sealed record PaymentReportPage(
    IReadOnlyList<PaymentReportItem> Items,
    int Limit,
    string? NextCursor,
    long TotalMatchingPayments,
    IReadOnlyList<PaymentReportCurrencyTotals> PageTotals,
    IReadOnlyList<PaymentReportCurrencyTotals> MatchingTotals);

public sealed record PaymentRefundReportLine(
    Guid RefundId,
    Guid PosTerminalId,
    string PosTerminalCode,
    string StoreReference,
    string? PosTransactionReference,
    string Reason,
    decimal Amount,
    Guid RefundLedgerTransactionId,
    DateTimeOffset RefundedAtUtc);

public sealed record PaymentReceiptReport(
    PaymentReportItem Payment,
    IReadOnlyList<PaymentRefundReportLine> Refunds);

public interface IPaymentReportingQuery
{
    Task<PaymentReportPage> GetPaymentsAsync(
        PaymentReportRequest request,
        CancellationToken cancellationToken);

    Task<PaymentReceiptReport> GetPaymentAsync(
        Guid paymentProvisionId,
        CancellationToken cancellationToken);
}
