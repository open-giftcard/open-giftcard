using System.Text.Json;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Audit.Domain;
using GiftCardPlatform.Modules.Audit.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Audit.Application;

/// <summary>
/// Writes audit records inside the caller's in-progress module transaction, so
/// the audit row and the audited business change commit or roll back together
/// (ADR-011). This is a synchronous write, not a fire-and-forget event: if it
/// throws, the caller's transaction is rolled back.
/// </summary>
internal sealed class AuditRecorder(
    AuditDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    IDatabaseConnectionFactory connectionFactory,
    ISessionContextWriter sessionContextWriter,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IAuditRecorder
{
    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var transaction = transactionCoordinator.Current
            ?? throw new InvalidOperationException(
                "Audit records must be written inside a module transaction so they commit atomically " +
                "with the audited operation. Begin a transaction through ITransactionCoordinator first.");

        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        await AuditCheckpointLock.AcquireWriterAsync(dbContext, cancellationToken)
            .ConfigureAwait(false);

        await InsertWithoutReturningAsync(dbContext, ToRecord(entry), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RecordIndependentlyAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // A separate physical connection on purpose: the scoped connection may be
        // inside a transaction that is about to roll back, which would discard
        // exactly the record we are trying to keep.
        await using var connection = await connectionFactory
            .CreateOpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await sessionContextWriter
            .WriteAsync(connection, transaction, executionContext, cancellationToken)
            .ConfigureAwait(false);

        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(connection)
            .Options;

        await using var context = new AuditDbContext(options, executionContext);
        await context.Database
            .UseTransactionAsync(transaction, cancellationToken)
            .ConfigureAwait(false);

        await AuditCheckpointLock.AcquireWriterAsync(context, cancellationToken)
            .ConfigureAwait(false);

        await InsertWithoutReturningAsync(context, ToRecord(entry), cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private AuditRecord ToRecord(AuditEntry entry)
    {
        var metadataJson = entry.Metadata is { Count: > 0 }
            ? JsonSerializer.Serialize(entry.Metadata)
            : null;

        return AuditRecord.Create(entry, timeProvider.GetUtcNow(), metadataJson);
    }

    private static Task<int> InsertWithoutReturningAsync(
        AuditDbContext context,
        AuditRecord record,
        CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            insert into audit.audit_records (
                id,
                actor_user_id,
                actor_type,
                actor_membership_id,
                organization_scope_id,
                operation,
                entity_type,
                entity_id,
                outcome,
                correlation_id,
                occurred_at_utc,
                metadata)
            values (
                {record.Id},
                {record.ActorUserId},
                {record.ActorType.ToString()},
                {record.ActorMembershipId},
                {record.OrganizationScopeId},
                {record.Operation},
                {record.EntityType},
                {record.EntityId},
                {record.Outcome.ToString()},
                {record.CorrelationId},
                {record.OccurredAtUtc},
                cast({record.MetadataJson} as jsonb))
            """,
            cancellationToken);
}
