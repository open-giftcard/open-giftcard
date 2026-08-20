using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Payments.Domain;

namespace GiftCardPlatform.UnitTests;

public sealed class PaymentRefundTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Refund_captures_the_confirmed_sale_and_normalizes_request_metadata()
    {
        var provision = ConfirmedProvision();
        var ledgerId = Guid.CreateVersion7();
        var refund = PaymentRefund.Create(
            Guid.CreateVersion7(), provision, ledgerId, Guid.CreateVersion7(),
            " STORE-2 ", " RETURN-42 ", " retry-key ", " Customer return ",
            12.5m, Now.AddMinutes(1));

        Assert.Equal(provision.Id, refund.PaymentProvisionId);
        Assert.Equal(provision.RedemptionLedgerTransactionId, refund.RedemptionLedgerTransactionId);
        Assert.Equal(ledgerId, refund.RefundLedgerTransactionId);
        Assert.Equal("STORE-2", refund.StoreReference);
        Assert.Equal("RETURN-42", refund.PosTransactionReference);
        Assert.Equal("retry-key", refund.IdempotencyKey);
        Assert.Equal("Customer return", refund.Reason);
        Assert.True(refund.Matches(12.5m, " RETURN-42 ", " Customer return "));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.00001")]
    public void Refund_requires_positive_four_decimal_money(string rawAmount)
    {
        var amount = decimal.Parse(rawAmount, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<ValidationFailedException>(() => PaymentRefund.Create(
            Guid.CreateVersion7(), ConfirmedProvision(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), "STORE-2", null, "retry-key", "Return",
            amount, Now));
    }

    private static PaymentProvision ConfirmedProvision()
    {
        var provision = PaymentProvision.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "GC-REFUND",
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), "STORE-1", "SALE-42", 50m, 50m, "TRY", Now, 120);
        provision.Confirm(40m, Guid.CreateVersion7(), Now.AddSeconds(30));
        return provision;
    }
}
