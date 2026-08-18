using System.Data;
using System.Globalization;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.Distribution.Domain;
using GiftCardPlatform.Modules.Distribution.Infrastructure;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GiftCardPlatform.Modules.Distribution.Application;

internal sealed class BulkGiftCardBatchProcessor(
    DistributionDbContext dbContext,
    GiftCardDistributionService distributionService,
    IAcceptedBulkGiftCardIssuanceService issuanceService,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IBulkGiftCardBatchProcessor
{
    public async Task<BulkGiftCardBatchProcessingResult> ProcessPendingAsync(
        int maximumItems,
        CancellationToken cancellationToken)
    {
        if (maximumItems is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumItems),
                "A processing pass must contain between 1 and 100 items.");
        }

        EnsureSystemActor();
        var examined = 0;
        var succeeded = 0;
        var failed = 0;
        var conflicted = 0;

        while (examined < maximumItems && !cancellationToken.IsCancellationRequested)
        {
            Guid? itemId = null;
            AppException? itemFailure = null;
            var itemConflict = false;
            try
            {
                await using var transaction = await transactionCoordinator
                    .BeginAsync(IsolationLevel.Serializable, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
                itemId = await LockNextPendingItemIdAsync(
                        transaction.Transaction,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (itemId is null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }

                var item = await dbContext.BulkItems
                    .SingleAsync(candidate => candidate.Id == itemId.Value, cancellationToken)
                    .ConfigureAwait(false);
                var batch = await dbContext.BulkBatches
                    .SingleAsync(candidate => candidate.Id == item.BatchId, cancellationToken)
                    .ConfigureAwait(false);
                batch.StartProcessing();

                var giftCard = await issuanceService
                    .IssueAsync(
                        new IssueAcceptedBulkGiftCardItemRequest(
                            batch.FundingOrganizationId,
                            batch.IssuingOrganizationId,
                            batch.CreatedByUserId,
                            batch.CreatedByMembershipId,
                            item.ToIssuanceRequest()),
                        cancellationToken)
                    .ConfigureAwait(false);
                var prepared = await distributionService
                    .PrepareAcceptedBatchItemAsync(
                        batch.IssuingOrganizationId,
                        batch.FundingOrganizationId,
                        batch.CreatedByUserId,
                        batch.CreatedByMembershipId,
                        item.ToDistributionRequest(giftCard.Id),
                        cancellationToken)
                    .ConfigureAwait(false);
                item.SetSuccessSources(giftCard, prepared.Result);
                batch.RecordSucceeded(item, timeProvider.GetUtcNow());
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (batch.State == BulkGiftCardBatchState.Completed)
                {
                    await RecordCompletionAuditAsync(batch, cancellationToken)
                        .ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                succeeded++;
            }
            catch (Exception exception) when (
                itemId is not null && IsTransientConflict(exception))
            {
                itemConflict = true;
            }
            catch (AppException exception) when (itemId is not null)
            {
                itemFailure = exception;
            }

            if (itemId is null)
            {
                break;
            }

            examined++;
            if (itemConflict)
            {
                conflicted++;
                break;
            }

            if (itemFailure is null)
            {
                continue;
            }

            if (await SettleFailureInFreshScopeAsync(
                    itemId.Value,
                    itemFailure,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                failed++;
            }
            else
            {
                conflicted++;
            }

            // The failed attempt rolled back DbContexts in this scope. Do not
            // reuse their tracked state for another item; the host starts the
            // next item in a fresh scope.
            break;
        }

        return new BulkGiftCardBatchProcessingResult(
            examined,
            succeeded,
            failed,
            conflicted);
    }

    private async Task<bool> SettleFailureInFreshScopeAsync(
        Guid itemId,
        AppException failure,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MutableExecutionContext>();
        context.SetCorrelationId(Guid.CreateVersion7());
        context.SetSystem(SystemActorIds.BulkGiftCardBatch, []);
        var settler = scope.ServiceProvider
            .GetRequiredService<BulkGiftCardBatchFailureSettler>();
        return await settler.SettleAsync(itemId, failure, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RecordCompletionAuditAsync(
        BulkGiftCardBatch batch,
        CancellationToken cancellationToken) =>
        await auditRecorder.RecordAsync(
            new AuditEntry(
                ActorUserId: SystemActorIds.BulkGiftCardBatch,
                ActorType: AuditActorType.System,
                OrganizationScopeId: batch.IssuingOrganizationId,
                Operation: AuditOperations.GiftCardBulkCompleted,
                EntityType: nameof(BulkGiftCardBatch),
                EntityId: batch.Id.ToString(),
                Outcome: AuditOutcome.Success,
                CorrelationId: executionContext.CorrelationId,
                Metadata: new Dictionary<string, string>
                {
                    ["fundingOrganizationId"] = batch.FundingOrganizationId.ToString(),
                    ["issuingOrganizationId"] = batch.IssuingOrganizationId.ToString(),
                    ["totalItems"] = batch.TotalItems.ToString(CultureInfo.InvariantCulture),
                    ["succeededItems"] =
                        batch.SucceededItems.ToString(CultureInfo.InvariantCulture),
                    ["failedItems"] =
                        batch.FailedItems.ToString(CultureInfo.InvariantCulture),
                }),
            cancellationToken).ConfigureAwait(false);

    private static async Task<Guid?> LockNextPendingItemIdAsync(
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select id
            from distribution.bulk_items
            where state = 'Pending'
            order by batch_id, position
            limit 1
            for update skip locked
            """,
            transaction.Connection,
            transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is Guid id ? id : null;
    }

    private void EnsureSystemActor()
    {
        if (!executionContext.IsSystem ||
            executionContext.UserId != SystemActorIds.BulkGiftCardBatch)
        {
            throw new ForbiddenException(
                "bulk.processor.system_required",
                "Only the bulk-batch system processor may process pending items.");
        }
    }

    private static bool IsTransientConflict(Exception exception)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is AppException appException &&
                appException.Code.EndsWith(
                    ".concurrent_conflict",
                    StringComparison.Ordinal))
            {
                return true;
            }

            if (current is DbUpdateConcurrencyException ||
                current is PostgresException
                {
                    SqlState: "40001" or "40P01",
                })
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class BulkGiftCardBatchFailureSettler(
    DistributionDbContext dbContext,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider)
{
    public async Task<bool> SettleAsync(
        Guid itemId,
        AppException failure,
        CancellationToken cancellationToken)
    {
        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        if (!await LockPendingItemAsync(itemId, transaction.Transaction, cancellationToken)
                .ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var item = await dbContext.BulkItems
            .SingleAsync(candidate => candidate.Id == itemId, cancellationToken)
            .ConfigureAwait(false);
        var batch = await dbContext.BulkBatches
            .SingleAsync(candidate => candidate.Id == item.BatchId, cancellationToken)
            .ConfigureAwait(false);
        batch.RecordFailed(item, failure.Code, failure.Message, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (batch.State == BulkGiftCardBatchState.Completed)
        {
            await auditRecorder.RecordAsync(
                new AuditEntry(
                    ActorUserId: SystemActorIds.BulkGiftCardBatch,
                    ActorType: AuditActorType.System,
                    OrganizationScopeId: batch.IssuingOrganizationId,
                    Operation: AuditOperations.GiftCardBulkCompleted,
                    EntityType: nameof(BulkGiftCardBatch),
                    EntityId: batch.Id.ToString(),
                    Outcome: AuditOutcome.Success,
                    CorrelationId: executionContext.CorrelationId,
                    Metadata: new Dictionary<string, string>
                    {
                        ["fundingOrganizationId"] = batch.FundingOrganizationId.ToString(),
                        ["issuingOrganizationId"] = batch.IssuingOrganizationId.ToString(),
                        ["totalItems"] = batch.TotalItems.ToString(CultureInfo.InvariantCulture),
                        ["succeededItems"] =
                            batch.SucceededItems.ToString(CultureInfo.InvariantCulture),
                        ["failedItems"] =
                            batch.FailedItems.ToString(CultureInfo.InvariantCulture),
                    }),
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> LockPendingItemAsync(
        Guid itemId,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select id
            from distribution.bulk_items
            where id = @item_id and state = 'Pending'
            for update
            """,
            transaction.Connection,
            transaction);
        command.Parameters.AddWithValue("item_id", itemId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is Guid;
    }
}
