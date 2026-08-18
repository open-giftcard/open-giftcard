using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Identity.Application;

namespace GiftCardPlatform.UnitTests;

public sealed class CredentialPolicyTests
{
    [Fact]
    public void Email_is_trimmed_and_normalized_for_case_insensitive_login()
    {
        var (email, normalized) = CredentialPolicy.NormalizeEmail("  Person.Example@example.com  ");

        Assert.Equal("Person.Example@example.com", email);
        Assert.Equal("PERSON.EXAMPLE@EXAMPLE.COM", normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("person@example.com extra")]
    public void Invalid_email_is_rejected(string? email)
    {
        var exception = Assert.Throws<ValidationFailedException>(
            () => CredentialPolicy.NormalizeEmail(email));

        Assert.Equal("user.email.invalid", exception.Code);
    }

    [Theory]
    [InlineData("long pass phrase with spaces")]
    [InlineData("no-uppercase-or-symbols")]
    [InlineData("UPPERCASE ONLY TEXT")]
    [InlineData("пароль-достаточной-длины")]
    public void Password_does_not_require_character_categories(string password) =>
        Assert.Equal(password, CredentialPolicy.ValidatePassword(password));

    [Fact]
    public void Password_length_counts_Unicode_characters_not_utf16_code_units()
    {
        var password = string.Concat(Enumerable.Repeat("🙂🙃", 6));

        Assert.Equal(password, CredentialPolicy.ValidatePassword(password));
    }

    [Fact]
    public void Password_shorter_than_twelve_characters_is_rejected()
    {
        var exception = Assert.Throws<ValidationFailedException>(
            () => CredentialPolicy.ValidatePassword("short pass"));

        Assert.Equal("user.password.invalid_length", exception.Code);
    }

    [Fact]
    public void Password_longer_than_128_characters_is_rejected()
    {
        var exception = Assert.Throws<ValidationFailedException>(
            () => CredentialPolicy.ValidatePassword(new string('x', 129)));

        Assert.Equal("user.password.invalid_length", exception.Code);
    }

    [Theory]
    [InlineData("password1234")]
    [InlineData("giftcardplatform!")]
    [InlineData("aaaaaaaaaaaa")]
    public void Common_or_trivial_password_is_rejected(string password)
    {
        var exception = Assert.Throws<ValidationFailedException>(
            () => CredentialPolicy.ValidatePassword(password));

        Assert.Equal("user.password.common", exception.Code);
    }
}
