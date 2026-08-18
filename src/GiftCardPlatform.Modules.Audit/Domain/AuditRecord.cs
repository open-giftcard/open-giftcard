using GiftCardPlatform.Modules.Audit.Contracts;

namespace GiftCardPlatform.Modules.Audit.Domain;

/// <summary>
/// An append-only audit record (ADR-008). Once written it is never updated or
/// deleted: the application exposes no mutation behaviour, and the runtime
/// database role holds no UPDATE or DELETE privilege on this table (ADR-019).
/// </summary>
internal sealed class AuditRecord
{
    private AuditRecord()
    {
        // Rehydration by EF Core.
        Operation = null!;
        EntityType = null!;
        EntityId = null!;
    }

    private AuditRecord(
        Guid id,
        Guid actorUserId,
        AuditActorType actorType,
        Guid? actorMembershipId,
        Guid? organizationScopeId,
        string operation,
        string entityType,
        string entityId,
        AuditOutcome outcome,
        Guid correlationId,
        DateTimeOffset occurredAtUtc,
        string? metadataJson)
    {
        Id = id;
        ActorUserId = actorUserId;
        ActorType = actorType;
        ActorMembershipId = actorMembershipId;
        OrganizationScopeId = organizationScopeId;
        Operation = operation;
        EntityType = entityType;
        EntityId = entityId;
        Outcome = outcome;
        CorrelationId = correlationId;
        OccurredAtUtc = occurredAtUtc;
        MetadataJson = metadataJson;
    }

    public Guid Id { get; private set; }

    /// <summary>
    /// Database-assigned append order used only by the checkpoint pipeline. It
    /// is never a public identifier or an authorization input.
    /// </summary>
    public long Sequence { get; private set; }

    public Guid ActorUserId { get; private set; }

    public AuditActorType ActorType { get; private set; }

    public Guid? ActorMembershipId { get; private set; }

    public Guid? OrganizationScopeId { get; private set; }

    public string Operation { get; private set; }

    public string EntityType { get; private set; }

    public string EntityId { get; private set; }

    public AuditOutcome Outcome { get; private set; }

    public Guid CorrelationId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>Small structured payload; never credentials or full requests.</summary>
    public string? MetadataJson { get; private set; }

    public static AuditRecord Create(AuditEntry entry, DateTimeOffset occurredAtUtc, string? metadataJson)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.EntityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.EntityId);

        if (entry.ActorType == AuditActorType.OrganizationMember &&
            entry.ActorMembershipId is null)
        {
            throw new ArgumentException(
                "An organization-member audit record requires the active membership.",
                nameof(entry));
        }

        return new AuditRecord(
            Guid.CreateVersion7(),
            entry.ActorUserId,
            entry.ActorType,
            entry.ActorMembershipId,
            entry.OrganizationScopeId,
            entry.Operation,
            entry.EntityType,
            entry.EntityId,
            entry.Outcome,
            entry.CorrelationId,
            occurredAtUtc,
            metadataJson);
    }
}
