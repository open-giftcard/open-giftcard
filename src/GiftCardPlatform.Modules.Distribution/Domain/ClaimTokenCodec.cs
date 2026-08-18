using System.Security.Cryptography;

namespace GiftCardPlatform.Modules.Distribution.Domain;

internal static class ClaimTokenCodec
{
    public const int SecretByteCount = 32;
    public const int HashHexLength = 64;

    public static IssuedClaimToken Create(Guid invitationId)
    {
        if (invitationId == Guid.Empty)
        {
            throw new ArgumentException(
                "An invitation identifier is required.",
                nameof(invitationId));
        }

        var secret = RandomNumberGenerator.GetBytes(SecretByteCount);
        var encodedSecret = Base64UrlEncode(secret);
        return new IssuedClaimToken(
            $"{invitationId:N}.{encodedSecret}",
            Hash(secret));
    }

    public static bool TryParse(
        string? token,
        out Guid invitationId,
        out byte[] secret)
    {
        invitationId = Guid.Empty;
        secret = [];
        var candidate = token?.Trim() ?? string.Empty;
        var separator = candidate.IndexOf('.', StringComparison.Ordinal);
        if (separator != 32 ||
            !Guid.TryParseExact(candidate[..separator], "N", out invitationId))
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
        // (`app.claim_invitation_id`), and a value surviving a failed parse
        // would put an attacker-chosen identifier into that context.
        invitationId = Guid.Empty;
        secret = [];
        return false;
    }

    public static bool Matches(string expectedHash, ReadOnlySpan<byte> secret)
    {
        if (expectedHash.Length != HashHexLength)
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        var candidateHash = SHA256.HashData(secret);
        return CryptographicOperations.FixedTimeEquals(expected, candidateHash);
    }

    private static string Hash(ReadOnlySpan<byte> secret) =>
        Convert.ToHexString(SHA256.HashData(secret));

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(
            normalized.Length + ((4 - (normalized.Length % 4)) % 4),
            '=');
        return Convert.FromBase64String(normalized);
    }

    internal sealed record IssuedClaimToken(string RawToken, string SecretHash);
}
