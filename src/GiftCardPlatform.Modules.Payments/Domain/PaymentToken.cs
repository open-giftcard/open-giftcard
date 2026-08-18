using GiftCardPlatform.BuildingBlocks.Errors;

namespace GiftCardPlatform.Modules.Payments.Domain;

/// <summary>
/// One short-lived, single-use payment credential (ADR-017).
///
/// There is deliberately no state column. A token is consumed exactly once —
/// <see cref="ConsumedAtUtc"/> records that — and expiry is derived from the
/// server clock against <see cref="ExpiresAtUtc"/>. Storing a redundant state
/// enum would create a second source of truth that could disagree with the
/// clock.
/// </summary>
internal sealed class PaymentToken
{
    public Guid Id { get; private init; }

    public Guid GiftCardId { get; private init; }

    /// <summary>Tenant key: the card's funding root, for RLS and reporting.</summary>
    public Guid FundingOrganizationId { get; private init; }

    public Guid OwnerUserId { get; private init; }

    public string SecretHash { get; private init; } = string.Empty;

    public string? NumericCodeHash { get; private init; }

    public DateTimeOffset IssuedAtUtc { get; private init; }

    public DateTimeOffset ExpiresAtUtc { get; private init; }

    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    public static PaymentToken Issue(
        Guid id,
        Guid giftCardId,
        Guid fundingOrganizationId,
        Guid ownerUserId,
        string secretHash,
        string numericCodeHash,
        DateTimeOffset now,
        int lifetimeSeconds)
    {
        if (id == Guid.Empty || giftCardId == Guid.Empty ||
            fundingOrganizationId == Guid.Empty || ownerUserId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "payment.token.scope.required",
                "Token, card, funding organization, and owner identifiers are required.");
        }

        if (secretHash is null || secretHash.Length != PaymentTokenCodec.HashHexLength)
        {
            throw new ValidationFailedException(
                "payment.token.secret.invalid",
                "A payment token secret hash is required.");
        }

        if (numericCodeHash is null ||
            numericCodeHash.Length != NumericPaymentCodeCodec.HashHexLength)
        {
            throw new ValidationFailedException(
                "payment.token.numeric_code.invalid",
                "A numeric payment code hash is required.");
        }

        if (lifetimeSeconds <= 0)
        {
            throw new ValidationFailedException(
                "payment.token.lifetime.invalid",
                "The payment token lifetime must be positive.");
        }

        var issuedAt = TruncateToPostgresPrecision(now);
        return new PaymentToken
        {
            Id = id,
            GiftCardId = giftCardId,
            FundingOrganizationId = fundingOrganizationId,
            OwnerUserId = ownerUserId,
            SecretHash = secretHash,
            NumericCodeHash = numericCodeHash,
            IssuedAtUtc = issuedAt,
            ExpiresAtUtc = issuedAt.AddSeconds(lifetimeSeconds),
        };
    }

    /// <summary>
    /// True only while the credential may still be presented. Consumed and
    /// expired are separate facts internally but must never be distinguishable
    /// to a caller (ADR-017).
    /// </summary>
    public bool IsPresentable(DateTimeOffset now) =>
        ConsumedAtUtc is null && ExpiresAtUtc > TruncateToPostgresPrecision(now);

    /// <summary>
    /// Spends the credential. Single use is the whole point, so a second call is
    /// refused rather than tolerated; the database trigger refuses it again if
    /// application code ever gets this wrong (ADR-017).
    /// </summary>
    public void Consume(DateTimeOffset now)
    {
        if (ConsumedAtUtc is not null)
        {
            throw new ConflictException(
                "payment.credential.consumed",
                "The payment credential has already been used.");
        }

        ConsumedAtUtc = TruncateToPostgresPrecision(now);
    }

    private static DateTimeOffset TruncateToPostgresPrecision(DateTimeOffset value) =>
        new(
            value.UtcDateTime.Ticks - (value.UtcDateTime.Ticks % 10),
            TimeSpan.Zero);
}
