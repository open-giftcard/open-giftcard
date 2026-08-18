using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Distribution.Contracts;

namespace GiftCardPlatform.Modules.Distribution.Domain;

internal enum DistributionInvitationState
{
    Pending = 1,
    Claimed = 2,
    Locked = 3,
    Expired = 4,
    Cancelled = 5,
}

internal sealed class DistributionInvitation
{
    private DistributionInvitation()
    {
        ClaimSecretHash = null!;
        BusinessReference = null!;
        IdempotencyKey = null!;
    }

    private DistributionInvitation(
        Guid id,
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        DistributionIntent intent,
        string claimSecretHash,
        DateTimeOffset claimExpiresAtUtc,
        Guid distributedByUserId,
        Guid distributedByMembershipId,
        DateTimeOffset distributedAtUtc)
    {
        Id = id;
        FundingOrganizationId = fundingOrganizationId;
        IssuingOrganizationId = issuingOrganizationId;
        GiftCardId = intent.GiftCardId;
        Kind = DistributionInvitationKind.Directed;
        ContactType = intent.ContactType;
        RecipientContact = intent.RecipientContact;
        MaskedRecipientContact = intent.MaskedRecipientContact;
        ClaimSecretHash = claimSecretHash;
        PinHash = null;
        State = DistributionInvitationState.Pending;
        ClaimExpiresAtUtc = Truncate(claimExpiresAtUtc);
        FailedClaimAttempts = 0;
        BusinessReference = intent.BusinessReference;
        IdempotencyKey = intent.IdempotencyKey;
        DistributedByUserId = distributedByUserId;
        DistributedByMembershipId = distributedByMembershipId;
        DistributedByPartnerClientId = null;
        DistributedAtUtc = Truncate(distributedAtUtc);
    }

    private DistributionInvitation(
        Guid id,
        Guid fundingOrganizationId,
        Guid giftCardId,
        string claimSecretHash,
        string pinHash,
        DateTimeOffset claimExpiresAtUtc,
        string businessReference,
        string idempotencyKey,
        Guid partnerClientId,
        DateTimeOffset distributedAtUtc)
    {
        Id = id;
        FundingOrganizationId = fundingOrganizationId;
        IssuingOrganizationId = fundingOrganizationId;
        GiftCardId = giftCardId;
        Kind = DistributionInvitationKind.OrphanPin;
        ContactType = null;
        RecipientContact = null;
        MaskedRecipientContact = null;
        ClaimSecretHash = claimSecretHash;
        PinHash = pinHash;
        State = DistributionInvitationState.Pending;
        ClaimExpiresAtUtc = Truncate(claimExpiresAtUtc);
        FailedClaimAttempts = 0;
        BusinessReference = businessReference;
        IdempotencyKey = idempotencyKey;
        DistributedByUserId = partnerClientId;
        DistributedByMembershipId = null;
        DistributedByPartnerClientId = partnerClientId;
        DistributedAtUtc = Truncate(distributedAtUtc);
    }

    public Guid Id { get; private set; }

    public Guid FundingOrganizationId { get; private set; }

    public Guid IssuingOrganizationId { get; private set; }

    public Guid GiftCardId { get; private set; }

    public DistributionInvitationKind Kind { get; private set; }

    public RecipientContactType? ContactType { get; private set; }

    public string? RecipientContact { get; private set; }

    public string? MaskedRecipientContact { get; private set; }

    public string ClaimSecretHash { get; private set; }

    public string? PinHash { get; private set; }

    public DistributionInvitationState State { get; private set; }

    public DateTimeOffset ClaimExpiresAtUtc { get; private set; }

    public int FailedClaimAttempts { get; private set; }

    public string BusinessReference { get; private set; }

    public string IdempotencyKey { get; private set; }

    public Guid DistributedByUserId { get; private set; }

    public Guid? DistributedByMembershipId { get; private set; }

    public Guid? DistributedByPartnerClientId { get; private set; }

    public DateTimeOffset DistributedAtUtc { get; private set; }

    public Guid? ClaimedByUserId { get; private set; }

    public DateTimeOffset? ClaimedAtUtc { get; private set; }

    public string? ClaimIdempotencyKey { get; private set; }

    public bool? IdentityWasCreatedOnClaim { get; private set; }

    public static DistributionInvitation Create(
        Guid id,
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        DistributionIntent intent,
        string claimSecretHash,
        DateTimeOffset claimExpiresAtUtc,
        Guid distributedByUserId,
        Guid distributedByMembershipId,
        DateTimeOffset distributedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (id == Guid.Empty ||
            fundingOrganizationId == Guid.Empty ||
            issuingOrganizationId == Guid.Empty ||
            distributedByUserId == Guid.Empty ||
            distributedByMembershipId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "distribution.scope.required",
                "Invitation, organization, actor, and membership identifiers are required.");
        }

        var now = distributedAtUtc.ToUniversalTime();
        if (claimExpiresAtUtc.ToUniversalTime() <= now)
        {
            throw new ValidationFailedException(
                "distribution.claim_expiry.invalid",
                "Claim expiration must be later than distribution time.");
        }

        if (claimSecretHash.Length != ClaimTokenCodec.HashHexLength)
        {
            throw new ValidationFailedException(
                "distribution.claim_token.invalid",
                "The claim token hash is invalid.");
        }

        return new DistributionInvitation(
            id,
            fundingOrganizationId,
            issuingOrganizationId,
            intent,
            claimSecretHash,
            claimExpiresAtUtc,
            distributedByUserId,
            distributedByMembershipId,
            now);
    }

    public static DistributionInvitation CreateOrphanPin(
        Guid id,
        Guid fundingOrganizationId,
        Guid giftCardId,
        string claimSecretHash,
        string pinHash,
        DateTimeOffset claimExpiresAtUtc,
        string businessReference,
        string idempotencyKey,
        Guid partnerClientId,
        DateTimeOffset distributedAtUtc)
    {
        if (id == Guid.Empty || fundingOrganizationId == Guid.Empty ||
            giftCardId == Guid.Empty || partnerClientId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "distribution.scope.required",
                "Invitation, organization, card, and partner client identifiers are required.");
        }

        var now = distributedAtUtc.ToUniversalTime();
        if (claimExpiresAtUtc.ToUniversalTime() <= now)
        {
            throw new ValidationFailedException(
                "distribution.claim_expiry.invalid",
                "Claim expiration must be later than distribution time.");
        }

        if (claimSecretHash.Length != ClaimTokenCodec.HashHexLength ||
            pinHash.Length != EpinCredentialCodec.PinHashHexLength)
        {
            throw new ValidationFailedException(
                "distribution.epin.credential.invalid",
                "The e-pin claim credential is invalid.");
        }

        var normalizedBusinessReference = NormalizeRequired(
            businessReference,
            DistributionIntent.BusinessReferenceMaxLength,
            1,
            "distribution.business_reference");
        var normalizedIdempotencyKey = NormalizeRequired(
            idempotencyKey,
            DistributionIntent.IdempotencyKeyMaxLength,
            DistributionIntent.IdempotencyKeyMinLength,
            "distribution.idempotency_key");

        return new DistributionInvitation(
            id,
            fundingOrganizationId,
            giftCardId,
            claimSecretHash,
            pinHash,
            claimExpiresAtUtc,
            normalizedBusinessReference,
            normalizedIdempotencyKey,
            partnerClientId,
            now);
    }

    public bool Matches(
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        DistributionIntent intent) =>
        Kind == DistributionInvitationKind.Directed &&
        FundingOrganizationId == fundingOrganizationId &&
        IssuingOrganizationId == issuingOrganizationId &&
        GiftCardId == intent.GiftCardId &&
        ContactType == intent.ContactType &&
        RecipientContact == intent.RecipientContact &&
        BusinessReference == intent.BusinessReference;

    public bool VerifySecret(ReadOnlySpan<byte> secret) =>
        ClaimTokenCodec.Matches(ClaimSecretHash, secret);

    public bool VerifyPin(string? pin, ReadOnlySpan<byte> deliveryKey) =>
        Kind != DistributionInvitationKind.OrphanPin ||
        EpinCredentialCodec.MatchesPin(Id, pin, PinHash ?? string.Empty, deliveryKey);

    public bool MatchesOrphanMint(
        Guid fundingOrganizationId,
        Guid giftCardId,
        Guid partnerClientId,
        string businessReference) =>
        Kind == DistributionInvitationKind.OrphanPin &&
        FundingOrganizationId == fundingOrganizationId &&
        IssuingOrganizationId == fundingOrganizationId &&
        GiftCardId == giftCardId &&
        DistributedByPartnerClientId == partnerClientId &&
        BusinessReference == businessReference;

    public bool RecordFailedClaimAttempt(
        int maximumFailedAttempts,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFailedAttempts, 1);

        if (State != DistributionInvitationState.Pending)
        {
            return false;
        }

        if (ClaimExpiresAtUtc <= now.ToUniversalTime())
        {
            State = DistributionInvitationState.Expired;
            return true;
        }

        FailedClaimAttempts++;
        if (FailedClaimAttempts >= maximumFailedAttempts)
        {
            State = DistributionInvitationState.Locked;
        }

        return true;
    }

    public void EnsureClaimableAt(DateTimeOffset now)
    {
        if (State == DistributionInvitationState.Claimed)
        {
            return;
        }

        if (State == DistributionInvitationState.Pending &&
            ClaimExpiresAtUtc <= now.ToUniversalTime())
        {
            State = DistributionInvitationState.Expired;
        }

        if (State != DistributionInvitationState.Pending)
        {
            throw new ConflictException(
                "distribution.claim.unavailable",
                "The claim invitation is unavailable.");
        }
    }

    public bool MatchesCompletedClaim(string idempotencyKey) =>
        State == DistributionInvitationState.Claimed &&
        string.Equals(
            ClaimIdempotencyKey,
            NormalizeClaimIdempotencyKey(idempotencyKey),
            StringComparison.Ordinal);

    public void CompleteClaim(
        Guid ownerUserId,
        bool identityWasCreated,
        string idempotencyKey,
        DateTimeOffset now)
    {
        if (ownerUserId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "distribution.claim.identity.required",
                "A recipient identity is required.");
        }

        var normalizedKey = NormalizeClaimIdempotencyKey(idempotencyKey);
        if (State == DistributionInvitationState.Claimed &&
            ClaimedByUserId == ownerUserId &&
            ClaimIdempotencyKey == normalizedKey &&
            IdentityWasCreatedOnClaim == identityWasCreated)
        {
            return;
        }

        if (State == DistributionInvitationState.Claimed)
        {
            throw new ConflictException(
                "distribution.claim.already_completed",
                "The invitation was already claimed.");
        }

        EnsureClaimableAt(now);
        State = DistributionInvitationState.Claimed;
        ClaimedByUserId = ownerUserId;
        ClaimedAtUtc = Truncate(now);
        ClaimIdempotencyKey = normalizedKey;
        IdentityWasCreatedOnClaim = identityWasCreated;
    }

    public bool CloseForCardLifecycle(
        DistributionLifecycleClosure closure)
    {
        if (!Enum.IsDefined(closure))
        {
            throw new ValidationFailedException(
                "distribution.lifecycle_closure.invalid",
                "The distribution lifecycle closure is invalid.");
        }

        if (State != DistributionInvitationState.Pending)
        {
            return false;
        }

        State = closure == DistributionLifecycleClosure.Cancelled
            ? DistributionInvitationState.Cancelled
            : DistributionInvitationState.Expired;
        return true;
    }

    public static string NormalizeClaimIdempotencyKey(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < DistributionIntent.IdempotencyKeyMinLength or
            > DistributionIntent.IdempotencyKeyMaxLength)
        {
            throw new ValidationFailedException(
                "distribution.claim.idempotency_key.invalid_length",
                $"Value must be between {DistributionIntent.IdempotencyKeyMinLength} " +
                $"and {DistributionIntent.IdempotencyKeyMaxLength} characters.");
        }

        return normalized;
    }

    private static DateTimeOffset Truncate(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }

    private static string NormalizeRequired(
        string? value,
        int maximumLength,
        int minimumLength,
        string errorPrefix)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length < minimumLength || normalized.Length > maximumLength)
        {
            throw new ValidationFailedException(
                $"{errorPrefix}.invalid_length",
                $"Value must be between {minimumLength} and {maximumLength} characters.");
        }

        return normalized;
    }
}
