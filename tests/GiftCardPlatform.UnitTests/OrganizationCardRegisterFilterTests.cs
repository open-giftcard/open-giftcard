using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Reporting.Application;
using GiftCardPlatform.Modules.Reporting.Contracts;

namespace GiftCardPlatform.UnitTests;

public sealed class OrganizationCardRegisterFilterTests
{
    private static OrganizationCardRegisterRequest Request(
        string? lifecycleState = null,
        string? ownershipState = null,
        string? currency = null,
        string? reference = null) =>
        new(50, null, lifecycleState, ownershipState, currency, reference);

    [Fact]
    public void AbsentFiltersNormalizeToNull()
    {
        var filters = OrganizationCardRegisterFilters.Create(
            Request(lifecycleState: "  ", currency: null, reference: ""));

        Assert.Null(filters.LifecycleState);
        Assert.Null(filters.OwnershipState);
        Assert.Null(filters.Currency);
        Assert.Null(filters.ReferencePattern);
    }

    [Theory]
    [InlineData("active", "Active")]
    [InlineData("AWAITINGCLAIM", "AwaitingClaim")]
    [InlineData("Cancelled", "Cancelled")]
    public void LifecycleStateIsMatchedCaseInsensitivelyAndReturnedCanonical(
        string supplied,
        string expected)
    {
        var filters = OrganizationCardRegisterFilters.Create(
            Request(lifecycleState: supplied));

        Assert.Equal(expected, filters.LifecycleState);
    }

    /// <summary>
    /// A typo that silently widens a filter is worse than one that fails: the
    /// reader would believe they were looking at cancelled cards only.
    /// </summary>
    [Fact]
    public void UnknownLifecycleStateIsRefusedRatherThanIgnored()
    {
        var error = Assert.Throws<ValidationFailedException>(
            () => OrganizationCardRegisterFilters.Create(
                Request(lifecycleState: "Revoked")));

        Assert.Equal(
            "reporting.card_register.lifecycle_state.invalid",
            error.Code);
    }

    [Fact]
    public void UnknownOwnershipStateIsRefused()
    {
        var error = Assert.Throws<ValidationFailedException>(
            () => OrganizationCardRegisterFilters.Create(
                Request(ownershipState: "PlatformOwned")));

        Assert.Equal(
            "reporting.card_register.ownership_state.invalid",
            error.Code);
    }

    [Fact]
    public void CurrencyIsUppercasedAndMustBeThreeLetters()
    {
        Assert.Equal(
            "TRY",
            OrganizationCardRegisterFilters.Create(Request(currency: "try")).Currency);

        var error = Assert.Throws<ValidationFailedException>(
            () => OrganizationCardRegisterFilters.Create(Request(currency: "TRYX")));

        Assert.Equal("reporting.card_register.currency.invalid", error.Code);
    }

    /// <summary>
    /// The reference filter is a literal substring, never a caller-supplied
    /// pattern, so PostgreSQL wildcards must survive as ordinary characters.
    /// </summary>
    [Fact]
    public void ReferenceWildcardsAreEscapedIntoALiteralPattern()
    {
        var filters = OrganizationCardRegisterFilters.Create(
            Request(reference: @"GC-100%_A\B"));

        Assert.Equal(@"%GC-100\%\_A\\B%", filters.ReferencePattern);
    }

    [Fact]
    public void OverlongFilterIsRefused()
    {
        var error = Assert.Throws<ValidationFailedException>(
            () => OrganizationCardRegisterFilters.Create(
                Request(reference: new string('x', 101))));

        Assert.Equal("reporting.card_register.filter.too_long", error.Code);
    }

    [Fact]
    public void EquivalentFiltersShareAFingerprintAndDifferentOnesDoNot()
    {
        var first = OrganizationCardRegisterFilters.Create(
            Request(lifecycleState: "active", currency: "try"));
        var equivalent = OrganizationCardRegisterFilters.Create(
            Request(lifecycleState: "ACTIVE", currency: "TRY"));
        var different = OrganizationCardRegisterFilters.Create(
            Request(lifecycleState: "Suspended", currency: "TRY"));

        Assert.Equal(first.Fingerprint, equivalent.Fingerprint);
        Assert.NotEqual(first.Fingerprint, different.Fingerprint);
    }

    /// <summary>
    /// A cursor is bound to its filter set, so narrowing a filter mid-page
    /// cannot silently continue against a different result set.
    /// </summary>
    [Fact]
    public void ACursorFromOneFilterSetIsRejectedByAnother()
    {
        var original = OrganizationCardRegisterFilters.Create(
            Request(lifecycleState: "Active"));
        var narrowed = OrganizationCardRegisterFilters.Create(
            Request(lifecycleState: "Expired"));

        var cursor = ReportingCursorCodec.EncodeFiltered(
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("D"),
            original.Fingerprint);

        Assert.Throws<ValidationFailedException>(
            () => ReportingCursorCodec.DecodeFiltered(
                cursor,
                "reporting.card_register.cursor",
                narrowed.Fingerprint));
    }
}
