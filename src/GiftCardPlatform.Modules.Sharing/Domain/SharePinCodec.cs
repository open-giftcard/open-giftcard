using System.Globalization;
using System.Security.Cryptography;

namespace GiftCardPlatform.Modules.Sharing.Domain;

internal static class SharePinCodec
{
    public const int PinLength = 6;
    public const int SaltByteCount = 16;
    public const int HashByteCount = 32;
    public const int Iterations = 210_000;
    public const int PersistedLength = 7 + (SaltByteCount * 2) + 1 + (HashByteCount * 2);

    public static IssuedSharePin Create()
    {
        var raw = RandomNumberGenerator.GetInt32(0, 1_000_000)
            .ToString("D6", CultureInfo.InvariantCulture);
        var salt = RandomNumberGenerator.GetBytes(SaltByteCount);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            raw,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashByteCount);
        return new IssuedSharePin(
            raw,
            $"{Iterations.ToString(CultureInfo.InvariantCulture)}.{Convert.ToHexString(salt)}.{Convert.ToHexString(hash)}");
    }

    public static bool Matches(string? persisted, string? supplied)
    {
        var pin = supplied?.Trim() ?? string.Empty;
        if (pin.Length != PinLength || pin.Any(character => character is < '0' or > '9'))
        {
            return false;
        }

        var parts = persisted?.Split('.', StringSplitOptions.None) ?? [];
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var iterations) ||
            iterations != Iterations)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromHexString(parts[1]);
            var expected = Convert.FromHexString(parts[2]);
            if (salt.Length != SaltByteCount || expected.Length != HashByteCount)
            {
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(
                pin,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                HashByteCount);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal sealed record IssuedSharePin(string RawPin, string PersistedHash);
}
