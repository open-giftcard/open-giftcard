using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.Distribution.Domain;

namespace GiftCardPlatform.UnitTests;

public sealed class DistributionDomainTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Contact_is_normalized_and_masked_for_both_supported_channels()
    {
        var email = DistributionIntent.Create(
            Request(RecipientContactType.Email, " Recipient@Example.COM "));
        var phone = DistributionIntent.Create(
            Request(RecipientContactType.Phone, "+90 (555) 123-45-67"));

        Assert.Equal("recipient@example.com", email.RecipientContact);
        Assert.Equal("r***@example.com", email.MaskedRecipientContact);
        Assert.Equal("+905551234567", phone.RecipientContact);
        Assert.Equal("+90***4567", phone.MaskedRecipientContact);
    }

    [Theory]
    [InlineData(RecipientContactType.Email, "not-an-email")]
    [InlineData(RecipientContactType.Phone, "0555 123 45 67")]
    [InlineData(RecipientContactType.Phone, "+00123456789")]
    public void Invalid_recipient_contacts_are_rejected(
        RecipientContactType type,
        string value)
    {
        var exception = Assert.Throws<ValidationFailedException>(
            () => DistributionIntent.Create(Request(type, value)));

        Assert.StartsWith("distribution.", exception.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void Claim_token_contains_256_random_bits_and_only_its_hash_is_persistable()
    {
        var invitationId = Guid.CreateVersion7();
        var first = ClaimTokenCodec.Create(invitationId);
        var second = ClaimTokenCodec.Create(invitationId);

        Assert.NotEqual(first.RawToken, second.RawToken);
        Assert.Equal(ClaimTokenCodec.HashHexLength, first.SecretHash.Length);
        Assert.DoesNotContain(first.SecretHash, first.RawToken, StringComparison.Ordinal);
        Assert.True(
            ClaimTokenCodec.TryParse(
                first.RawToken,
                out var parsedInvitationId,
                out var secret));
        Assert.Equal(invitationId, parsedInvitationId);
        Assert.Equal(ClaimTokenCodec.SecretByteCount, secret.Length);
        Assert.True(ClaimTokenCodec.Matches(first.SecretHash, secret));
        Assert.False(ClaimTokenCodec.Matches(second.SecretHash, secret));
    }

    [Fact]
    public void Epin_credentials_are_retry_stable_and_verify_without_storing_raw_material()
    {
        var invitationId = Guid.CreateVersion7();
        var key = Enumerable.Range(1, EpinCredentialCodec.DeliveryKeyByteCount)
            .Select(value => (byte)value)
            .ToArray();

        var first = EpinCredentialCodec.Create(invitationId, key);
        var retry = EpinCredentialCodec.Create(invitationId, key);

        Assert.Equal(first, retry);
        Assert.Matches("^[0-9]{6}$", first.Pin);
        Assert.DoesNotContain(first.Pin, first.PinHash, StringComparison.Ordinal);
        Assert.True(EpinCredentialCodec.MatchesPin(
            invitationId,
            first.Pin,
            first.PinHash,
            key));
        var wrongPin = first.Pin == "000000" ? "000001" : "000000";
        Assert.False(EpinCredentialCodec.MatchesPin(
            invitationId,
            wrongPin,
            first.PinHash,
            key));
        Assert.True(ClaimTokenCodec.TryParse(
            first.ClaimToken,
            out var parsedInvitationId,
            out var claimSecret));
        Assert.Equal(invitationId, parsedInvitationId);
        Assert.True(ClaimTokenCodec.Matches(first.ClaimSecretHash, claimSecret));
    }

    [Fact]
    public void Orphan_invitation_has_no_preselected_recipient_and_is_client_bound()
    {
        var invitationId = Guid.CreateVersion7();
        var fundingId = Guid.CreateVersion7();
        var cardId = Guid.CreateVersion7();
        var partnerClientId = Guid.CreateVersion7();
        var key = Enumerable.Repeat((byte)42, EpinCredentialCodec.DeliveryKeyByteCount).ToArray();
        var credential = EpinCredentialCodec.Create(invitationId, key);

        var invitation = DistributionInvitation.CreateOrphanPin(
            invitationId,
            fundingId,
            cardId,
            credential.ClaimSecretHash,
            credential.PinHash,
            Now.AddYears(1),
            "ORDER-42",
            "partner-order-42",
            partnerClientId,
            Now);

        Assert.Equal(DistributionInvitationKind.OrphanPin, invitation.Kind);
        Assert.Null(invitation.ContactType);
        Assert.Null(invitation.RecipientContact);
        Assert.Null(invitation.DistributedByMembershipId);
        Assert.Equal(partnerClientId, invitation.DistributedByPartnerClientId);
        Assert.True(invitation.VerifyPin(credential.Pin, key));
        Assert.True(invitation.MatchesOrphanMint(
            fundingId,
            cardId,
            partnerClientId,
            "ORDER-42"));
        Assert.False(invitation.MatchesOrphanMint(
            fundingId,
            cardId,
            Guid.CreateVersion7(),
            "ORDER-42"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAA")]
    public void AWellFormedIdentifierWithAnUndersizedSecretLeavesNoParsedInvitationId(
        string encodedSecret)
    {
        // The parsed identifier establishes the transaction-local RLS candidate
        // (app.claim_invitation_id), so a failed parse must not leave one
        // populated.
        var invitationId = Guid.CreateVersion7();

        var parsed = ClaimTokenCodec.TryParse(
            $"{invitationId:N}.{encodedSecret}",
            out var parsedInvitationId,
            out var secret);

        Assert.False(parsed);
        Assert.Equal(Guid.Empty, parsedInvitationId);
        Assert.Empty(secret);
    }

    [Fact]
    public void Failed_attempts_are_bounded_and_lock_the_invitation()
    {
        var invitation = CreateInvitation();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.True(
                invitation.RecordFailedClaimAttempt(5, Now.AddMinutes(attempt)));
        }

        Assert.False(invitation.RecordFailedClaimAttempt(5, Now.AddMinutes(5)));
        Assert.Equal(5, invitation.FailedClaimAttempts);
        Assert.Equal(DistributionInvitationState.Locked, invitation.State);
        Assert.Throws<ConflictException>(() => invitation.EnsureClaimableAt(Now.AddMinutes(6)));
    }

    [Fact]
    public void Claim_is_single_transition_and_identical_completion_is_idempotent()
    {
        var invitation = CreateInvitation();
        var ownerUserId = Guid.CreateVersion7();

        invitation.CompleteClaim(
            ownerUserId,
            identityWasCreated: true,
            "claim-idempotency-42",
            Now.AddMinutes(1));
        invitation.CompleteClaim(
            ownerUserId,
            identityWasCreated: true,
            "claim-idempotency-42",
            Now.AddMinutes(2));

        Assert.Equal(DistributionInvitationState.Claimed, invitation.State);
        Assert.Equal(ownerUserId, invitation.ClaimedByUserId);
        Assert.True(invitation.IdentityWasCreatedOnClaim);
        Assert.True(invitation.MatchesCompletedClaim("claim-idempotency-42"));
        Assert.Throws<ConflictException>(
            () => invitation.CompleteClaim(
                Guid.CreateVersion7(),
                identityWasCreated: true,
                "different-claim-key",
                Now.AddMinutes(3)));
    }

    [Fact]
    public void Expired_invitation_cannot_be_claimed()
    {
        var invitation = CreateInvitation(expiresAt: Now.AddMinutes(1));

        var exception = Assert.Throws<ConflictException>(
            () => invitation.EnsureClaimableAt(Now.AddMinutes(2)));

        Assert.Equal("distribution.claim.unavailable", exception.Code);
        Assert.Equal(DistributionInvitationState.Expired, invitation.State);
    }

    private static DistributionInvitation CreateInvitation(
        DateTimeOffset? expiresAt = null)
    {
        var intent = DistributionIntent.Create(
            Request(RecipientContactType.Email, "recipient@example.com"));
        var id = Guid.CreateVersion7();
        var token = ClaimTokenCodec.Create(id);
        return DistributionInvitation.Create(
            id,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            intent,
            token.SecretHash,
            expiresAt ?? Now.AddHours(24),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Now);
    }

    private static DistributeGiftCardRequest Request(
        RecipientContactType type,
        string contact) =>
        new(
            Guid.CreateVersion7(),
            type,
            contact,
            "EMPLOYEE-AWARD-42",
            "distribution-award-42");
}
