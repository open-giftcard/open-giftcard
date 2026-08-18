namespace GiftCardPlatform.Modules.GiftCards.Contracts;

public sealed record IssueGiftCardRequest(
    decimal Amount,
    string? Currency,
    DateTimeOffset? ValidFromUtc,
    DateTimeOffset? ExpiresAtUtc,
    bool? IsTransferable,
    bool? IsDivisible,
    string? BusinessReference,
    string? IdempotencyKey);

public sealed record GiftCardResult(
    Guid Id,
    string PublicReference,
    Guid FundingOrganizationId,
    Guid IssuingOrganizationId,
    Guid? OwnerOrganizationId,
    Guid? OwnerUserId,
    string OwnershipState,
    string LifecycleState,
    Guid LedgerAccountId,
    Guid IssuanceLedgerTransactionId,
    decimal FundedAmount,
    string Currency,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ExpiresAtUtc,
    bool IsTransferable,
    bool IsDivisible,
    Guid? SourceGiftCardId,
    Guid RootGiftCardId,
    int Generation,
    Guid? DistributionInvitationId,
    DateTimeOffset? DistributedAtUtc,
    DateTimeOffset? ClaimedAtUtc,
    string BusinessReference,
    string IdempotencyKey,
    Guid IssuedByUserId,
    Guid? IssuedByMembershipId,
    Guid? IssuedByPartnerClientId,
    DateTimeOffset IssuedAtUtc);

public sealed record GiftCardInventoryRequest(int Limit, string? Cursor)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
}

public sealed record GiftCardInventoryPage(
    IReadOnlyList<GiftCardResult> Items,
    int Limit,
    string? NextCursor);

public interface IGiftCardIssuanceService
{
    Task<GiftCardResult> IssueAsync(
        Guid issuingOrganizationId,
        IssueGiftCardRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Narrow issuance boundary for an authenticated e-pin reseller. The
/// implementation derives both the funding and issuing organization from the
/// server-resolved partner principal; callers cannot select another tenant.
/// </summary>
public interface IPartnerGiftCardIssuanceService
{
    Task<GiftCardResult> MintAsync(
        IssueGiftCardRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reuses the Gift Cards module's canonical static issuance validation and
/// normalization when a cross-module workflow must validate all input before
/// starting financial work.
/// </summary>
public interface IGiftCardIssuanceRequestValidator
{
    IssueGiftCardRequest ValidateAndNormalize(IssueGiftCardRequest request);
}

public interface IGiftCardInventoryQuery
{
    Task<GiftCardInventoryPage> GetInventoryAsync(
        Guid organizationId,
        GiftCardInventoryRequest request,
        CancellationToken cancellationToken);
}

public sealed record GiftCardShareSourceResult(
    Guid Id,
    string PublicReference,
    Guid FundingOrganizationId,
    Guid IssuingOrganizationId,
    Guid OwnerUserId,
    string LifecycleState,
    string Currency,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ExpiresAtUtc,
    bool IsTransferable,
    bool IsDivisible,
    Guid RootGiftCardId,
    int Generation);

public sealed record CreateSharedGiftCardChildRequest(
    Guid ShareId,
    Guid SourceGiftCardId,
    Guid ChildGiftCardId,
    Guid RecipientUserId,
    decimal Amount,
    Guid LedgerAccountId,
    Guid LedgerTransactionId,
    DateTimeOffset PostedAtUtc);

/// <summary>
/// Narrow Sharing collaboration boundary. Gift Cards alone validates source
/// eligibility/ownership and creates immutable source/root/generation lineage.
/// </summary>
public interface IGiftCardSharingWriter
{
    Task<IReadOnlyDictionary<Guid, string>> GetVisiblePublicReferencesAsync(
        IReadOnlyCollection<Guid> giftCardIds,
        CancellationToken cancellationToken);

    Task<GiftCardShareSourceResult> GetOwnedSourceAsync(
        Guid sourceGiftCardId,
        CancellationToken cancellationToken);

    Task<GiftCardShareSourceResult> GetClaimSourceAsync(
        Guid sourceGiftCardId,
        CancellationToken cancellationToken);

    Task<GiftCardResult> CreateChildAsync(
        CreateSharedGiftCardChildRequest request,
        CancellationToken cancellationToken);

    Task<GiftCardResult> GetChildAsync(
        Guid childGiftCardId,
        CancellationToken cancellationToken);
}

public sealed record BeginGiftCardDistributionRequest(
    Guid GiftCardId,
    Guid OwnerOrganizationId,
    Guid InvitationId);

public sealed record BeginPartnerEpinDistributionRequest(
    Guid GiftCardId,
    Guid InvitationId);

public sealed record GiftCardSpendableResult(
    Guid Id,
    string PublicReference,
    Guid FundingOrganizationId,
    Guid OwnerUserId,
    string Currency,
    DateTimeOffset ExpiresAtUtc);

public sealed record GiftCardRefundableResult(
    Guid Id,
    string PublicReference,
    Guid FundingOrganizationId,
    Guid OwnerUserId,
    string Currency);

/// <summary>
/// Narrow Payments collaboration boundary. Gift Cards alone decides whether a
/// card may be spent from and who owns it. This is separate from
/// <see cref="IGiftCardSharingWriter"/> because sharing additionally requires
/// transferability and divisibility, which default to false and must not gate
/// payment.
/// </summary>
public interface IGiftCardPaymentWriter
{
    Task<GiftCardSpendableResult> GetOwnedSpendableAsync(
        Guid giftCardId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads a spendable card on behalf of a till acting through a verified
    /// payment credential. Ownership is deliberately not required of the caller:
    /// the credential is what proves the owner authorised this sale, and a POS
    /// client is never the card owner. Requires the exact credential candidate,
    /// exactly as claim reads require the invitation candidate.
    /// </summary>
    Task<GiftCardSpendableResult> GetCredentialSpendableAsync(
        Guid giftCardId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Revalidates a card for refund under Gift Cards' lifecycle authority.
    /// Active and Suspended identity-owned cards are eligible; terminal
    /// Cancelled and Expired cards are not.
    /// </summary>
    Task<GiftCardRefundableResult> GetCredentialRefundableAsync(
        Guid giftCardId,
        CancellationToken cancellationToken);
}

public sealed record IssueAcceptedBulkGiftCardItemRequest(
    Guid FundingOrganizationId,
    Guid IssuingOrganizationId,
    Guid AcceptedByUserId,
    Guid AcceptedByMembershipId,
    IssueGiftCardRequest Issuance);

/// <summary>
/// Narrow trusted-system boundary for a durable bulk item whose organization
/// permissions were checked when its batch was accepted. Implementations must
/// reject every caller except the dedicated bulk-batch system actor.
/// </summary>
public interface IAcceptedBulkGiftCardIssuanceService
{
    Task<GiftCardResult> IssueAsync(
        IssueAcceptedBulkGiftCardItemRequest request,
        CancellationToken cancellationToken);
}

public sealed record CompleteGiftCardClaimRequest(
    Guid GiftCardId,
    Guid InvitationId,
    Guid OwnerUserId);

/// <summary>
/// Narrow cross-module ownership transition boundary. Distribution owns
/// invitations; Gift Cards alone owns card state.
/// </summary>
public interface IGiftCardOwnershipWriter
{
    Task<GiftCardResult> BeginDistributionAsync(
        BeginGiftCardDistributionRequest request,
        CancellationToken cancellationToken);

    Task<GiftCardResult> BeginPartnerEpinDistributionAsync(
        BeginPartnerEpinDistributionRequest request,
        CancellationToken cancellationToken);

    Task<GiftCardResult> CompleteClaimAsync(
        CompleteGiftCardClaimRequest request,
        CancellationToken cancellationToken);
}

public enum GiftCardLifecycleAction
{
    Suspend = 1,
    Reactivate = 2,
    Cancel = 3,
    Expire = 4,
}

public sealed record AdministerGiftCardLifecycleRequest(
    string? Reason,
    string? IdempotencyKey);

public sealed record OwnGiftCardLifecycleRequest(string? IdempotencyKey);

public sealed record GiftCardLifecycleEventResult(
    Guid Id,
    Guid GiftCardId,
    Guid FundingOrganizationId,
    Guid IssuingOrganizationId,
    GiftCardLifecycleAction Action,
    string PreviousState,
    string NewState,
    string ActorType,
    Guid ActorUserId,
    Guid? ActorMembershipId,
    Guid CorrelationId,
    string Reason,
    string IdempotencyKey,
    Guid? LedgerTransactionId,
    decimal? ReturnedAmount,
    string? Currency,
    DateTimeOffset OccurredAtUtc);

public sealed record GiftCardLifecycleOperationResult(
    GiftCardLifecycleEventResult Event);

public sealed record GiftCardLifecycleHistoryResult(
    GiftCardResult GiftCard,
    IReadOnlyList<GiftCardLifecycleEventResult> Events);

public sealed record GiftCardExpirationBatchResult(
    int Examined,
    int Expired,
    int Conflicted);

public interface IGiftCardLifecycleService
{
    Task<GiftCardLifecycleOperationResult> ExecuteForOrganizationAsync(
        Guid organizationId,
        Guid giftCardId,
        GiftCardLifecycleAction action,
        AdministerGiftCardLifecycleRequest request,
        CancellationToken cancellationToken);

    Task<GiftCardLifecycleOperationResult> ExecuteForPlatformAsync(
        Guid giftCardId,
        GiftCardLifecycleAction action,
        AdministerGiftCardLifecycleRequest request,
        CancellationToken cancellationToken);

    Task<GiftCardLifecycleOperationResult> ExecuteForOwnerAsync(
        Guid giftCardId,
        GiftCardLifecycleAction action,
        OwnGiftCardLifecycleRequest request,
        CancellationToken cancellationToken);
}

public interface IGiftCardLifecycleHistoryQuery
{
    Task<GiftCardLifecycleHistoryResult> GetForOrganizationAsync(
        Guid organizationId,
        Guid giftCardId,
        CancellationToken cancellationToken);

    Task<GiftCardLifecycleHistoryResult> GetForPlatformAsync(
        Guid giftCardId,
        CancellationToken cancellationToken);

    Task<GiftCardLifecycleHistoryResult> GetForOwnerAsync(
        Guid giftCardId,
        CancellationToken cancellationToken);
}

public interface IGiftCardExpirationProcessor
{
    Task<GiftCardExpirationBatchResult> ProcessDueAsync(
        int maximumItems,
        CancellationToken cancellationToken);
}

public sealed class GiftCardExpirationOptions
{
    public const string SectionName = "GiftCards:Expiration";

    public bool Enabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 30;

    public int BatchSize { get; set; } = 50;
}
