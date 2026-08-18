using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.CorporateCredits.Contracts;
using GiftCardPlatform.Modules.Ledger.Contracts;

namespace GiftCardPlatform.Modules.CorporateCredits.Domain;

internal sealed record CorporateCreditReversalIntent(
    Guid AllocationId,
    string Reason,
    string IdempotencyKey)
{
    public const int ReasonMaxLength = 240;

    public static CorporateCreditReversalIntent Create(ReverseCorporateCreditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.AllocationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "corporate_credit.reversal.allocation.required",
                "An original allocation is required.");
        }

        var reason = NormalizeRequired(
            request.Reason,
            ReasonMaxLength,
            minimumLength: 3,
            "corporate_credit.reversal.reason");
        var idempotencyKey = NormalizeRequired(
            request.IdempotencyKey,
            CorporateCreditAllocation.IdempotencyKeyMaxLength,
            CorporateCreditIntent.IdempotencyKeyMinLength,
            "corporate_credit.reversal.idempotency_key");

        return new CorporateCreditReversalIntent(
            request.AllocationId,
            reason,
            idempotencyKey);
    }

    public RecordCorporateCreditReversalRequest ToLedgerRequest(
        CorporateCreditAllocation allocation) =>
        new(
            allocation.OrganizationId,
            allocation.LedgerTransactionId,
            allocation.Amount,
            allocation.Currency,
            $"REVERSAL-{allocation.Id:N}",
            IdempotencyKey);

    private static string NormalizeRequired(
        string? value,
        int maximumLength,
        int minimumLength,
        string errorPrefix)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length < minimumLength || normalized.Length > maximumLength)
        {
            throw new ValidationFailedException(
                $"{errorPrefix}.invalid_length",
                $"Value must be between {minimumLength} and {maximumLength} characters.");
        }

        return normalized;
    }
}
