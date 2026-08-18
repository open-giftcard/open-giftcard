using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GiftCardPlatform.Modules.Distribution.Domain;

internal sealed record IssuedEpinCredential(
    string ClaimToken,
    string ClaimSecretHash,
    string Pin,
    string PinHash);

/// <summary>
/// Derives stable e-pin delivery material from a server-held key and an opaque
/// invitation id. Stability makes a lost HTTP response safely retryable while
/// the database still stores only one-way verifiers.
/// </summary>
internal static class EpinCredentialCodec
{
    public const int PinDigits = 6;
    public const int PinHashHexLength = 64;
    public const int DeliveryKeyByteCount = 32;

    public static IssuedEpinCredential Create(Guid invitationId, ReadOnlySpan<byte> deliveryKey)
    {
        Validate(invitationId, deliveryKey);
        var claimSecret = Derive(deliveryKey, invitationId, "claim-secret");
        var pinSeed = Derive(deliveryKey, invitationId, "pin-value");
        var pinNumber = BinaryPrimitives.ReadUInt64BigEndian(pinSeed) % 1_000_000UL;
        var pin = pinNumber.ToString("D6", CultureInfo.InvariantCulture);
        var pinHash = ComputePinHash(invitationId, pin, deliveryKey);
        var encodedSecret = Convert.ToBase64String(claimSecret)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return new IssuedEpinCredential(
            $"{invitationId:N}.{encodedSecret}",
            Convert.ToHexString(SHA256.HashData(claimSecret)),
            pin,
            pinHash);
    }

    public static bool MatchesPin(
        Guid invitationId,
        string? suppliedPin,
        string storedPinHash,
        ReadOnlySpan<byte> deliveryKey)
    {
        if (invitationId == Guid.Empty ||
            suppliedPin is null ||
            suppliedPin.Length != PinDigits ||
            suppliedPin.AsSpan().ContainsAnyExceptInRange('0', '9') ||
            storedPinHash.Length != PinHashHexLength ||
            deliveryKey.Length != DeliveryKeyByteCount)
        {
            return false;
        }

        try
        {
            var expected = Convert.FromHexString(storedPinHash);
            var actual = Convert.FromHexString(
                ComputePinHash(invitationId, suppliedPin, deliveryKey));
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ComputePinHash(
        Guid invitationId,
        string pin,
        ReadOnlySpan<byte> deliveryKey)
    {
        var payload = Encoding.UTF8.GetBytes($"epin-pin-v1|{invitationId:N}|{pin}");
        return Convert.ToHexString(HMACSHA256.HashData(deliveryKey, payload));
    }

    private static byte[] Derive(
        ReadOnlySpan<byte> deliveryKey,
        Guid invitationId,
        string purpose)
    {
        var payload = Encoding.UTF8.GetBytes($"epin-v1|{purpose}|{invitationId:N}");
        return HMACSHA256.HashData(deliveryKey, payload);
    }

    private static void Validate(Guid invitationId, ReadOnlySpan<byte> deliveryKey)
    {
        if (invitationId == Guid.Empty)
        {
            throw new ArgumentException("An invitation identifier is required.", nameof(invitationId));
        }

        if (deliveryKey.Length != DeliveryKeyByteCount)
        {
            throw new ArgumentException(
                "The e-pin delivery key must contain exactly 32 bytes.",
                nameof(deliveryKey));
        }
    }
}
