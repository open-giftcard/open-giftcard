using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Reporting.Application;

namespace GiftCardPlatform.UnitTests;

public sealed class ReportingCursorTests
{
    [Fact]
    public void Cursor_round_trips_timestamp_and_stable_key()
    {
        var occurredAt = new DateTimeOffset(
            2026,
            7,
            27,
            12,
            34,
            56,
            TimeSpan.Zero).AddTicks(1234);
        const string key = "lifecycle:0198dcb4-3ab9-7f20-a46b-6d9f4885817d";

        var encoded = ReportingCursorCodec.Encode(occurredAt, key);
        var decoded = ReportingCursorCodec.Decode(encoded, "reporting.test");

        Assert.NotNull(decoded);
        Assert.Equal(occurredAt, decoded.OccurredAtUtc);
        Assert.Equal(key, decoded.StableKey);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("djJ8NjM4ODkxMDAwMDAwMDAwMDAwfGtleQ")]
    [InlineData("djF8bm90LXRpY2tzfGtleQ")]
    public void Malformed_cursor_is_rejected_with_stable_code(string cursor)
    {
        var exception = Assert.Throws<ValidationFailedException>(
            () => ReportingCursorCodec.Decode(cursor, "reporting.test"));

        Assert.Equal("reporting.test.cursor.invalid", exception.Code);
    }

    [Fact]
    public void Empty_cursor_means_first_page()
    {
        Assert.Null(ReportingCursorCodec.Decode(null, "reporting.test"));
        Assert.Null(ReportingCursorCodec.Decode("  ", "reporting.test"));
    }

    [Fact]
    public void Filtered_cursor_requires_the_same_filter_fingerprint()
    {
        var occurredAt = new DateTimeOffset(
            2026,
            7,
            29,
            8,
            30,
            0,
            TimeSpan.Zero);
        const string key = "allocation:0198dcb4-3ab9-7f20-a46b-6d9f4885817d";
        const string fingerprint = "FINGERPRINT-A";

        var encoded = ReportingCursorCodec.EncodeFiltered(
            occurredAt,
            key,
            fingerprint);
        var decoded = ReportingCursorCodec.DecodeFiltered(
            encoded,
            "reporting.test",
            fingerprint);

        Assert.NotNull(decoded);
        Assert.Equal(occurredAt, decoded.OccurredAtUtc);
        Assert.Equal(key, decoded.StableKey);
        Assert.Equal(fingerprint, decoded.FilterFingerprint);
        var exception = Assert.Throws<ValidationFailedException>(
            () => ReportingCursorCodec.DecodeFiltered(
                encoded,
                "reporting.test",
                "FINGERPRINT-B"));
        Assert.Equal("reporting.test.cursor.invalid", exception.Code);
        Assert.Throws<ValidationFailedException>(
            () => ReportingCursorCodec.Decode(encoded, "reporting.test"));
    }
}
