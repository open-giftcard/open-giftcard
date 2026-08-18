using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Organizations.Contracts;

namespace GiftCardPlatform.Modules.Organizations.Application;

internal static class PageRequestValidator
{
    /// <summary>
    /// Validates a requested page. An out-of-range limit is rejected rather than
    /// silently clamped, so a caller that believes it is fetching everything
    /// finds out that it is not.
    /// </summary>
    public static PageRequest Validate(PageRequest? page)
    {
        if (page is null)
        {
            return PageRequest.Default;
        }

        if (page.Limit is < 1 or > PageRequest.MaxLimit)
        {
            throw new ValidationFailedException(
                "page.limit.out_of_range",
                $"Limit must be between 1 and {PageRequest.MaxLimit}.");
        }

        if (page.Offset < 0)
        {
            throw new ValidationFailedException("page.offset.negative", "Offset must not be negative.");
        }

        return page;
    }
}
