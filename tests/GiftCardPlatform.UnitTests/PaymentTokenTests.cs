using System.Security.Cryptography;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Payments.Domain;

namespace GiftCardPlatform.UnitTests;

public sealed class PaymentTokenCodecTests
{
    [Fact]
    public void CreatedTokenCarriesItsIdentifierAndAVerifiableSecret()
    {
        var tokenId = Guid.CreateVersion7();

        var issued = PaymentTokenCodec.Create(tokenId);

        Assert.True(PaymentTokenCodec.TryParse(issued.RawToken, out var parsedId, out var secret));
        Assert.Equal(tokenId, parsedId);
        Assert.Equal(PaymentTokenCodec.SecretByteCount, secret.Length);
        Assert.True(PaymentTokenCodec.Matches(issued.SecretHash, secret));
    }

    [Fact]
    public void TheStoredHashDoesNotContainTheSecret()
    {
        // The persisted form must not be reversible into a usable credential.
        var issued = PaymentTokenCodec.Create(Guid.CreateVersion7());
        Assert.True(PaymentTokenCodec.TryParse(issued.RawToken, out _, out var secret));

        Assert.Equal(PaymentTokenCodec.HashHexLength, issued.SecretHash.Length);
        Assert.DoesNotContain(
            Convert.ToBase64String(secret).TrimEnd('='),
            issued.SecretHash,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(secret)),
            issued.SecretHash);
    }

    [Fact]
    public void EachIssuedTokenIsDistinct()
    {
        var first = PaymentTokenCodec.Create(Guid.CreateVersion7());
        var second = PaymentTokenCodec.Create(Guid.CreateVersion7());

        Assert.NotEqual(first.RawToken, second.RawToken);
        Assert.NotEqual(first.SecretHash, second.SecretHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef.")]
    [InlineData("0123456789abcdef0123456789abcdef.!!!!")]
    public void MalformedTokensAreRejectedWithoutThrowing(string? candidate)
    {
        Assert.False(PaymentTokenCodec.TryParse(candidate, out var id, out var secret));
        Assert.Equal(Guid.Empty, id);
        Assert.Empty(secret);
    }

    [Fact]
    public void ASecretFromAnotherTokenDoesNotMatch()
    {
        var issued = PaymentTokenCodec.Create(Guid.CreateVersion7());
        var other = PaymentTokenCodec.Create(Guid.CreateVersion7());
        Assert.True(PaymentTokenCodec.TryParse(other.RawToken, out _, out var otherSecret));

        Assert.False(PaymentTokenCodec.Matches(issued.SecretHash, otherSecret));
    }

    [Fact]
    public void AMalformedStoredHashNeverMatches()
    {
        var issued = PaymentTokenCodec.Create(Guid.CreateVersion7());
        Assert.True(PaymentTokenCodec.TryParse(issued.RawToken, out _, out var secret));

        Assert.False(PaymentTokenCodec.Matches("short", secret));
        Assert.False(PaymentTokenCodec.Matches(new string('Z', 64), secret));
    }
}

public sealed class NumericPaymentCodeCodecTests
{
    [Fact]
    public void CreatedCodeHasTwelveDigitsAndOnlyItsHashNeedsPersistence()
    {
        var issued = NumericPaymentCodeCodec.Create();

        Assert.Equal(NumericPaymentCodeCodec.DigitCount, issued.RawCode.Length);
        Assert.All(issued.RawCode, character => Assert.InRange(character, '0', '9'));
        Assert.Equal(NumericPaymentCodeCodec.HashHexLength, issued.CodeHash.Length);
        Assert.True(NumericPaymentCodeCodec.Matches(issued.CodeHash, issued.RawCode));
        Assert.DoesNotContain(issued.RawCode, issued.CodeHash, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("123456789012", "123456789012")]
    [InlineData("1234 5678 9012", "123456789012")]
    [InlineData("1234-5678-9012", "123456789012")]
    public void InputSeparatorsNormalizeToTheCanonicalDigits(string value, string expected)
    {
        Assert.True(NumericPaymentCodeCodec.TryNormalize(value, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123456")]
    [InlineData("1234567890123")]
    [InlineData("1234 5678 90A2")]
    [InlineData("１２３４５６７８９０１２")]
    public void InvalidCodesAreRejectedWithoutLeavingAnOutput(string? value)
    {
        Assert.False(NumericPaymentCodeCodec.TryNormalize(value, out var canonical));
        Assert.Empty(canonical);
    }

    [Fact]
    public void AnotherCodeAndMalformedStoredHashesNeverMatch()
    {
        var first = NumericPaymentCodeCodec.Create();
        var second = NumericPaymentCodeCodec.Create();

        Assert.False(NumericPaymentCodeCodec.Matches(first.CodeHash, second.RawCode));
        Assert.False(NumericPaymentCodeCodec.Matches("short", first.RawCode));
        Assert.False(NumericPaymentCodeCodec.Matches(new string('Z', 64), first.RawCode));
    }
}

public sealed class PaymentTokenLifetimeTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static PaymentToken Issue(int lifetimeSeconds = 60) =>
        PaymentToken.Issue(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new string('A', 64),
            new string('B', 64),
            Now,
            lifetimeSeconds);

    [Fact]
    public void ExpiryIsDerivedFromTheServerClockAndTheConfiguredLifetime()
    {
        var token = Issue();

        Assert.Equal(Now, token.IssuedAtUtc);
        Assert.Equal(Now.AddSeconds(60), token.ExpiresAtUtc);
        Assert.Null(token.ConsumedAtUtc);
    }

    [Fact]
    public void ATokenIsPresentableOnlyStrictlyBeforeItsExpiry()
    {
        var token = Issue();

        Assert.True(token.IsPresentable(Now));
        Assert.True(token.IsPresentable(Now.AddSeconds(59)));
        // Boundary: at exactly the expiry instant the credential is already dead.
        Assert.False(token.IsPresentable(Now.AddSeconds(60)));
        Assert.False(token.IsPresentable(Now.AddSeconds(61)));
    }

    [Fact]
    public void IssuanceRequiresEveryBindingIdentifier()
    {
        Assert.Throws<ValidationFailedException>(() => PaymentToken.Issue(
            Guid.Empty,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new string('A', 64),
            new string('B', 64),
            Now,
            60));

        Assert.Throws<ValidationFailedException>(() => PaymentToken.Issue(
            Guid.CreateVersion7(),
            Guid.Empty,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new string('A', 64),
            new string('B', 64),
            Now,
            60));

        Assert.Throws<ValidationFailedException>(() => PaymentToken.Issue(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.Empty,
            new string('A', 64),
            new string('B', 64),
            Now,
            60));
    }

    [Fact]
    public void IssuanceRejectsAMissingOrMalformedSecretHash()
    {
        Assert.Throws<ValidationFailedException>(() => PaymentToken.Issue(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "too-short",
            new string('B', 64),
            Now,
            60));
    }

    [Fact]
    public void IssuanceRejectsANonPositiveLifetime()
    {
        Assert.Throws<ValidationFailedException>(() => Issue(0));
        Assert.Throws<ValidationFailedException>(() => Issue(-1));
    }
}
