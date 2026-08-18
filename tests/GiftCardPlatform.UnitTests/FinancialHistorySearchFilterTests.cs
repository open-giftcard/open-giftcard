using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Reporting.Application;
using GiftCardPlatform.Modules.Reporting.Contracts;

namespace GiftCardPlatform.UnitTests;

public sealed class FinancialHistorySearchFilterTests
{
    [Fact]
    public void Filters_are_normalized_and_literal_reference_characters_are_escaped()
    {
        var request = Request(
            category: " GiftCard ",
            operation: " ISSUED ",
            currency: "try",
            reference: @" Prize%_2026\Q3 ",
            occurredFromUtc: new DateTimeOffset(
                2026,
                7,
                29,
                10,
                0,
                0,
                TimeSpan.FromHours(3)));

        var filters = FinancialHistorySearchFilters.Normalize(request);

        Assert.Equal("giftcard", filters.Category);
        Assert.Equal("issued", filters.Operation);
        Assert.Equal("TRY", filters.Currency);
        Assert.Equal(@"%prize\%\_2026\\q3%", filters.ReferencePattern);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 29, 7, 0, 0, TimeSpan.Zero),
            filters.OccurredFromUtc);
        Assert.False(filters.IsEmpty);
    }

    [Fact]
    public void Equivalent_case_and_whitespace_produce_the_same_fingerprint()
    {
        var first = FinancialHistorySearchFilters.Normalize(
            Request(
                category: "GiftCard",
                currency: "try",
                reference: "Prize-2026"));
        var second = FinancialHistorySearchFilters.Normalize(
            Request(
                category: " giftcard ",
                currency: "TRY",
                reference: " prize-2026 "));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Invalid_currency_and_time_range_fail_with_stable_codes()
    {
        var currency = Assert.Throws<ValidationFailedException>(
            () => FinancialHistorySearchFilters.Normalize(
                Request(currency: "TR")));
        Assert.Equal("reporting.history.currency.invalid", currency.Code);

        var from = new DateTimeOffset(2026, 7, 29, 8, 0, 0, TimeSpan.Zero);
        var range = Assert.Throws<ValidationFailedException>(
            () => FinancialHistorySearchFilters.Normalize(
                Request(
                    occurredFromUtc: from,
                    occurredBeforeUtc: from)));
        Assert.Equal("reporting.history.occurred_range.invalid", range.Code);
    }

    private static OrganizationFinancialHistoryRequest Request(
        string? category = null,
        string? operation = null,
        string? currency = null,
        string? reference = null,
        DateTimeOffset? occurredFromUtc = null,
        DateTimeOffset? occurredBeforeUtc = null) =>
        new(
            ReportingPageRequest.DefaultLimit,
            null,
            category,
            operation,
            currency,
            reference,
            occurredFromUtc,
            occurredBeforeUtc);
}
