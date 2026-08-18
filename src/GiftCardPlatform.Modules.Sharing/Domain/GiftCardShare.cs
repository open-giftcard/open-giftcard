using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Sharing.Contracts;

namespace GiftCardPlatform.Modules.Sharing.Domain;

internal sealed class GiftCardShare
{
    public const int CurrencyLength = 3;
    public const int IdempotencyKeyMinLength = 8;
    public const int IdempotencyKeyMaxLength = 128;
    public const int AmountScale = 4;
    public const decimal MaximumAmount = 999_999_999_999_999.9999m;

    private GiftCardShare()
    {
        Currency = null!;
        ClaimSecretHash = null!;
        CreateIdempotencyKey = null!;
    }

    private GiftCardShare(
        Guid id,
        Guid sourceGiftCardId,
        Guid fundingOrganizationId,
        Guid senderUserId,
        decimal amount,
        string currency,
        string claimSecretHash,
        string? pinHash,
        GiftCardShareKind kind,
        GiftCardShareContactType? recipientContactType,
        string? recipientContact,
        string? maskedRecipientContact,
        string createIdempotencyKey,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Id = id;
        SourceGiftCardId = sourceGiftCardId;
        FundingOrganizationId = fundingOrganizationId;
        SenderUserId = senderUserId;
        Amount = amount;
        Currency = currency;
        ClaimSecretHash = claimSecretHash;
        PinHash = pinHash;
        Kind = kind;
        RecipientContactType = recipientContactType;
        RecipientContact = recipientContact;
        MaskedRecipientContact = maskedRecipientContact;
        CreateIdempotencyKey = createIdempotencyKey;
        State = GiftCardShareState.Pending;
        CreatedAtUtc = Truncate(createdAtUtc);
        ExpiresAtUtc = Truncate(expiresAtUtc);
    }

    public Guid Id { get; private set; }

    public GiftCardShareKind Kind { get; private set; }

    public Guid SourceGiftCardId { get; private set; }

    public Guid FundingOrganizationId { get; private set; }

    public Guid SenderUserId { get; private set; }

    public Guid? ClaimedByUserId { get; private set; }

    public Guid? ChildGiftCardId { get; private set; }

    public Guid? LedgerTransactionId { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; }

    public string ClaimSecretHash { get; private set; }

    public string? PinHash { get; private set; }

    public GiftCardShareContactType? RecipientContactType { get; private set; }

    public string? RecipientContact { get; private set; }

    public string? MaskedRecipientContact { get; private set; }

    public bool? IdentityWasCreatedOnClaim { get; private set; }

    public GiftCardShareState State { get; private set; }

    public int FailedPinAttempts { get; private set; }

    public string CreateIdempotencyKey { get; private set; }

    public string? ClaimIdempotencyKey { get; private set; }

    public string? CancelIdempotencyKey { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? ClaimedAtUtc { get; private set; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    public static GiftCardShare Create(
        Guid id,
        Guid sourceGiftCardId,
        Guid fundingOrganizationId,
        Guid senderUserId,
        decimal amount,
        string currency,
        string claimSecretHash,
        string pinHash,
        string? idempotencyKey,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (id == Guid.Empty || sourceGiftCardId == Guid.Empty ||
            fundingOrganizationId == Guid.Empty || senderUserId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "sharing.scope.required",
                "Share, card, organization, and sender identifiers are required.");
        }

        var normalizedAmount = NormalizeAmount(amount);
        var normalizedCurrency = currency?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalizedCurrency.Length != CurrencyLength ||
            normalizedCurrency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ValidationFailedException("sharing.currency.invalid", "Currency must be ISO 4217.");
        }

        if (claimSecretHash.Length != ShareTokenCodec.HashHexLength ||
            pinHash.Length != SharePinCodec.PersistedLength)
        {
            throw new ValidationFailedException("sharing.credentials.invalid", "Share credentials are invalid.");
        }

        var created = Truncate(createdAtUtc);
        var expires = Truncate(expiresAtUtc);
        if (expires <= created)
        {
            throw new ValidationFailedException("sharing.expiry.invalid", "Share expiry must follow creation.");
        }

        return new GiftCardShare(
            id,
            sourceGiftCardId,
            fundingOrganizationId,
            senderUserId,
            normalizedAmount,
            normalizedCurrency,
            claimSecretHash,
            pinHash,
            GiftCardShareKind.ProtectedLink,
            recipientContactType: null,
            recipientContact: null,
            maskedRecipientContact: null,
            NormalizeIdempotencyKey(idempotencyKey, "sharing.create.idempotency_key"),
            created,
            expires);
    }

    public static GiftCardShare CreateDirect(
        Guid id,
        Guid sourceGiftCardId,
        Guid fundingOrganizationId,
        Guid senderUserId,
        decimal amount,
        string currency,
        string claimSecretHash,
        GiftCardShareContactType contactType,
        string recipientContact,
        string maskedRecipientContact,
        string? idempotencyKey,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (id == Guid.Empty || sourceGiftCardId == Guid.Empty || fundingOrganizationId == Guid.Empty ||
            senderUserId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "sharing.scope.required",
                "Share, card, organization, and sender identifiers are required.");
        }

        if (!Enum.IsDefined(contactType) || string.IsNullOrWhiteSpace(recipientContact) ||
            recipientContact.Length > 320 || string.IsNullOrWhiteSpace(maskedRecipientContact) ||
            maskedRecipientContact.Length > 320)
        {
            throw new ValidationFailedException(
                "sharing.direct.contact.invalid",
                "A normalized email or phone recipient is required.");
        }

        if (claimSecretHash.Length != ShareTokenCodec.HashHexLength)
        {
            throw new ValidationFailedException(
                "sharing.credentials.invalid",
                "Share credentials are invalid.");
        }

        var normalizedCurrency = NormalizeCurrency(currency);
        var created = Truncate(createdAtUtc);
        var expires = Truncate(expiresAtUtc);
        if (expires <= created)
        {
            throw new ValidationFailedException("sharing.expiry.invalid", "Share expiry must follow creation.");
        }

        return new GiftCardShare(
            id,
            sourceGiftCardId,
            fundingOrganizationId,
            senderUserId,
            NormalizeAmount(amount),
            normalizedCurrency,
            claimSecretHash,
            pinHash: null,
            GiftCardShareKind.DirectInvitation,
            contactType,
            recipientContact,
            maskedRecipientContact,
            NormalizeIdempotencyKey(idempotencyKey, "sharing.create.idempotency_key"),
            created,
            expires);
    }

    public bool MatchesCreate(Guid sourceGiftCardId, decimal amount) =>
        Kind == GiftCardShareKind.ProtectedLink && SourceGiftCardId == sourceGiftCardId &&
        Amount == NormalizeAmount(amount);

    public bool MatchesDirectCreate(
        Guid sourceGiftCardId,
        decimal amount,
        GiftCardShareContactType contactType,
        string recipientContact) =>
        Kind == GiftCardShareKind.DirectInvitation && SourceGiftCardId == sourceGiftCardId &&
        Amount == NormalizeAmount(amount) && RecipientContactType == contactType &&
        string.Equals(RecipientContact, recipientContact, StringComparison.Ordinal);

    public bool VerifySecret(ReadOnlySpan<byte> secret) =>
        ShareTokenCodec.Matches(ClaimSecretHash, secret);

    public bool VerifyPin(string? pin) =>
        Kind == GiftCardShareKind.ProtectedLink && SharePinCodec.Matches(PinHash, pin);

    public bool RecordFailedPinAttempt(int maximumAttempts, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        if (Kind != GiftCardShareKind.ProtectedLink)
        {
            throw new ConflictException("sharing.claim.kind.invalid", "The share claim type is invalid.");
        }

        if (State != GiftCardShareState.Pending)
        {
            return false;
        }

        var occurredAt = Truncate(now);
        if (ExpiresAtUtc <= occurredAt)
        {
            State = GiftCardShareState.Expired;
            ClosedAtUtc = occurredAt;
            return true;
        }

        FailedPinAttempts++;
        if (FailedPinAttempts >= maximumAttempts)
        {
            State = GiftCardShareState.Locked;
            ClosedAtUtc = occurredAt;
        }

        return true;
    }

    public void EnsureClaimable(DateTimeOffset now)
    {
        var occurredAt = Truncate(now);
        if (State == GiftCardShareState.Pending && ExpiresAtUtc <= occurredAt)
        {
            State = GiftCardShareState.Expired;
            ClosedAtUtc = occurredAt;
        }

        if (State != GiftCardShareState.Pending)
        {
            throw new ConflictException("sharing.claim.unavailable", "The share is unavailable.");
        }
    }

    public void BeginClaim(
        Guid recipientUserId,
        Guid childGiftCardId,
        Guid ledgerTransactionId,
        string? idempotencyKey,
        DateTimeOffset now)
    {
        if (recipientUserId == Guid.Empty || childGiftCardId == Guid.Empty || ledgerTransactionId == Guid.Empty)
        {
            throw new ValidationFailedException("sharing.claim.scope.required", "Claim identifiers are required.");
        }

        if (recipientUserId == SenderUserId)
        {
            throw new ConflictException("sharing.claim.self.forbidden", "A share cannot be claimed by its sender.");
        }

        EnsureClaimable(now);
        State = GiftCardShareState.Claiming;
        ClaimedByUserId = recipientUserId;
        ChildGiftCardId = childGiftCardId;
        LedgerTransactionId = ledgerTransactionId;
        ClaimIdempotencyKey = NormalizeIdempotencyKey(
            idempotencyKey,
            "sharing.claim.idempotency_key");
    }

    public void CompleteClaim(DateTimeOffset now, bool? identityWasCreated = null)
    {
        if (State != GiftCardShareState.Claiming || ClaimedByUserId is null ||
            ChildGiftCardId is null || LedgerTransactionId is null || ClaimIdempotencyKey is null)
        {
            throw new ConflictException("sharing.claim.state.invalid", "The share claim is not ready to complete.");
        }

        if ((Kind == GiftCardShareKind.DirectInvitation) != identityWasCreated.HasValue)
        {
            throw new ConflictException(
                "sharing.claim.identity_state.invalid",
                "The share identity result does not match its claim type.");
        }

        State = GiftCardShareState.Claimed;
        IdentityWasCreatedOnClaim = identityWasCreated;
        ClaimedAtUtc = Truncate(now);
        ClosedAtUtc = ClaimedAtUtc;
    }

    public bool MatchesCompletedClaim(Guid recipientUserId, string? idempotencyKey) =>
        Kind == GiftCardShareKind.ProtectedLink && State == GiftCardShareState.Claimed &&
        ClaimedByUserId == recipientUserId &&
        ClaimIdempotencyKey == NormalizeIdempotencyKey(
            idempotencyKey,
            "sharing.claim.idempotency_key");

    public bool MatchesCompletedDirectClaim(string? idempotencyKey) =>
        Kind == GiftCardShareKind.DirectInvitation && State == GiftCardShareState.Claimed &&
        ClaimedByUserId is not null && IdentityWasCreatedOnClaim is not null &&
        ClaimIdempotencyKey == NormalizeIdempotencyKey(
            idempotencyKey,
            "sharing.claim.idempotency_key");

    public void Cancel(Guid senderUserId, string? idempotencyKey, DateTimeOffset now)
    {
        var normalizedKey = NormalizeIdempotencyKey(
            idempotencyKey,
            "sharing.cancel.idempotency_key");
        if (State == GiftCardShareState.Cancelled && SenderUserId == senderUserId &&
            CancelIdempotencyKey == normalizedKey)
        {
            return;
        }

        if (SenderUserId != senderUserId)
        {
            throw new NotFoundException("sharing.not_found", "Share not found.");
        }

        EnsureClaimable(now);
        State = GiftCardShareState.Cancelled;
        CancelIdempotencyKey = normalizedKey;
        ClosedAtUtc = Truncate(now);
    }

    public bool Expire(DateTimeOffset now)
    {
        var occurredAt = Truncate(now);
        if (State != GiftCardShareState.Pending || ExpiresAtUtc > occurredAt)
        {
            return false;
        }

        State = GiftCardShareState.Expired;
        ClosedAtUtc = occurredAt;
        return true;
    }

    public bool CloseForSourceLifecycle(
        ShareSourceLifecycleClosure closure,
        DateTimeOffset now)
    {
        if (!Enum.IsDefined(closure))
        {
            throw new ValidationFailedException(
                "sharing.source_lifecycle.invalid",
                "The source lifecycle closure is invalid.");
        }

        if (State != GiftCardShareState.Pending)
        {
            return false;
        }

        State = closure == ShareSourceLifecycleClosure.Cancelled
            ? GiftCardShareState.Cancelled
            : GiftCardShareState.Expired;
        ClosedAtUtc = Truncate(now);
        return true;
    }

    public static string NormalizeIdempotencyKey(string? value, string errorCode)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < IdempotencyKeyMinLength or > IdempotencyKeyMaxLength)
        {
            throw new ValidationFailedException(
                $"{errorCode}.invalid_length",
                $"Value must be between {IdempotencyKeyMinLength} and {IdempotencyKeyMaxLength} characters.");
        }

        return normalized;
    }

    private static decimal NormalizeAmount(decimal amount)
    {
        if (amount <= 0 || amount > MaximumAmount || decimal.Round(amount, AmountScale) != amount)
        {
            throw new ValidationFailedException(
                "sharing.amount.invalid",
                "Amount must be positive and have no more than four decimal places.");
        }

        return amount;
    }

    private static string NormalizeCurrency(string? currency)
    {
        var normalized = currency?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length != CurrencyLength ||
            normalized.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ValidationFailedException("sharing.currency.invalid", "Currency must be ISO 4217.");
        }

        return normalized;
    }

    private static DateTimeOffset Truncate(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }
}
