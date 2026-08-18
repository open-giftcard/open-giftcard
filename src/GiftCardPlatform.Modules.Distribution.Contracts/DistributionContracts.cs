namespace GiftCardPlatform.Modules.Distribution.Contracts;

using GiftCardPlatform.Modules.GiftCards.Contracts;

public enum RecipientContactType
{
    Email = 1,
    Phone = 2,
}

public enum DistributionInvitationKind
{
    Directed = 1,
    OrphanPin = 2,
}

public sealed record DistributeGiftCardRequest(
    Guid GiftCardId,
    RecipientContactType ContactType,
    string? RecipientContact,
    string? BusinessReference,
    string? IdempotencyKey);

public sealed record DistributionInvitationResult(
    Guid Id,
    Guid FundingOrganizationId,
    Guid IssuingOrganizationId,
    Guid GiftCardId,
    DistributionInvitationKind Kind,
    RecipientContactType? ContactType,
    string? MaskedRecipientContact,
    string State,
    DateTimeOffset ClaimExpiresAtUtc,
    int FailedClaimAttempts,
    string BusinessReference,
    string IdempotencyKey,
    Guid DistributedByUserId,
    Guid? DistributedByMembershipId,
    Guid? DistributedByPartnerClientId,
    DateTimeOffset DistributedAtUtc,
    Guid? ClaimedByUserId,
    DateTimeOffset? ClaimedAtUtc);

public interface IGiftCardDistributionService
{
    Task<DistributionInvitationResult> DistributeAsync(
        Guid organizationId,
        DistributeGiftCardRequest request,
        CancellationToken cancellationToken);
}

public sealed record ClaimGiftCardRequest(
    string? ClaimToken,
    string? Pin,
    RecipientContactType? ContactType,
    string? RecipientContact,
    string? Password,
    string? IdempotencyKey);

public sealed record GiftCardClaimSessionResult(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc);

public sealed record GiftCardClaimResult(
    Guid InvitationId,
    Guid OwnerUserId,
    bool IdentityWasCreated,
    string MaskedLoginIdentifier,
    GiftCardClaimSessionResult? Session,
    GiftCardResult GiftCard,
    DateTimeOffset ClaimedAtUtc);

public interface IGiftCardClaimService
{
    Task<GiftCardClaimResult> ClaimAsync(
        ClaimGiftCardRequest request,
        CancellationToken cancellationToken);
}

public sealed record MintPartnerEpinRequest(IssueGiftCardRequest Issuance);

public sealed record MintedPartnerEpinResult(
    GiftCardResult GiftCard,
    Guid InvitationId,
    string ClaimUrl,
    string Pin,
    DateTimeOffset ClaimExpiresAtUtc);

/// <summary>
/// Atomically mints a partner-funded card and creates its buyer-facing orphan
/// claim credential. An idempotent retry returns the same claim material only
/// to the same live partner API client.
/// </summary>
public interface IPartnerEpinService
{
    Task<MintedPartnerEpinResult> MintAsync(
        MintPartnerEpinRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Raw claim material is passed only to the delivery boundary. Implementations
/// must never log, audit, or persist <see cref="ClaimToken"/>.
/// </summary>
public sealed record GiftCardClaimNotification(
    Guid InvitationId,
    Guid IssuingOrganizationId,
    RecipientContactType ContactType,
    string RecipientContact,
    string ClaimToken,
    DateTimeOffset ExpiresAtUtc);

public interface IGiftCardClaimNotifier
{
    Task SendAsync(
        GiftCardClaimNotification notification,
        CancellationToken cancellationToken);
}

public sealed record DevelopmentClaimDeliveryResult(
    Guid InvitationId,
    RecipientContactType ContactType,
    string MaskedRecipientContact,
    string ClaimUrl,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset CapturedAtUtc);

/// <summary>
/// Development-only inspection surface used by the demo instead of a real
/// email or SMS provider.
/// </summary>
public interface IDevelopmentClaimDeliveryQuery
{
    Task<DevelopmentClaimDeliveryResult?> FindAsync(
        Guid organizationId,
        Guid invitationId,
        CancellationToken cancellationToken);
}

public enum DistributionLifecycleClosure
{
    Cancelled = 1,
    Expired = 2,
}

public sealed record CloseDistributionForLifecycleRequest(
    Guid InvitationId,
    Guid GiftCardId,
    DistributionLifecycleClosure Closure);

/// <summary>
/// Narrow cross-module boundary used when a card becomes terminal.
/// Distribution retains its immutable invitation while closing any still-live
/// activation path inside the caller's transaction.
/// </summary>
public interface IDistributionLifecycleWriter
{
    Task CloseForCardLifecycleAsync(
        CloseDistributionForLifecycleRequest request,
        CancellationToken cancellationToken);
}

public sealed record BulkGiftCardBatchItemRequest(
    string? ItemReference,
    decimal Amount,
    string? Currency,
    DateTimeOffset? ValidFromUtc,
    DateTimeOffset? ExpiresAtUtc,
    bool? IsTransferable,
    bool? IsDivisible,
    RecipientContactType ContactType,
    string? RecipientContact);

public sealed record CreateBulkGiftCardBatchRequest(
    string? BatchReference,
    string? IdempotencyKey,
    IReadOnlyList<BulkGiftCardBatchItemRequest>? Items);

public sealed record BulkGiftCardCurrencyTotal(
    string Currency,
    decimal Amount);

public sealed record BulkGiftCardBatchItemResult(
    int Position,
    string ItemReference,
    string Status,
    RecipientContactType ContactType,
    string MaskedRecipientContact,
    decimal Amount,
    string Currency,
    Guid? GiftCardId,
    string? GiftCardPublicReference,
    Guid? InvitationId,
    string? GiftCardState,
    string? InvitationState,
    DateTimeOffset? DistributedAtUtc,
    string? FailureCode,
    string? FailureMessage,
    DateTimeOffset? SettledAtUtc);

public sealed record BulkGiftCardBatchResult(
    Guid Id,
    Guid FundingOrganizationId,
    Guid IssuingOrganizationId,
    string BatchReference,
    string IdempotencyKey,
    string Status,
    int TotalItems,
    int SucceededItems,
    int FailedItems,
    IReadOnlyList<BulkGiftCardCurrencyTotal> CurrencyTotals,
    Guid CreatedByUserId,
    Guid CreatedByMembershipId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    Guid? RetryOfBatchId,
    IReadOnlyList<BulkGiftCardBatchItemResult> Items);

public sealed record BulkGiftCardBatchPageRequest(int Limit, string? Cursor)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
}

public sealed record BulkGiftCardBatchPage(
    Guid Id,
    Guid FundingOrganizationId,
    Guid IssuingOrganizationId,
    string BatchReference,
    string Status,
    int TotalItems,
    int SucceededItems,
    int FailedItems,
    Guid CreatedByUserId,
    Guid CreatedByMembershipId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    Guid? RetryOfBatchId,
    int Limit,
    string? NextCursor,
    IReadOnlyList<BulkGiftCardBatchItemResult> Items);

public sealed record BulkGiftCardBatchSummary(
    Guid Id,
    Guid FundingOrganizationId,
    Guid IssuingOrganizationId,
    string BatchReference,
    string Status,
    int TotalItems,
    int SucceededItems,
    int FailedItems,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    Guid? RetryOfBatchId);

public sealed record BulkGiftCardBatchProcessingResult(
    int Examined,
    int Succeeded,
    int Failed,
    int Conflicted);

public interface IBulkGiftCardBatchService
{
    Task<BulkGiftCardBatchResult> CreateAsync(
        Guid organizationId,
        CreateBulkGiftCardBatchRequest request,
        CancellationToken cancellationToken);

    Task<BulkGiftCardBatchResult> GetAsync(
        Guid organizationId,
        Guid batchId,
        CancellationToken cancellationToken);

    Task<BulkGiftCardBatchSummary> AcceptAsync(
        Guid organizationId,
        CreateBulkGiftCardBatchRequest request,
        CancellationToken cancellationToken);

    Task<BulkGiftCardBatchPage> GetPageAsync(
        Guid organizationId,
        Guid batchId,
        BulkGiftCardBatchPageRequest request,
        CancellationToken cancellationToken);

    Task<BulkGiftCardBatchSummary> RetryAsync(
        Guid organizationId,
        Guid batchId,
        CancellationToken cancellationToken);
}

public interface IBulkGiftCardBatchProcessor
{
    Task<BulkGiftCardBatchProcessingResult> ProcessPendingAsync(
        int maximumItems,
        CancellationToken cancellationToken);
}

public sealed class DistributionOptions
{
    public const string SectionName = "Distribution";

    public int ClaimTokenLifetimeHours { get; set; } = 24;

    public int MaximumFailedClaimAttempts { get; set; } = 5;

    public string ClaimBaseUrl { get; set; } = "http://localhost:5050/#claim";
}

public sealed class BulkGiftCardBatchOptions
{
    public const string SectionName = "Distribution:BulkBatches";

    public bool Enabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 5;

    public int ChunkSize { get; set; } = 25;
}
