namespace GiftCardPlatform.Modules.Audit.Contracts;

public enum AuditActorType
{
    PlatformOperator = 1,
    OrganizationMember = 2,
    System = 3,
    IdentityUser = 4,
    PosClient = 5,
    PartnerClient = 6,
}

public enum AuditOutcome
{
    Success = 1,
    Failure = 2,
}

/// <summary>Well-known audit operation codes.</summary>
public static class AuditOperations
{
    public const string OrganizationCreated = "organization.created";
    public const string SubsidiaryCreated = "organization.subsidiary.created";
    public const string MembershipCreated = "organization.membership.created";
    public const string MembershipDisabled = "organization.membership.disabled";
    public const string RoleCreated = "authorization.role.created";
    public const string RolePermissionsGranted = "authorization.role.permissions_granted";
    public const string RoleAssigned = "authorization.role.assigned";
    public const string UserCreated = "identity.user.created";
    public const string UserDisabled = "identity.user.disabled";
    public const string SessionRevoked = "identity.session.revoked";
    public const string RefreshTokenReuseDetected = "identity.refresh_token.reuse_detected";
    public const string PlatformAdministratorBootstrapped =
        "authorization.platform_administrator.bootstrapped";
    public const string InitialOrganizationAdministratorAssigned =
        "authorization.initial_organization_administrator.assigned";
    public const string CorporateCreditAllocated = "corporate_credit.allocated";
    public const string CorporateCreditReversed = "corporate_credit.reversed";
    public const string GiftCardIssued = "gift_card.issued";
    public const string GiftCardDistributed = "gift_card.distributed";
    public const string GiftCardClaimed = "gift_card.claimed";
    public const string GiftCardSuspended = "gift_card.suspended";
    public const string GiftCardReactivated = "gift_card.reactivated";
    public const string GiftCardCancelled = "gift_card.cancelled";
    public const string GiftCardExpired = "gift_card.expired";
    public const string GiftCardBulkDistributed = "gift_card.bulk_distributed";
    public const string GiftCardBulkAccepted = "gift_card.bulk_accepted";
    public const string GiftCardBulkRetried = "gift_card.bulk_retried";
    public const string GiftCardBulkCompleted = "gift_card.bulk_completed";
    public const string GiftCardShareCreated = "gift_card.share.created";
    public const string GiftCardShareClaimed = "gift_card.share.claimed";
    public const string GiftCardShareCancelled = "gift_card.share.cancelled";
    public const string GiftCardShareExpired = "gift_card.share.expired";
    public const string GiftCardShareLocked = "gift_card.share.locked";
    public const string PaymentProvisionCreated = "payment.provision.created";
    public const string PaymentProvisionCancelled = "payment.provision.cancelled";
    public const string PaymentProvisionConfirmed = "payment.provision.confirmed";
    public const string PaymentRefundCreated = "payment.refund.created";

    /// <summary>
    /// An authenticated caller was refused a permission-checked operation. The
    /// signal security monitoring needs to see tenant-boundary probing.
    /// </summary>
    public const string AuthorizationDenied = "authorization.denied";
}

/// <summary>
/// One append-only audit entry. Must not carry passwords, tokens, PINs, or full
/// request payloads — only the small structured metadata needed to explain the
/// operation.
/// </summary>
public sealed record AuditEntry(
    Guid ActorUserId,
    AuditActorType ActorType,
    Guid? OrganizationScopeId,
    string Operation,
    string EntityType,
    string EntityId,
    AuditOutcome Outcome,
    Guid CorrelationId,
    IReadOnlyDictionary<string, string>? Metadata = null,
    Guid? ActorMembershipId = null);

/// <summary>
/// The Audit module's public contract. Other modules record audit entries
/// through this interface and never touch the Audit DbContext or entities
/// (ADR-004, ADR-011).
///
/// The implementation joins the caller's in-progress module transaction, so the
/// audit row commits atomically with the audited change.
/// </summary>
public interface IAuditRecorder
{
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Writes an audit record on its own connection and transaction, committed
    /// immediately and independently of any unit of work in progress.
    ///
    /// This exists for records that must survive a rollback — a refused
    /// operation, above all. A denial audit written through
    /// <see cref="RecordAsync"/> would be rolled back along with the operation
    /// that failed, which is precisely the case that must be recorded (ADR-025).
    ///
    /// Use it only where that survival is the point; ordinary audit records must
    /// stay atomic with the change they describe.
    /// </summary>
    Task RecordIndependentlyAsync(AuditEntry entry, CancellationToken cancellationToken);
}

public sealed record AuditInvestigationRequest(
    int Limit,
    string? Cursor,
    string? Operation,
    AuditOutcome? Outcome,
    Guid? CorrelationId)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
}

public sealed record AuditInvestigationItem(
    Guid Id,
    Guid ActorUserId,
    AuditActorType ActorType,
    Guid? ActorMembershipId,
    Guid? OrganizationScopeId,
    string Operation,
    string EntityType,
    string EntityId,
    AuditOutcome Outcome,
    Guid CorrelationId,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record AuditInvestigationPage(
    IReadOnlyList<AuditInvestigationItem> Items,
    int Limit,
    string? NextCursor);

/// <summary>
/// Tenant-safe, read-only investigation surface. Audit remains authoritative;
/// consumers receive curated DTOs and never the module DbContext.
/// </summary>
public interface IAuditInvestigationQuery
{
    Task<AuditInvestigationPage> GetAsync(
        Guid organizationId,
        AuditInvestigationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Operational bounds for the asynchronous ADR-013 checkpoint pipeline. These
/// values bound exposure and work per pass; they are not financial/domain rules.
/// </summary>
public sealed class AuditCheckpointOptions
{
    public const string SectionName = "Audit:Checkpoints";

    public bool Enabled { get; set; }

    public int PollIntervalSeconds { get; set; } = 300;

    public int BatchSize { get; set; } = 10_000;

    public string? DevelopmentSigningKeyPath { get; set; }

    public string? DevelopmentWitnessDirectory { get; set; }
}

/// <summary>Result returned by an external or Development-only signing adapter.</summary>
public sealed record AuditCheckpointSignature(
    string Algorithm,
    string KeyId,
    byte[] PublicKey,
    byte[] Signature);

/// <summary>
/// Provider-neutral KMS/HSM boundary. The private key must never be returned or
/// stored by this application.
/// </summary>
public interface IAuditCheckpointSigner
{
    Task<AuditCheckpointSignature> SignDigestAsync(
        ReadOnlyMemory<byte> digest,
        CancellationToken cancellationToken);
}

/// <summary>Receipt for one exact signed manifest published to immutable storage.</summary>
public sealed record AuditCheckpointWitnessReceipt(
    string Reference,
    DateTimeOffset PublishedAtUtc);

/// <summary>
/// Provider-neutral WORM witness boundary. Publication must be idempotent for a
/// checkpoint identifier and exact manifest bytes.
/// </summary>
public interface IAuditCheckpointWitness
{
    Task<AuditCheckpointWitnessReceipt> PublishAsync(
        Guid checkpointId,
        ReadOnlyMemory<byte> signedManifest,
        CancellationToken cancellationToken);

    Task<byte[]?> ReadAsync(
        string reference,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every immutable manifest in this deployment's dedicated witness
    /// prefix. Verification compares this external inventory with database
    /// receipts so deleting both a checkpoint and its receipt remains visible.
    /// </summary>
    Task<IReadOnlyCollection<string>> ListReferencesAsync(
        CancellationToken cancellationToken);
}

public sealed record AuditCheckpointPassResult(
    bool ManifestCreated,
    bool SignatureCreated,
    bool WitnessPublished);

public sealed record AuditCheckpointVerificationResult(
    bool IsValid,
    int ManifestCount,
    int SignedCount,
    int WitnessedCount,
    string? FailureCode);

/// <summary>Audit-owned checkpoint pipeline used only by the trusted host worker.</summary>
public interface IAuditCheckpointProcessor
{
    Task<AuditCheckpointPassResult> ProcessNextAsync(
        int maximumRecords,
        CancellationToken cancellationToken);

    Task<AuditCheckpointVerificationResult> VerifyAsync(
        CancellationToken cancellationToken);
}
