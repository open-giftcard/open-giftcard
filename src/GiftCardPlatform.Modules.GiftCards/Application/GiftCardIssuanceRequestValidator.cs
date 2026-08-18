using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.GiftCards.Domain;

namespace GiftCardPlatform.Modules.GiftCards.Application;

internal sealed class GiftCardIssuanceRequestValidator(TimeProvider timeProvider) :
    IGiftCardIssuanceRequestValidator
{
    public IssueGiftCardRequest ValidateAndNormalize(IssueGiftCardRequest request)
    {
        var intent = GiftCardIssuanceIntent.Create(request);
        var validatedAtUtc = timeProvider.GetUtcNow();
        intent.EnsureCanIssueAt(validatedAtUtc);

        return new IssueGiftCardRequest(
            intent.Amount,
            intent.Currency,
            intent.RequestedValidFromUtc ?? validatedAtUtc,
            intent.ExpiresAtUtc,
            intent.IsTransferable,
            intent.IsDivisible,
            intent.BusinessReference,
            intent.IdempotencyKey);
    }
}
