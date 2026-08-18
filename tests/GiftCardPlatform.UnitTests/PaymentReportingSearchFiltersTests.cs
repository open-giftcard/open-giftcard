using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Reporting.Application;
using GiftCardPlatform.Modules.Reporting.Contracts;

namespace GiftCardPlatform.UnitTests;

public sealed class PaymentReportingSearchFiltersTests
{
    [Fact]
    public void Filters_normalize_case_time_and_literal_reference_search()
    {
        var clientId = Guid.CreateVersion7();
        var from = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.FromHours(3));
        var filters = PaymentReportingSearchFilters.Normalize(new PaymentReportRequest(
            50,
            null,
            clientId,
            null,
            null,
            " store-42 ",
            " confirmed ",
            " try ",
            "receipt_100%",
            from,
            from.AddHours(1)));

        Assert.Equal(clientId, filters.PosClientId);
        Assert.Equal("STORE-42", filters.StoreReference);
        Assert.Equal("Confirmed", filters.State);
        Assert.Equal("TRY", filters.Currency);
        Assert.Equal(@"%receipt\_100\%%", filters.ReferencePattern);
        Assert.Equal(from.ToUniversalTime(), filters.OccurredFromUtc);
        Assert.Equal(from.AddHours(1).ToUniversalTime(), filters.OccurredBeforeUtc);
    }

    [Fact]
    public void Fingerprint_changes_when_any_authoritative_filter_changes()
    {
        var baseline = Request(storeReference: "STORE-1");
        var first = PaymentReportingSearchFilters.Normalize(baseline);
        var same = PaymentReportingSearchFilters.Normalize(Request(storeReference: "store-1"));
        var changed = PaymentReportingSearchFilters.Normalize(Request(storeReference: "STORE-2"));

        Assert.Equal(first.Fingerprint, same.Fingerprint);
        Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
    }

    [Theory]
    [InlineData("settled", "reporting.payments.state.invalid")]
    [InlineData("US", "reporting.payments.currency.invalid")]
    public void Invalid_enum_and_currency_filters_are_rejected(string value, string code)
    {
        var request = code.EndsWith("state.invalid", StringComparison.Ordinal)
            ? Request(state: value)
            : Request(currency: value);

        var exception = Assert.Throws<ValidationFailedException>(
            () => PaymentReportingSearchFilters.Normalize(request));
        Assert.Equal(code, exception.Code);
    }

    [Fact]
    public void Inverted_time_range_and_empty_identifiers_are_rejected()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(
            "reporting.payments.occurred_range.invalid",
            Assert.Throws<ValidationFailedException>(() =>
                PaymentReportingSearchFilters.Normalize(
                    Request(from: now, before: now))).Code);
        Assert.Equal(
            "reporting.payments.pos_client_id.invalid",
            Assert.Throws<ValidationFailedException>(() =>
                PaymentReportingSearchFilters.Normalize(
                    Request(posClientId: Guid.Empty))).Code);
    }

    private static PaymentReportRequest Request(
        Guid? posClientId = null,
        string? storeReference = null,
        string? state = null,
        string? currency = null,
        DateTimeOffset? from = null,
        DateTimeOffset? before = null) =>
        new(
            50,
            null,
            posClientId,
            null,
            null,
            storeReference,
            state,
            currency,
            null,
            from,
            before);
}
