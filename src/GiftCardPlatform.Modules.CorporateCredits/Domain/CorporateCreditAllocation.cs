using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Ledger.Contracts;

namespace GiftCardPlatform.Modules.CorporateCredits.Domain;

internal sealed class CorporateCreditAllocation
{
    public const int CurrencyLength = 3;
    public const int BusinessReferenceMaxLength = 120;
    public const int IdempotencyKeyMaxLength = 128;

    private CorporateCreditAllocation()
    {
        Currency = null!;
        BusinessReference = null!;
        IdempotencyKey = null!;
    }

    private CorporateCreditAllocation(
        Guid id,
        Guid organizationId,
        Guid ledgerTransactionId,
        decimal amount,
        string currency,
        string businessReference,
        string idempotencyKey,
        Guid allocatedByUserId,
        DateTimeOffset allocatedAtUtc)
    {
        Id = id;
        OrganizationId = organizationId;
        LedgerTransactionId = ledgerTransactionId;
        Amount = amount;
        Currency = currency;
        BusinessReference = businessReference;
        IdempotencyKey = idempotencyKey;
        AllocatedByUserId = allocatedByUserId;
        AllocatedAtUtc = allocatedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid LedgerTransactionId { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; }

    public string BusinessReference { get; private set; }

    public string IdempotencyKey { get; private set; }

    public Guid AllocatedByUserId { get; private set; }

    public DateTimeOffset AllocatedAtUtc { get; private set; }

    public static CorporateCreditAllocation Create(
        RecordCorporateCreditRequest request,
        LedgerTransactionResult ledgerResult,
        Guid allocatedByUserId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ledgerResult);

        if (request.OrganizationId == Guid.Empty ||
            allocatedByUserId == Guid.Empty ||
            ledgerResult.TransactionId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "corporate_credit.scope.required",
                "An organization, ledger transaction, and allocating user are required.");
        }

        return new CorporateCreditAllocation(
            Guid.CreateVersion7(),
            request.OrganizationId,
            ledgerResult.TransactionId,
            request.Amount,
            request.Currency,
            request.BusinessReference,
            request.IdempotencyKey,
            allocatedByUserId,
            ledgerResult.PostedAtUtc);
    }

    public bool Matches(RecordCorporateCreditRequest request) =>
        OrganizationId == request.OrganizationId &&
        Amount == request.Amount &&
        Currency == request.Currency &&
        BusinessReference == request.BusinessReference;
}
