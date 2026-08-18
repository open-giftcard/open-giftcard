using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.CorporateCredits.Contracts;
using GiftCardPlatform.Modules.CorporateCredits.Domain;

namespace GiftCardPlatform.UnitTests;

public sealed class CorporateCreditIntentTests
{
    [Fact]
    public void Intent_normalizes_boundary_values_before_ledger_submission()
    {
        var organizationId = Guid.CreateVersion7();

        var intent = CorporateCreditIntent.Create(
            new AllocateCorporateCreditRequest(
                organizationId,
                50.25m,
                " try ",
                " CONTRACT-1 ",
                " allocation-contract-1 "));

        Assert.Equal("TRY", intent.Currency);
        Assert.Equal("CONTRACT-1", intent.BusinessReference);
        Assert.Equal("allocation-contract-1", intent.IdempotencyKey);
        Assert.Equal(intent.ToLedgerRequest().OrganizationId, organizationId);
    }

    [Fact]
    public void Empty_organization_is_rejected()
    {
        var exception = Assert.Throws<ValidationFailedException>(() =>
            CorporateCreditIntent.Create(
                new AllocateCorporateCreditRequest(
                    Guid.Empty,
                    10m,
                    "TRY",
                    "CONTRACT-1",
                    "allocation-contract-1")));

        Assert.Equal("corporate_credit.organization.required", exception.Code);
    }

    [Fact]
    public void Empty_business_reference_is_rejected()
    {
        var exception = Assert.Throws<ValidationFailedException>(() =>
            CorporateCreditIntent.Create(
                new AllocateCorporateCreditRequest(
                    Guid.CreateVersion7(),
                    10m,
                    "TRY",
                    " ",
                    "allocation-contract-1")));

        Assert.Equal("corporate_credit.business_reference.invalid_length", exception.Code);
    }
}
