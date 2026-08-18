using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Ledger.Contracts;

namespace GiftCardPlatform.Modules.CorporateCredits.Domain;

internal sealed class CorporateCreditReversal
{
    private CorporateCreditReversal()
    {
        Currency = null!;
        Reason = null!;
        IdempotencyKey = null!;
    }

    private CorporateCreditReversal(
        Guid id,
        Guid allocationId,
        Guid organizationId,
        Guid ledgerTransactionId,
        decimal amount,
        string currency,
        string reason,
        string idempotencyKey,
        Guid reversedByUserId,
        DateTimeOffset reversedAtUtc)
    {
        Id = id;
        AllocationId = allocationId;
        OrganizationId = organizationId;
        LedgerTransactionId = ledgerTransactionId;
        Amount = amount;
        Currency = currency;
        Reason = reason;
        IdempotencyKey = idempotencyKey;
        ReversedByUserId = reversedByUserId;
        ReversedAtUtc = reversedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid AllocationId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid LedgerTransactionId { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; }

    public string Reason { get; private set; }

    public string IdempotencyKey { get; private set; }

    public Guid ReversedByUserId { get; private set; }

    public DateTimeOffset ReversedAtUtc { get; private set; }

    public static CorporateCreditReversal Create(
        CorporateCreditAllocation allocation,
        CorporateCreditReversalIntent intent,
        LedgerTransactionResult ledgerResult,
        Guid reversedByUserId)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(ledgerResult);

        if (reversedByUserId == Guid.Empty || ledgerResult.TransactionId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "corporate_credit.reversal.scope.required",
                "A reversal ledger transaction and initiating user are required.");
        }

        return new CorporateCreditReversal(
            Guid.CreateVersion7(),
            allocation.Id,
            allocation.OrganizationId,
            ledgerResult.TransactionId,
            allocation.Amount,
            allocation.Currency,
            intent.Reason,
            intent.IdempotencyKey,
            reversedByUserId,
            ledgerResult.PostedAtUtc);
    }

    public bool Matches(CorporateCreditReversalIntent intent) =>
        AllocationId == intent.AllocationId &&
        Reason == intent.Reason;
}
