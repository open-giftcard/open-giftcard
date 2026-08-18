using System.Globalization;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;

namespace GiftCardPlatform.Modules.Reporting.Application;

internal sealed record ReportingCursor(
    DateTimeOffset OccurredAtUtc,
    string StableKey,
    string? FilterFingerprint = null);

internal static class ReportingCursorCodec
{
    private const string UnfilteredVersion = "v1";
    private const string FilteredVersion = "v2";
    private const int MaximumEncodedLength = 512;
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Encode(DateTimeOffset occurredAtUtc, string stableKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey);
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{UnfilteredVersion}|{occurredAtUtc.UtcDateTime.Ticks}|{stableKey}");
        return EncodeValue(value);
    }

    public static string EncodeFiltered(
        DateTimeOffset occurredAtUtc,
        string stableKey,
        string filterFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(filterFingerprint);
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{FilteredVersion}|{occurredAtUtc.UtcDateTime.Ticks}|" +
            $"{stableKey}|{filterFingerprint}");
        return EncodeValue(value);
    }

    public static ReportingCursor? Decode(string? value, string errorPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorPrefix);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var decoded = DecodeValue(value);
            var parts = decoded.Split('|', 3, StringSplitOptions.None);
            if (parts.Length != 3 ||
                parts[0] != UnfilteredVersion ||
                !long.TryParse(
                    parts[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var ticks) ||
                string.IsNullOrWhiteSpace(parts[2]) ||
                parts[2].Length > 160)
            {
                throw new FormatException("Cursor payload is invalid.");
            }

            return new ReportingCursor(
                new DateTimeOffset(ticks, TimeSpan.Zero),
                parts[2]);
        }
        catch (Exception exception) when (
            exception is FormatException or
                ArgumentOutOfRangeException or
                DecoderFallbackException)
        {
            throw new ValidationFailedException(
                $"{errorPrefix}.cursor.invalid",
                "The reporting cursor is invalid.");
        }
    }

    public static ReportingCursor? DecodeFiltered(
        string? value,
        string errorPrefix,
        string expectedFilterFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFilterFingerprint);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var decoded = DecodeValue(value);
            var parts = decoded.Split('|', 4, StringSplitOptions.None);
            if (parts.Length != 4 ||
                parts[0] != FilteredVersion ||
                !long.TryParse(
                    parts[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var ticks) ||
                string.IsNullOrWhiteSpace(parts[2]) ||
                parts[2].Length > 160 ||
                !string.Equals(
                    parts[3],
                    expectedFilterFingerprint,
                    StringComparison.Ordinal))
            {
                throw new FormatException("Cursor payload is invalid.");
            }

            return new ReportingCursor(
                new DateTimeOffset(ticks, TimeSpan.Zero),
                parts[2],
                parts[3]);
        }
        catch (Exception exception) when (
            exception is FormatException or
                ArgumentOutOfRangeException or
                DecoderFallbackException)
        {
            throw new ValidationFailedException(
                $"{errorPrefix}.cursor.invalid",
                "The reporting cursor is invalid.");
        }
    }

    private static string EncodeValue(string value) =>
        Convert.ToBase64String(StrictUtf8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string DecodeValue(string value)
    {
        var encoded = value.Trim();
        if (encoded.Length > MaximumEncodedLength)
        {
            throw new FormatException("Cursor is too long.");
        }

        var normalized = encoded.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(
            normalized.Length + ((4 - (normalized.Length % 4)) % 4),
            '=');
        return StrictUtf8.GetString(Convert.FromBase64String(normalized));
    }
}
