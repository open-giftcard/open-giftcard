namespace GiftCardPlatform.Modules.Authorization.Domain;

internal sealed class PlatformBootstrapState
{
    public const int SingletonId = 1;

    private PlatformBootstrapState()
    {
    }

    public int Id { get; private set; } = SingletonId;

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public Guid? CompletedByUserId { get; private set; }

    public bool IsCompleted => CompletedAtUtc is not null;

    public void Complete(Guid userId, DateTimeOffset completedAtUtc)
    {
        if (IsCompleted)
        {
            throw new InvalidOperationException("Platform bootstrap is already complete.");
        }

        CompletedByUserId = userId;
        CompletedAtUtc = completedAtUtc.ToUniversalTime();
    }
}
