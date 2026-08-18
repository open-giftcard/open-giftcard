using GiftCardPlatform.Modules.Identity.Application;
using GiftCardPlatform.Modules.Identity.Contracts;

namespace GiftCardPlatform.UnitTests;

public sealed class RecipientContactServiceTests
{
    private readonly RecipientContactService service = new();

    [Fact]
    public void Email_is_normalized_and_masked_by_identity_boundary()
    {
        var result = service.NormalizeAndMask(
            IdentityContactType.Email,
            "  Recipient.Example@Example.com  ");

        Assert.Equal("recipient.example@example.com", result.Contact);
        Assert.Equal("r***@example.com", result.MaskedContact);
    }

    [Fact]
    public void Phone_is_normalized_to_e164_and_masked()
    {
        var result = service.NormalizeAndMask(
            IdentityContactType.Phone,
            "+90 (555) 123-4567");

        Assert.Equal("+905551234567", result.Contact);
        Assert.Equal("+90***4567", result.MaskedContact);
    }
}
