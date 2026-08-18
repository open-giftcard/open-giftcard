using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GiftCardPlatform.Modules.Payments.Domain;

/// <summary>
/// ADR-050 human-enterable alias for one payment token. The raw 12 digits are
/// returned once; only SHA-256 of the canonical digits is persisted.
/// </summary>
internal static class NumericPaymentCodeCodec
{
    public const int DigitCount = 12;
    public const int HashHexLength = 64;

    public static IssuedNumericCode Create()
    {
        var rawCode = string.Create(
            CultureInfo.InvariantCulture,
            $"{RandomNumberGenerator.GetInt32(10_000):D4}" +
            $"{RandomNumberGenerator.GetInt32(10_000):D4}" +
            $"{RandomNumberGenerator.GetInt32(10_000):D4}");
        return new IssuedNumericCode(rawCode, Hash(rawCode));
    }

    public static bool TryNormalize(string? value, out string canonicalCode)
    {
        canonicalCode = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        Span<char> digits = stackalloc char[DigitCount];
        var count = 0;
        foreach (var character in value.Trim())
        {
            if (character is ' ' or '-')
            {
                continue;
            }

            if (character is < '0' or > '9' || count == DigitCount)
            {
                return false;
            }

            digits[count++] = character;
        }

        if (count != DigitCount)
        {
            return false;
        }

        canonicalCode = new string(digits);
        return true;
    }

    public static string Hash(string canonicalCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalCode);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.ASCII.GetBytes(canonicalCode)));
    }

    public static bool Matches(string? expectedHash, string canonicalCode)
    {
        if (expectedHash is null || expectedHash.Length != HashHexLength)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHash),
                Convert.FromHexString(Hash(canonicalCode)));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal sealed record IssuedNumericCode(string RawCode, string CodeHash);
}
