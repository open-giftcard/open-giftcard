using System.Security.Cryptography;

namespace GiftCardPlatform.Modules.Payments.Domain;

/// <summary>
/// ADR-017: the payment credential is 256 opaque CSPRNG bits, not a JWT. It
/// encodes no card, owner, amount, or balance — the identifier prefix selects a
/// row, and the secret proves possession. Only the secret's SHA-256 hash is
/// persisted, so a database read cannot reconstruct a usable credential.
/// </summary>
internal static class PaymentTokenCodec
{
    public const int SecretByteCount = 32;
    public const int HashHexLength = 64;

    public static IssuedToken Create(Guid tokenId)
    {
        if (tokenId == Guid.Empty)
        {
            throw new ArgumentException("A token identifier is required.", nameof(tokenId));
        }

        var secret = RandomNumberGenerator.GetBytes(SecretByteCount);
        return new IssuedToken(
            $"{tokenId:N}.{Base64UrlEncode(secret)}",
            Convert.ToHexString(SHA256.HashData(secret)));
    }

    public static bool TryParse(string? token, out Guid tokenId, out byte[] secret)
    {
        tokenId = Guid.Empty;
        secret = [];
        var candidate = token?.Trim() ?? string.Empty;
        var separator = candidate.IndexOf('.', StringComparison.Ordinal);
        if (separator != 32 || !Guid.TryParseExact(candidate[..separator], "N", out tokenId))
        {
            return false;
        }

        try
        {
            secret = Base64UrlDecode(candidate[(separator + 1)..]);
            if (secret.Length == SecretByteCount)
            {
                return true;
            }
        }
        catch (FormatException)
        {
            // Fall through to the shared failure path below.
        }

        // Every failure path clears both outputs. A well-formed identifier with
        // a malformed secret must not leave the identifier populated: callers
        // use it to establish the transaction-local RLS candidate, and a value
        // surviving a failed parse would put an attacker-chosen identifier into
        // that context.
        tokenId = Guid.Empty;
        secret = [];
        return false;
    }

    public static bool Matches(string expectedHash, ReadOnlySpan<byte> secret)
    {
        if (expectedHash.Length != HashHexLength)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHash),
                SHA256.HashData(secret));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(
            normalized.Length + ((4 - (normalized.Length % 4)) % 4),
            '=');
        return Convert.FromBase64String(normalized);
    }

    internal sealed record IssuedToken(string RawToken, string SecretHash);
}
