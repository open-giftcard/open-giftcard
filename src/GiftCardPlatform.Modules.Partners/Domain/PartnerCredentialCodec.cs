using System.Security.Cryptography;
using System.Text;

namespace GiftCardPlatform.Modules.Partners.Domain;

/// <summary>
/// Partner API client secrets are 256 CSPRNG bits, so a plain SHA-256 digest is
/// an appropriate store: there is no low-entropy guess space to slow down. This
/// is the same reasoning already applied to POS client secrets, refresh tokens,
/// and invitation secrets. A password KDF is reserved for human-chosen values
/// such as the six-digit e-pin PIN.
/// </summary>
internal static class PartnerCredentialCodec
{
    public const int SecretByteCount = 32;
    public const int HashHexLength = 64;

    public static IssuedSecret Create()
    {
        var secret = ToBase64Url(RandomNumberGenerator.GetBytes(SecretByteCount));
        return new IssuedSecret(secret, Hash(secret));
    }

    public static string Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    /// <summary>
    /// Constant-time comparison. A malformed stored hash never matches rather
    /// than throwing, so an unusable row is refused exactly like a wrong secret
    /// and cannot be distinguished by timing or error shape.
    /// </summary>
    public static bool Matches(string? expectedHash, string? presentedSecret)
    {
        if (expectedHash is null || expectedHash.Length != HashHexLength ||
            string.IsNullOrWhiteSpace(presentedSecret))
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHash),
                SHA256.HashData(Encoding.UTF8.GetBytes(presentedSecret)));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    internal sealed record IssuedSecret(string Secret, string Hash);
}
