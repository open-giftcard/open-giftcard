using System.Security.Cryptography;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.GiftCards.Contracts;

namespace GiftCardPlatform.Modules.GiftCards.Domain;

internal sealed record GiftCardIssuanceIntent(
    decimal Amount,
    string Currency,
    DateTimeOffset? RequestedValidFromUtc,
    DateTimeOffset ExpiresAtUtc,
    bool IsTransferable,
    bool IsDivisible,
    string BusinessReference,
    string IdempotencyKey)
{
    public const int IdempotencyKeyMinLength = 8;

    public static GiftCardIssuanceIntent Create(IssueGiftCardRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateAmount(request.Amount);
        var currency = NormalizeCurrency(request.Currency);
        var requestedValidFrom = request.ValidFromUtc?.ToUniversalTime();
        var expiresAt = request.ExpiresAtUtc?.ToUniversalTime()
            ?? throw new ValidationFailedException(
                "gift_card.expires_at.required",
                "An expiration timestamp is required.");

        if (expiresAt == default)
        {
            throw new ValidationFailedException(
                "gift_card.expires_at.required",
                "An expiration timestamp is required.");
        }

        if (requestedValidFrom is not null && expiresAt <= requestedValidFrom.Value)
        {
            throw new ValidationFailedException(
                "gift_card.validity.invalid",
                "Expiration must be later than the requested validity start.");
        }

        return new GiftCardIssuanceIntent(
            request.Amount,
            currency,
            requestedValidFrom,
            expiresAt,
            request.IsTransferable ?? false,
            request.IsDivisible ?? false,
            NormalizeRequired(
                request.BusinessReference,
                GiftCard.BusinessReferenceMaxLength,
                minimumLength: 1,
                "gift_card.business_reference"),
            NormalizeRequired(
                request.IdempotencyKey,
                GiftCard.IdempotencyKeyMaxLength,
                IdempotencyKeyMinLength,
                "gift_card.idempotency_key"));
    }

    public void EnsureCanIssueAt(DateTimeOffset now)
    {
        var effectiveValidFrom = RequestedValidFromUtc ?? now.ToUniversalTime();
        if (ExpiresAtUtc <= effectiveValidFrom)
        {
            throw new ValidationFailedException(
                "gift_card.validity.invalid",
                "Expiration must be later than the effective validity start.");
        }
    }

    public string ToLedgerIdempotencyKey(Guid fundingOrganizationId)
    {
        var canonical = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{fundingOrganizationId:D}|{IdempotencyKey}");
        return "giftcard:" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void ValidateAmount(decimal amount)
    {
        if (amount <= 0 || amount > GiftCard.MaximumAmount)
        {
            throw new ValidationFailedException(
                "money.amount.invalid",
                $"Amount must be greater than zero and no greater than {GiftCard.MaximumAmount}.");
        }

        if (decimal.Round(amount, GiftCard.AmountScale, MidpointRounding.ToEven) != amount)
        {
            throw new ValidationFailedException(
                "money.amount.scale",
                $"Amount may have at most {GiftCard.AmountScale} decimal places.");
        }
    }

    private static string NormalizeCurrency(string? value)
    {
        var currency = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (currency.Length != GiftCard.CurrencyLength ||
            currency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ValidationFailedException(
                "money.currency.invalid",
                "Currency must be a three-letter ISO-style code.");
        }

        return currency;
    }

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
