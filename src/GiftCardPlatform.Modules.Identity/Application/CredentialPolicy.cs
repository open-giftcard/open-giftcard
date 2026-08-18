using System.Net.Mail;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;

namespace GiftCardPlatform.Modules.Identity.Application;

internal static class CredentialPolicy
{
    public const int MinimumPasswordLength = 12;
    public const int MaximumPasswordLength = 128;

    private static readonly HashSet<string> BlockedPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "123456789012",
        "administrator",
        "letmeinletmein",
        "password1234",
        "password123!",
        "qwertyqwerty",
        "welcome12345",
        "giftcardplatform",
        "giftcardplatform!",
        "giftcard1234",
    };

    public static (string Email, string NormalizedEmail) NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Length > 320 ||
            !MailAddress.TryCreate(trimmed, out var parsed) ||
            !string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationFailedException("user.email.invalid", "A valid email address is required.");
        }

        return (trimmed, trimmed.ToUpperInvariant());
    }

    public static (string PhoneNumber, string NormalizedPhoneNumber) NormalizePhone(
        string? phoneNumber)
    {
        var candidate = phoneNumber?.Trim() ?? string.Empty;
        var normalized = new StringBuilder(candidate.Length);
        foreach (var character in candidate)
        {
            if (character is ' ' or '-' or '(' or ')')
            {
                continue;
            }

            normalized.Append(character);
        }

        var result = normalized.ToString();
        if (result.Length is < 9 or > 16 ||
            result[0] != '+' ||
            result[1] is < '1' or > '9' ||
            result.AsSpan(2).ContainsAnyExceptInRange('0', '9'))
        {
            throw new ValidationFailedException(
                "user.phone.invalid",
                "Phone numbers must use E.164 form, for example +905551234567.");
        }

        return (result, result);
    }

    public static NormalizedLoginIdentifier NormalizeIdentifier(string? identifier)
    {
        var candidate = identifier?.Trim() ?? string.Empty;
        if (candidate.StartsWith('+'))
        {
            var (phone, normalizedPhone) = NormalizePhone(candidate);
            return new NormalizedLoginIdentifier(
                Email: null,
                NormalizedEmail: null,
                PhoneNumber: phone,
                NormalizedPhoneNumber: normalizedPhone);
        }

        var (email, normalizedEmail) = NormalizeEmail(candidate);
        return new NormalizedLoginIdentifier(
            email,
            normalizedEmail,
            PhoneNumber: null,
            NormalizedPhoneNumber: null);
    }

    public static string ValidatePassword(string? password)
    {
        if (password is null)
        {
            throw new ValidationFailedException("user.password.required", "A password is required.");
        }

        var characterCount = password.EnumerateRunes().Count();
        if (characterCount < MinimumPasswordLength || characterCount > MaximumPasswordLength)
        {
            throw new ValidationFailedException(
                "user.password.invalid_length",
                $"The password must be between {MinimumPasswordLength} and {MaximumPasswordLength} characters.");
        }

        if (BlockedPasswords.Contains(password) || HasSingleRepeatedCharacter(password))
        {
            throw new ValidationFailedException(
                "user.password.common",
                "Choose a less common password or passphrase.");
        }

        return password;
    }

    private static bool HasSingleRepeatedCharacter(string password)
    {
        using var enumerator = password.EnumerateRunes().GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return false;
        }

        var first = enumerator.Current;
        while (enumerator.MoveNext())
        {
            if (enumerator.Current != first)
            {
                return false;
            }
        }

        return true;
    }

    internal sealed record NormalizedLoginIdentifier(
        string? Email,
        string? NormalizedEmail,
        string? PhoneNumber,
        string? NormalizedPhoneNumber);
}
