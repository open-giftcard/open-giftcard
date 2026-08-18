namespace GiftCardPlatform.Modules.CorporateCredits.Contracts;

public sealed record AllocateCorporateCreditRequest(
    Guid OrganizationId,
    decimal Amount,
    string? Currency,
    string? BusinessReference,
    string? IdempotencyKey);

public sealed record CorporateCreditAllocationResult(
    Guid Id,
    Guid OrganizationId,
    Guid LedgerTransactionId,
    decimal Amount,
    string Currency,
    string BusinessReference,
    string IdempotencyKey,
    DateTimeOffset AllocatedAtUtc);

public sealed record ReverseCorporateCreditRequest(
    Guid AllocationId,
    string? Reason,
    string? IdempotencyKey);

public sealed record CorporateCreditReversalResult(
    Guid Id,
    Guid AllocationId,
    Guid OrganizationId,
    Guid LedgerTransactionId,
    decimal Amount,
    string Currency,
    string Reason,
    string IdempotencyKey,
    DateTimeOffset ReversedAtUtc);

public sealed record CorporateCreditBalanceResult(string Currency, decimal Amount);

public sealed record CorporateCreditAllocationHistoryItem(
    Guid Id,
    Guid OrganizationId,
    Guid LedgerTransactionId,
    decimal Amount,
    string Currency,
    string BusinessReference,
    Guid AllocatedByUserId,
    DateTimeOffset AllocatedAtUtc,
    CorporateCreditReversalSummary? Reversal);

public sealed record CorporateCreditReversalSummary(
    Guid Id,
    Guid LedgerTransactionId,
    string Reason,
    Guid ReversedByUserId,
    DateTimeOffset ReversedAtUtc);

public sealed record CorporateCreditHistoryRequest(int Limit, string? Cursor)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
}

public sealed record CorporateCreditHistoryPage(
    IReadOnlyList<CorporateCreditAllocationHistoryItem> Items,
    int Limit,
    string? NextCursor);

public interface ICorporateCreditAllocationService
{
    Task<CorporateCreditAllocationResult> AllocateAsync(
        AllocateCorporateCreditRequest request,
        CancellationToken cancellationToken);
}

public interface ICorporateCreditReversalService
{
    Task<CorporateCreditReversalResult> ReverseAsync(
        ReverseCorporateCreditRequest request,
        CancellationToken cancellationToken);
}

public interface ICorporateCreditQueryService
{
    Task<IReadOnlyList<CorporateCreditBalanceResult>> GetBalancesAsync(
        Guid organizationId,
        CancellationToken cancellationToken);

    Task<CorporateCreditHistoryPage> GetAllocationHistoryAsync(
        Guid organizationId,
        CorporateCreditHistoryRequest request,
        CancellationToken cancellationToken);
}
