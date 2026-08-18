using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.CorporateCredits.Contracts;
using GiftCardPlatform.Modules.CorporateCredits.Domain;

namespace GiftCardPlatform.UnitTests;

public sealed class CorporateCreditReversalIntentTests
{
    [Fact]
    public void Intent_normalizes_reason_and_idempotency_key()
    {
        var allocationId = Guid.CreateVersion7();

        var intent = CorporateCreditReversalIntent.Create(
            new ReverseCorporateCreditRequest(
                allocationId,
                " Commercial agreement cancelled ",
                " reversal-contract-42 "));

        Assert.Equal(allocationId, intent.AllocationId);
        Assert.Equal("Commercial agreement cancelled", intent.Reason);
        Assert.Equal("reversal-contract-42", intent.IdempotencyKey);
    }

    [Fact]
    public void Reversal_requires_an_allocation_and_meaningful_reason()
    {
        var missingAllocation = Assert.Throws<ValidationFailedException>(() =>
            CorporateCreditReversalIntent.Create(
                new ReverseCorporateCreditRequest(
                    Guid.Empty,
                    "Cancelled",
                    "reversal-contract-42")));
        var shortReason = Assert.Throws<ValidationFailedException>(() =>
            CorporateCreditReversalIntent.Create(
                new ReverseCorporateCreditRequest(
                    Guid.CreateVersion7(),
                    "x",
                    "reversal-contract-42")));

        Assert.Equal("corporate_credit.reversal.allocation.required", missingAllocation.Code);
        Assert.Equal("corporate_credit.reversal.reason.invalid_length", shortReason.Code);
    }
}
