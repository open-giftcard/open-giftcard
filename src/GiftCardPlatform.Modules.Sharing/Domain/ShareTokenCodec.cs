using System.Security.Cryptography;

namespace GiftCardPlatform.Modules.Sharing.Domain;

internal static class ShareTokenCodec
{
    public const int SecretByteCount = 32;
    public const int HashHexLength = 64;

    public static IssuedShareToken Create(Guid shareId)
    {
        if (shareId == Guid.Empty)
        {
            throw new ArgumentException("A share identifier is required.", nameof(shareId));
        }

        var secret = RandomNumberGenerator.GetBytes(SecretByteCount);
        return new IssuedShareToken(
            $"{shareId:N}.{Base64UrlEncode(secret)}",
            Convert.ToHexString(SHA256.HashData(secret)));
    }

    public static bool TryParse(string? token, out Guid shareId, out byte[] secret)
    {
        shareId = Guid.Empty;
        secret = [];
        var candidate = token?.Trim() ?? string.Empty;
        var separator = candidate.IndexOf('.', StringComparison.Ordinal);
        if (separator != 32 || !Guid.TryParseExact(candidate[..separator], "N", out shareId))
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
        // use it to establish the transaction-local RLS candidate
        // (`app.share_id`), and a value surviving a failed parse would put an
        // attacker-chosen identifier into that context.
        shareId = Guid.Empty;
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

    internal sealed record IssuedShareToken(string RawToken, string SecretHash);
}
