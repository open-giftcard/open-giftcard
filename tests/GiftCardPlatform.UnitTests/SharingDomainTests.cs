using GiftCardPlatform.Modules.Sharing.Contracts;
using GiftCardPlatform.Modules.Sharing.Domain;

namespace GiftCardPlatform.UnitTests;

public sealed class SharingDomainTests
{
    [Fact]
    public void Token_is_256_bit_parseable_and_matches_only_its_persisted_hash()
    {
        var shareId = Guid.CreateVersion7();
        var issued = ShareTokenCodec.Create(shareId);

        Assert.True(ShareTokenCodec.TryParse(issued.RawToken, out var parsedId, out var secret));
        Assert.Equal(shareId, parsedId);
        Assert.Equal(ShareTokenCodec.SecretByteCount, secret.Length);
        Assert.True(ShareTokenCodec.Matches(issued.SecretHash, secret));
        Assert.False(ShareTokenCodec.Matches(issued.SecretHash, new byte[32]));
        Assert.DoesNotContain(Convert.ToHexString(secret), issued.SecretHash, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAA")]
    public void AWellFormedIdentifierWithAnUndersizedSecretLeavesNoParsedShareId(
        string encodedSecret)
    {
        // The parsed identifier establishes the transaction-local RLS candidate
        // (app.share_id), so a failed parse must not leave one populated.
        var shareId = Guid.CreateVersion7();

        var parsed = ShareTokenCodec.TryParse(
            $"{shareId:N}.{encodedSecret}",
            out var parsedShareId,
            out var secret);

        Assert.False(parsed);
        Assert.Equal(Guid.Empty, parsedShareId);
        Assert.Empty(secret);
    }

    [Fact]
    public void Pin_is_six_digits_and_slow_hash_matches_without_storing_plaintext()
    {
        var issued = SharePinCodec.Create();

        Assert.Equal(6, issued.RawPin.Length);
        Assert.All(issued.RawPin, character => Assert.InRange(character, '0', '9'));
        Assert.True(SharePinCodec.Matches(issued.PersistedHash, issued.RawPin));
        Assert.False(SharePinCodec.Matches(issued.PersistedHash, "999999" == issued.RawPin ? "000000" : "999999"));
        Assert.DoesNotContain(issued.RawPin, issued.PersistedHash, StringComparison.Ordinal);
    }

    [Fact]
    public void Fifth_failed_pin_locks_and_releases_the_active_reservation_state()
    {
        var now = DateTimeOffset.UtcNow;
        var token = ShareTokenCodec.Create(Guid.CreateVersion7());
        var pin = SharePinCodec.Create();
        var share = GiftCardShare.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            25m,
            "TRY",
            token.SecretHash,
            pin.PersistedHash,
            "create-123456",
            now,
            now.AddHours(24));

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.True(share.RecordFailedPinAttempt(5, now.AddMinutes(attempt)));
        }

        Assert.Equal(GiftCardShareState.Locked, share.State);
        Assert.Equal(5, share.FailedPinAttempts);
        Assert.NotNull(share.ClosedAtUtc);
    }

    [Fact]
    public void Claim_records_planned_lineage_and_completes_once()
    {
        var now = DateTimeOffset.UtcNow;
        var shareId = Guid.CreateVersion7();
        var sender = Guid.CreateVersion7();
        var recipient = Guid.CreateVersion7();
        var child = Guid.CreateVersion7();
        var transaction = Guid.CreateVersion7();
        var token = ShareTokenCodec.Create(shareId);
        var pin = SharePinCodec.Create();
        var share = GiftCardShare.Create(
            shareId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            sender,
            10m,
            "TRY",
            token.SecretHash,
            pin.PersistedHash,
            "create-123456",
            now,
            now.AddHours(24));

        share.BeginClaim(recipient, child, transaction, "claim-123456", now.AddMinutes(1));
        share.CompleteClaim(now.AddMinutes(1));

        Assert.Equal(GiftCardShareState.Claimed, share.State);
        Assert.Equal(recipient, share.ClaimedByUserId);
        Assert.Equal(child, share.ChildGiftCardId);
        Assert.Equal(transaction, share.LedgerTransactionId);
        Assert.True(share.MatchesCompletedClaim(recipient, "claim-123456"));
    }

    [Fact]
    public void Direct_invitation_has_no_pin_and_records_new_identity_claim_state()
    {
        var now = DateTimeOffset.UtcNow;
        var shareId = Guid.CreateVersion7();
        var sender = Guid.CreateVersion7();
        var recipient = Guid.CreateVersion7();
        var token = ShareTokenCodec.Create(shareId);
        var share = GiftCardShare.CreateDirect(
            shareId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            sender,
            15m,
            "TRY",
            token.SecretHash,
            GiftCardShareContactType.Email,
            "recipient@example.com",
            "r***@example.com",
            "direct-123456",
            now,
            now.AddHours(24));

        Assert.Equal(GiftCardShareKind.DirectInvitation, share.Kind);
        Assert.Null(share.PinHash);
        Assert.False(share.VerifyPin("123456"));
        Assert.True(share.VerifySecret(Convert.FromBase64String(
            token.RawToken[(token.RawToken.IndexOf('.') + 1)..]
                .Replace('-', '+').Replace('_', '/').PadRight(44, '='))));

        share.BeginClaim(
            recipient,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "direct-claim-123456",
            now.AddMinutes(1));
        share.CompleteClaim(now.AddMinutes(1), identityWasCreated: true);

        Assert.True(share.MatchesCompletedDirectClaim("direct-claim-123456"));
        Assert.True(share.IdentityWasCreatedOnClaim);
        Assert.Equal(recipient, share.ClaimedByUserId);
    }

    [Fact]
    public void Direct_invitation_cannot_record_pin_failures()
    {
        var now = DateTimeOffset.UtcNow;
        var shareId = Guid.CreateVersion7();
        var token = ShareTokenCodec.Create(shareId);
        var share = GiftCardShare.CreateDirect(
            shareId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            15m,
            "TRY",
            token.SecretHash,
            GiftCardShareContactType.Phone,
            "+905551234567",
            "+90***4567",
            "direct-123456",
            now,
            now.AddHours(24));

        Assert.Throws<GiftCardPlatform.BuildingBlocks.Errors.ConflictException>(
            () => share.RecordFailedPinAttempt(5, now.AddMinutes(1)));
    }

    [Fact]
    public void Direct_invitation_expiry_releases_the_pending_state_without_claim_identity()
    {
        var now = DateTimeOffset.UtcNow;
        var shareId = Guid.CreateVersion7();
        var token = ShareTokenCodec.Create(shareId);
        var share = GiftCardShare.CreateDirect(
            shareId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            15m,
            "TRY",
            token.SecretHash,
            GiftCardShareContactType.Email,
            "recipient@example.com",
            "r***@example.com",
            "direct-123456",
            now,
            now.AddHours(24));

        Assert.True(share.Expire(now.AddHours(24)));
        Assert.Equal(GiftCardShareState.Expired, share.State);
        Assert.Null(share.ClaimedByUserId);
        Assert.Null(share.IdentityWasCreatedOnClaim);
    }
}
