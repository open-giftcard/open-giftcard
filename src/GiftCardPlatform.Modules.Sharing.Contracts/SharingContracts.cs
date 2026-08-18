using GiftCardPlatform.Modules.GiftCards.Contracts;

namespace GiftCardPlatform.Modules.Sharing.Contracts;

public enum GiftCardShareState
{
    Pending = 1,
    Claiming = 2,
    Claimed = 3,
    Cancelled = 4,
    Expired = 5,
    Locked = 6,
}

public enum GiftCardShareKind
{
    ProtectedLink = 1,
    DirectInvitation = 2,
}

public enum GiftCardShareContactType
{
    Email = 1,
    Phone = 2,
}

public enum GiftCardShareDirection
{
    Sent = 1,
    Received = 2,
}

public sealed record CreateGiftCardShareRequest(
    decimal Amount,
    string? IdempotencyKey);

public sealed record ClaimGiftCardShareRequest(
    string? ClaimToken,
    string? Pin,
    string? IdempotencyKey);

public sealed record CreateDirectGiftCardShareRequest(
    decimal Amount,
    GiftCardShareContactType ContactType,
    string? RecipientContact,
    string? IdempotencyKey);

public sealed record ClaimDirectGiftCardShareRequest(
    string? ClaimToken,
    string? Password,
    string? IdempotencyKey);

public sealed record GiftCardShareResult(
    Guid Id,
    GiftCardShareKind Kind,
    Guid SourceGiftCardId,
    Guid FundingOrganizationId,
    Guid SenderUserId,
    Guid? ClaimedByUserId,
    Guid? ChildGiftCardId,
    string? SourceGiftCardPublicReference,
    string? ChildGiftCardPublicReference,
    Guid? LedgerTransactionId,
    decimal Amount,
    string Currency,
    GiftCardShareState State,
    int FailedPinAttempts,
    GiftCardShareContactType? RecipientContactType,
    string? MaskedRecipientContact,
    bool? IdentityWasCreatedOnClaim,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ClaimedAtUtc,
    DateTimeOffset? ClosedAtUtc);

public sealed record CreatedGiftCardShareResult(
    GiftCardShareResult Share,
    string ClaimUrl,
    string Pin);

public sealed record ClaimedGiftCardShareResult(
    GiftCardShareResult Share,
    GiftCardResult ChildGiftCard);

public sealed record CreatedDirectGiftCardShareResult(
    GiftCardShareResult Share,
    string MaskedRecipientContact,
    bool DeliveryDispatchedThisRequest);

public sealed record DirectGiftCardShareClaimSessionResult(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc);

public sealed record ClaimedDirectGiftCardShareResult(
    GiftCardShareResult Share,
    Guid OwnerUserId,
    bool IdentityWasCreated,
    string MaskedLoginIdentifier,
    DirectGiftCardShareClaimSessionResult? Session,
    GiftCardResult ChildGiftCard);

public sealed record GiftCardSharePageRequest(
    int Limit,
    string? Cursor,
    GiftCardShareKind? Kind = null,
    GiftCardShareState? State = null,
    GiftCardShareDirection? Direction = null)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;
}

public sealed record GiftCardSharePage(
    IReadOnlyList<GiftCardShareResult> Items,
    int Limit,
    string? NextCursor);

public sealed record GiftCardShareValueResult(
    string Currency,
    decimal Posted,
    decimal Reserved,
    decimal Available);

public interface IProtectedGiftCardShareService
{
    Task<CreatedGiftCardShareResult> CreateAsync(
        Guid sourceGiftCardId,
        CreateGiftCardShareRequest request,
        CancellationToken cancellationToken);

    Task<GiftCardSharePage> GetMineAsync(
        GiftCardSharePageRequest request,
        CancellationToken cancellationToken);

    Task<GiftCardShareResult> CancelAsync(
        Guid shareId,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    Task<ClaimedGiftCardShareResult> ClaimAsync(
        ClaimGiftCardShareRequest request,
        CancellationToken cancellationToken);
}

public interface IDirectGiftCardShareService
{
    Task<CreatedDirectGiftCardShareResult> CreateDirectAsync(
        Guid sourceGiftCardId,
        CreateDirectGiftCardShareRequest request,
        CancellationToken cancellationToken);

    Task<ClaimedDirectGiftCardShareResult> ClaimDirectAsync(
        ClaimDirectGiftCardShareRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Raw claim material crosses only this post-commit delivery boundary. A
/// notifier must never log, audit, or persist the raw token as application data.
/// </summary>
public sealed record DirectGiftCardShareNotification(
    Guid ShareId,
    Guid SenderUserId,
    GiftCardShareContactType ContactType,
    string RecipientContact,
    string MaskedRecipientContact,
    string ClaimToken,
    DateTimeOffset ExpiresAtUtc);

public interface IDirectGiftCardShareNotifier
{
    Task SendAsync(
        DirectGiftCardShareNotification notification,
        CancellationToken cancellationToken);
}

public sealed record DevelopmentDirectGiftCardShareDeliveryResult(
    Guid ShareId,
    GiftCardShareContactType ContactType,
    string MaskedRecipientContact,
    string ClaimUrl,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset CapturedAtUtc);

public interface IDevelopmentDirectGiftCardShareDeliveryQuery
{
    Task<DevelopmentDirectGiftCardShareDeliveryResult?> FindAsync(
        Guid shareId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Authoritative active reservation read boundary. Posted value remains owned
/// by Ledger; consumers must not derive reservations from client state.
/// </summary>
public interface IShareReservationQuery
{
    Task<decimal> GetActiveReservedAmountAsync(
        Guid giftCardId,
        CancellationToken cancellationToken);
}

public interface IShareExpirationProcessor
{
    Task<int> ProcessDueAsync(int maximumItems, CancellationToken cancellationToken);
}

public enum ShareSourceLifecycleClosure
{
    Cancelled = 1,
    Expired = 2,
}

public sealed record CloseSharesForSourceLifecycleRequest(
    Guid SourceGiftCardId,
    ShareSourceLifecycleClosure Closure);

/// <summary>
/// Gift Cards uses this narrow boundary when a source becomes terminal. Active
/// reservations close in the same transaction before remaining value returns.
/// </summary>
public interface IShareLifecycleWriter
{
    Task CloseForSourceLifecycleAsync(
        CloseSharesForSourceLifecycleRequest request,
        CancellationToken cancellationToken);
}

public sealed class SharingOptions
{
    public const string SectionName = "Sharing";

    public int ClaimTokenLifetimeHours { get; set; } = 24;

    public int MaximumFailedPinAttempts { get; set; } = 5;

    public string ClaimBaseUrl { get; set; } = "http://localhost:5180/share/claim";

    public string DirectClaimBaseUrl { get; set; } = "http://localhost:5180/activate/share";

    public bool ExpirationEnabled { get; set; } = true;

    public int ExpirationPollIntervalSeconds { get; set; } = 30;

    public int ExpirationBatchSize { get; set; } = 50;
}
