namespace GiftCardPlatform.Modules.Identity.Domain;

internal sealed class RefreshToken
{
    private RefreshToken()
    {
    }

    private RefreshToken(
        Guid id,
        Guid sessionId,
        Guid tokenFamilyId,
        string tokenHash,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Id = id;
        SessionId = sessionId;
        TokenFamilyId = tokenFamilyId;
        TokenHash = tokenHash;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid SessionId { get; private set; }

    public Guid TokenFamilyId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public Guid? ReplacedByTokenId { get; private set; }

    public static RefreshToken Create(
        Guid sessionId,
        Guid tokenFamilyId,
        string tokenHash,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc) =>
        new(
            Guid.CreateVersion7(),
            sessionId,
            tokenFamilyId,
            tokenHash,
            createdAtUtc.ToUniversalTime(),
            expiresAtUtc.ToUniversalTime());

    public void Consume(DateTimeOffset consumedAtUtc, Guid replacementTokenId)
    {
        ConsumedAtUtc = consumedAtUtc.ToUniversalTime();
        ReplacedByTokenId = replacementTokenId;
    }

    public void Revoke(DateTimeOffset revokedAtUtc) =>
        RevokedAtUtc ??= revokedAtUtc.ToUniversalTime();
}
