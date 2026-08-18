namespace GiftCardPlatform.Modules.Identity.Domain;

internal sealed class UserSession
{
    private UserSession()
    {
    }

    private UserSession(
        Guid id,
        Guid userId,
        Guid tokenFamilyId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Id = id;
        UserId = userId;
        TokenFamilyId = tokenFamilyId;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid TokenFamilyId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public string? RevocationReason { get; private set; }

    public bool IsRevoked => RevokedAtUtc is not null;

    public static UserSession Create(
        Guid userId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc) =>
        new(
            Guid.CreateVersion7(),
            userId,
            Guid.CreateVersion7(),
            createdAtUtc.ToUniversalTime(),
            expiresAtUtc.ToUniversalTime());

    public void Revoke(DateTimeOffset revokedAtUtc, string reason)
    {
        if (RevokedAtUtc is not null)
        {
            return;
        }

        RevokedAtUtc = revokedAtUtc.ToUniversalTime();
        RevocationReason = reason;
    }
}
