using System.Data;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.Distribution.Domain;
using GiftCardPlatform.Modules.Distribution.Infrastructure;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GiftCardPlatform.Modules.Distribution.Application;

internal sealed class BulkGiftCardBatchService(
    DistributionDbContext dbContext,
    GiftCardDistributionService distributionService,
    IGiftCardIssuanceService issuanceService,
    IGiftCardIssuanceRequestValidator issuanceValidator,
    IOrganizationPermissionAuthorizer organizationAuthorizer,
    IAuditRecorder auditRecorder,
    INotificationChannelAvailability notificationChannels,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IBulkGiftCardBatchService
{
    private const string UniqueViolation = "23505";
    private const string SerializationFailure = "40001";

    public async Task<BulkGiftCardBatchResult> CreateAsync(
        Guid organizationId,
        CreateBulkGiftCardBatchRequest request,
        CancellationToken cancellationToken)
    {
        EnsureOrganization(organizationId);
        var intent = BulkGiftCardBatchIntent.Create(request, issuanceValidator);
        await RequireWritePermissionsAsync(organizationId, cancellationToken)
            .ConfigureAwait(false);
        var actor = GetOrganizationActor();
        BulkGiftCardBatch batch;

        try
        {
            await using var transaction = await transactionCoordinator
                .BeginAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            var existing = await FindByIdempotencyAsync(
                    actor.FundingOrganizationId,
                    intent.IdempotencyKey,
                    includeItems: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                EnsureMatching(existing, actor.FundingOrganizationId, organizationId, intent);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return BulkGiftCardBatchMapping.ToResult(existing);
            }

            RequireNotificationChannels(intent);

            batch = BulkGiftCardBatch.CreateSynchronous(
                Guid.CreateVersion7(),
                actor.FundingOrganizationId,
                organizationId,
                intent,
                actor.UserId,
                actor.MembershipId,
                timeProvider.GetUtcNow());
            dbContext.BulkBatches.Add(batch);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            foreach (var item in batch.Items.OrderBy(candidate => candidate.Position))
            {
                try
                {
                    var giftCard = await issuanceService
                        .IssueAsync(
                            organizationId,
                            item.ToIssuanceRequest(),
                            cancellationToken)
                        .ConfigureAwait(false);
                    var prepared = await distributionService
                        .PrepareAsync(
                            organizationId,
                            item.ToDistributionRequest(giftCard.Id),
                            cancellationToken)
                        .ConfigureAwait(false);
                    item.SetSuccessSources(giftCard, prepared.Result);
                    batch.RecordSucceeded(item, timeProvider.GetUtcNow());
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ValidationFailedException exception)
                {
                    throw WrapValidation(item, exception);
                }
                catch (ConflictException exception)
                {
                    throw WrapConflict(item, exception);
                }
            }

            await RecordBatchAuditAsync(
                    batch,
                    AuditOperations.GiftCardBulkDistributed,
                    AuditActorType.OrganizationMember,
                    actor.UserId,
                    actor.MembershipId,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsDatabaseConcurrencyConflict(exception))
        {
            throw ConcurrentConflict();
        }

        return BulkGiftCardBatchMapping.ToResult(batch);
    }

    public async Task<BulkGiftCardBatchSummary> AcceptAsync(
        Guid organizationId,
        CreateBulkGiftCardBatchRequest request,
        CancellationToken cancellationToken)
    {
        EnsureOrganization(organizationId);
        var intent = BulkGiftCardBatchIntent.CreateAsync(request, issuanceValidator);
        await RequireWritePermissionsAsync(organizationId, cancellationToken)
            .ConfigureAwait(false);
        var actor = GetOrganizationActor();

        try
        {
            await using var transaction = await transactionCoordinator
                .BeginAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            var existing = await FindByIdempotencyAsync(
                    actor.FundingOrganizationId,
                    intent.IdempotencyKey,
                    includeItems: false,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                EnsureMatching(existing, actor.FundingOrganizationId, organizationId, intent);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return BulkGiftCardBatchMapping.ToSummary(existing);
            }

            RequireNotificationChannels(intent);

            var batch = BulkGiftCardBatch.CreatePending(
                Guid.CreateVersion7(),
                actor.FundingOrganizationId,
                organizationId,
                intent,
                actor.UserId,
                actor.MembershipId,
                timeProvider.GetUtcNow());
            dbContext.BulkBatches.Add(batch);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await RecordBatchAuditAsync(
                    batch,
                    AuditOperations.GiftCardBulkAccepted,
                    AuditActorType.OrganizationMember,
                    actor.UserId,
                    actor.MembershipId,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return BulkGiftCardBatchMapping.ToSummary(batch);
        }
        catch (Exception exception) when (IsDatabaseConcurrencyConflict(exception))
        {
            throw ConcurrentConflict();
        }
    }

    public async Task<BulkGiftCardBatchResult> GetAsync(
        Guid organizationId,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        EnsureBatchRequest(organizationId, batchId);
        await RequireViewPermissionAsync(organizationId, cancellationToken)
            .ConfigureAwait(false);

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var batch = await dbContext.BulkBatches
            .AsNoTracking()
            .Include(candidate => candidate.Items)
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.Id == batchId &&
                    candidate.IssuingOrganizationId == organizationId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw BatchNotFound();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return BulkGiftCardBatchMapping.ToResult(batch);
    }

    private void RequireNotificationChannels(BulkGiftCardBatchIntent intent)
    {
        foreach (var contactType in intent.Items
                     .Select(item => item.ContactType)
                     .Distinct())
        {
            notificationChannels.RequireAvailable(
                contactType == RecipientContactType.Email
                    ? NotificationChannel.Email
                    : NotificationChannel.Sms);
        }
    }

    public async Task<BulkGiftCardBatchPage> GetPageAsync(
        Guid organizationId,
        Guid batchId,
        BulkGiftCardBatchPageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureBatchRequest(organizationId, batchId);
        if (request.Limit is < 1 or > BulkGiftCardBatchPageRequest.MaxLimit)
        {
            throw new ValidationFailedException(
                "bulk.page.limit.invalid",
                $"Limit must be between 1 and {BulkGiftCardBatchPageRequest.MaxLimit}.");
        }

        var afterPosition = BulkGiftCardBatchCursorCodec.Decode(request.Cursor);
        await RequireViewPermissionAsync(organizationId, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var batch = await dbContext.BulkBatches
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.Id == batchId &&
                    candidate.IssuingOrganizationId == organizationId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw BatchNotFound();
        var items = await dbContext.BulkItems
            .AsNoTracking()
            .Where(item =>
                item.BatchId == batchId &&
                (afterPosition == null || item.Position > afterPosition))
            .OrderBy(item => item.Position)
            .Take(request.Limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasMore = items.Count > request.Limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        var nextCursor = hasMore && items.Count > 0
            ? BulkGiftCardBatchCursorCodec.Encode(items[^1].Position)
            : null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return BulkGiftCardBatchMapping.ToPage(batch, items, request.Limit, nextCursor);
    }

    public async Task<BulkGiftCardBatchSummary> RetryAsync(
        Guid organizationId,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        EnsureBatchRequest(organizationId, batchId);
        await RequireWritePermissionsAsync(organizationId, cancellationToken)
            .ConfigureAwait(false);
        var actor = GetOrganizationActor();

        try
        {
            await using var transaction = await transactionCoordinator
                .BeginAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            var source = await dbContext.BulkBatches
                .Include(candidate => candidate.Items)
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.Id == batchId &&
                        candidate.IssuingOrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw BatchNotFound();
            var existingRetry = await dbContext.BulkBatches
                .SingleOrDefaultAsync(
                    candidate => candidate.RetryOfBatchId == source.Id,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existingRetry is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return BulkGiftCardBatchMapping.ToSummary(existingRetry);
            }

            if (source.State != BulkGiftCardBatchState.Completed)
            {
                throw new ConflictException(
                    "bulk.retry.not_completed",
                    "Only a completed batch can be retried.");
            }

            var failedIntents = source.Items
                .Where(item => item.State == BulkGiftCardBatchItemState.Failed)
                .OrderBy(item => item.Position)
                .Select(item => item.ToIntent())
                .ToArray();
            var retryIntent = BulkGiftCardBatchIntent.CreateRetry(source, failedIntents);
            RequireNotificationChannels(retryIntent);
            var retry = BulkGiftCardBatch.CreatePending(
                Guid.CreateVersion7(),
                actor.FundingOrganizationId,
                organizationId,
                retryIntent,
                actor.UserId,
                actor.MembershipId,
                timeProvider.GetUtcNow(),
                source.Id);
            dbContext.BulkBatches.Add(retry);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await RecordBatchAuditAsync(
                    retry,
                    AuditOperations.GiftCardBulkRetried,
                    AuditActorType.OrganizationMember,
                    actor.UserId,
                    actor.MembershipId,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return BulkGiftCardBatchMapping.ToSummary(retry);
        }
        catch (Exception exception) when (IsDatabaseConcurrencyConflict(exception))
        {
            throw ConcurrentConflict();
        }
    }

    private async Task<BulkGiftCardBatch?> FindByIdempotencyAsync(
        Guid fundingOrganizationId,
        string idempotencyKey,
        bool includeItems,
        CancellationToken cancellationToken)
    {
        IQueryable<BulkGiftCardBatch> query = dbContext.BulkBatches;
        if (includeItems)
        {
            query = query.Include(candidate => candidate.Items);
        }

        return await query.SingleOrDefaultAsync(
            candidate =>
                candidate.FundingOrganizationId == fundingOrganizationId &&
                candidate.IdempotencyKey == idempotencyKey,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireWritePermissionsAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await organizationAuthorizer
            .RequirePermissionAsync(
                organizationId,
                OrganizationPermissions.GiftCardsIssue,
                cancellationToken)
            .ConfigureAwait(false);
        await organizationAuthorizer
            .RequirePermissionAsync(
                organizationId,
                OrganizationPermissions.GiftCardsDistribute,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task RequireViewPermissionAsync(
        Guid organizationId,
        CancellationToken cancellationToken) =>
        organizationAuthorizer.RequirePermissionAsync(
            organizationId,
            OrganizationPermissions.GiftCardsView,
            cancellationToken);

    private (Guid FundingOrganizationId, Guid UserId, Guid MembershipId)
        GetOrganizationActor() =>
        (
            executionContext.TenantRootOrganizationId
                ?? throw new ForbiddenException(
                    "auth.unauthenticated",
                    "A verified organization membership is required."),
            executionContext.UserId!.Value,
            executionContext.ActiveMembershipId!.Value
        );

    private Task RecordBatchAuditAsync(
        BulkGiftCardBatch batch,
        string operation,
        AuditActorType actorType,
        Guid actorId,
        Guid? actorMembershipId,
        CancellationToken cancellationToken) =>
        auditRecorder.RecordAsync(
            new AuditEntry(
                ActorUserId: actorId,
                ActorType: actorType,
                OrganizationScopeId: batch.IssuingOrganizationId,
                Operation: operation,
                EntityType: nameof(BulkGiftCardBatch),
                EntityId: batch.Id.ToString(),
                Outcome: AuditOutcome.Success,
                CorrelationId: executionContext.CorrelationId,
                Metadata: new Dictionary<string, string>
                {
                    ["fundingOrganizationId"] = batch.FundingOrganizationId.ToString(),
                    ["issuingOrganizationId"] = batch.IssuingOrganizationId.ToString(),
                    ["batchReference"] = batch.BatchReference,
                    ["totalItems"] = batch.TotalItems.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["succeededItems"] = batch.SucceededItems.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["failedItems"] = batch.FailedItems.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                },
                ActorMembershipId: actorMembershipId),
            cancellationToken);

    private static void EnsureMatching(
        BulkGiftCardBatch existing,
        Guid fundingOrganizationId,
        Guid organizationId,
        BulkGiftCardBatchIntent intent)
    {
        if (!existing.Matches(fundingOrganizationId, organizationId, intent))
        {
            throw new ConflictException(
                "bulk.idempotency_key.reused",
                "The idempotency key was already used for different batch intent.");
        }
    }

    private static void EnsureBatchRequest(Guid organizationId, Guid batchId)
    {
        EnsureOrganization(organizationId);
        if (batchId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "bulk.batch.required",
                "A batch identifier is required.");
        }
    }

    private static void EnsureOrganization(Guid organizationId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "bulk.organization.required",
                "An issuing organization is required.");
        }
    }

    private static ValidationFailedException WrapValidation(
        BulkGiftCardBatchItem item,
        ValidationFailedException exception) =>
        new(
            "bulk.item.invalid",
            $"Batch item at index {item.Position - 1} is invalid.",
            ItemErrorExtensions(item, exception.Code));

    private static ConflictException WrapConflict(
        BulkGiftCardBatchItem item,
        ConflictException exception) =>
        new(
            "bulk.item.conflict",
            $"Batch item at index {item.Position - 1} could not be completed.",
            ItemErrorExtensions(item, exception.Code));

    private static Dictionary<string, object?> ItemErrorExtensions(
        BulkGiftCardBatchItem item,
        string causeCode) =>
        new()
        {
            ["itemIndex"] = item.Position - 1,
            ["itemReference"] = item.ItemReference,
            ["causeCode"] = causeCode,
        };

    private static NotFoundException BatchNotFound() =>
        new(
            "bulk.batch.not_found",
            "The gift-card batch was not found.");

    private static ConflictException ConcurrentConflict() =>
        new(
            "bulk.concurrent_conflict",
            "A concurrent batch or financial operation conflicted. Retry safely with the same idempotency key.");

    private static bool IsDatabaseConcurrencyConflict(Exception exception)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is PostgresException
                {
                    SqlState: UniqueViolation or SerializationFailure,
                })
            {
                return true;
            }
        }

        return false;
    }
}
