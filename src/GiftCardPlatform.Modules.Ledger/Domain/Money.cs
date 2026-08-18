using GiftCardPlatform.BuildingBlocks.Errors;

namespace GiftCardPlatform.Modules.Ledger.Domain;

internal readonly record struct Money
{
    public const int CurrencyLength = 3;
    public const int Scale = 4;
    public const decimal MaximumAmount = 999_999_999_999_999.9999m;

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public string Currency { get; }

    public static Money Create(decimal amount, string? currency)
    {
        if (amount <= 0 || amount > MaximumAmount)
        {
            throw new ValidationFailedException(
                "money.amount.invalid",
                $"Amount must be greater than zero and no greater than {MaximumAmount}.");
        }

        if (decimal.Round(amount, Scale, MidpointRounding.ToEven) != amount)
        {
            throw new ValidationFailedException(
                "money.amount.scale",
                $"Amount may have at most {Scale} decimal places.");
        }

        var normalizedCurrency = currency?.Trim().ToUpperInvariant() ?? string.Empty;

        if (normalizedCurrency.Length != CurrencyLength ||
            normalizedCurrency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ValidationFailedException(
                "money.currency.invalid",
                "Currency must be a three-letter ISO-style code.");
        }

        return new Money(amount, normalizedCurrency);
    }
}
