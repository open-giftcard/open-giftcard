using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.CorporateCredits.Contracts;
using GiftCardPlatform.Modules.Ledger.Contracts;

namespace GiftCardPlatform.Modules.CorporateCredits.Domain;

internal sealed record CorporateCreditIntent(
    Guid OrganizationId,
    decimal Amount,
    string Currency,
    string BusinessReference,
    string IdempotencyKey)
{
    public const int IdempotencyKeyMinLength = 8;
    public const decimal MaximumAmount = 999_999_999_999_999.9999m;

    public static CorporateCreditIntent Create(AllocateCorporateCreditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.OrganizationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "corporate_credit.organization.required",
                "A recipient organization is required.");
        }

        if (request.Amount <= 0 || request.Amount > MaximumAmount)
        {
            throw new ValidationFailedException(
                "money.amount.invalid",
                $"Amount must be greater than zero and no greater than {MaximumAmount}.");
        }

        if (decimal.Round(request.Amount, 4, MidpointRounding.ToEven) != request.Amount)
        {
            throw new ValidationFailedException(
                "money.amount.scale",
                "Amount may have at most 4 decimal places.");
        }

        var currency = request.Currency?.Trim().ToUpperInvariant() ?? string.Empty;
        if (currency.Length != CorporateCreditAllocation.CurrencyLength ||
            currency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ValidationFailedException(
                "money.currency.invalid",
                "Currency must be a three-letter ISO-style code.");
        }

        var businessReference = NormalizeRequired(
            request.BusinessReference,
            CorporateCreditAllocation.BusinessReferenceMaxLength,
            minimumLength: 1,
            "corporate_credit.business_reference");
        var idempotencyKey = NormalizeRequired(
            request.IdempotencyKey,
            CorporateCreditAllocation.IdempotencyKeyMaxLength,
            IdempotencyKeyMinLength,
            "corporate_credit.idempotency_key");

        return new CorporateCreditIntent(
            request.OrganizationId,
            request.Amount,
            currency,
            businessReference,
            idempotencyKey);
    }

    public RecordCorporateCreditRequest ToLedgerRequest() =>
        new(OrganizationId, Amount, Currency, BusinessReference, IdempotencyKey);

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
