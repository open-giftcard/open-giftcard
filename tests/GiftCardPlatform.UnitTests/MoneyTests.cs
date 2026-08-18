using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Ledger.Domain;

namespace GiftCardPlatform.UnitTests;

public sealed class MoneyTests
{
    [Fact]
    public void Valid_money_normalizes_the_currency()
    {
        var money = Money.Create(1250.5000m, " try ");

        Assert.Equal(1250.5000m, money.Amount);
        Assert.Equal("TRY", money.Currency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Amount_must_be_positive(decimal amount)
    {
        var exception = Assert.Throws<ValidationFailedException>(() => Money.Create(amount, "TRY"));

        Assert.Equal("money.amount.invalid", exception.Code);
    }

    [Fact]
    public void Amount_cannot_exceed_four_decimal_places()
    {
        var exception = Assert.Throws<ValidationFailedException>(() => Money.Create(1.00001m, "TRY"));

        Assert.Equal("money.amount.scale", exception.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("TR")]
    [InlineData("TR1")]
    [InlineData("EURO")]
    public void Currency_must_be_three_ascii_letters(string currency)
    {
        var exception = Assert.Throws<ValidationFailedException>(() => Money.Create(1m, currency));

        Assert.Equal("money.currency.invalid", exception.Code);
    }
}
