using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Identity.Contracts;

namespace GiftCardPlatform.Modules.Identity.Application;

internal sealed class RecipientContactService : IRecipientContactService
{
    public RecipientContactResult NormalizeAndMask(
        IdentityContactType contactType,
        string? contact) =>
        contactType switch
        {
            IdentityContactType.Email => FromEmail(contact),
            IdentityContactType.Phone => FromPhone(contact),
            _ => throw new ValidationFailedException(
                "recipient_identity.contact_type.invalid",
                "Recipient contact type must be Email or Phone."),
        };

    private static RecipientContactResult FromEmail(string? value)
    {
        var (email, _) = CredentialPolicy.NormalizeEmail(value);
        var canonical = email.ToLowerInvariant();
        var at = canonical.IndexOf('@', StringComparison.Ordinal);
        return new RecipientContactResult(
            IdentityContactType.Email,
            canonical,
            $"{canonical[..1]}***{canonical[at..]}");
    }

    private static RecipientContactResult FromPhone(string? value)
    {
        var (phone, _) = CredentialPolicy.NormalizePhone(value);
        return new RecipientContactResult(
            IdentityContactType.Phone,
            phone,
            $"{phone[..Math.Min(3, phone.Length - 4)]}***{phone[^4..]}");
    }
}
