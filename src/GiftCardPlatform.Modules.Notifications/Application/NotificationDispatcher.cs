using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Notifications.Contracts;
using GiftCardPlatform.Modules.Notifications.Domain;
using GiftCardPlatform.Modules.Notifications.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Modules.Notifications.Application;

internal sealed class NotificationDispatcher(
    NotificationsDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    INotificationPayloadProtector protector,
    IEnumerable<INotificationChannelSender> senders,
    IExecutionContext executionContext,
    IOptions<NotificationOptions> options,
    TimeProvider timeProvider) : INotificationDispatcher
{
    private readonly NotificationOptions settings = options.Value;

    public async Task<NotificationDispatchBatchResult> DispatchDueAsync(
        int maximumItems,
        CancellationToken cancellationToken)
    {
        // Delivery is trusted-system work. It reads every tenant's queue, so it
        // must not be reachable from an ordinary caller's context.
        if (!executionContext.IsSystem)
        {
            throw new ForbiddenException(
                "notification.dispatch.system.required",
                "A trusted system context is required to dispatch notifications.");
        }

        if (maximumItems <= 0)
        {
            return new NotificationDispatchBatchResult(0, 0, 0, 0);
        }

        var due = await LeaseDueAsync(maximumItems, cancellationToken).ConfigureAwait(false);
        var delivered = 0;
        var retrying = 0;
        var deadLettered = 0;

        foreach (var id in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await AttemptAsync(id, cancellationToken).ConfigureAwait(false);
            switch (outcome)
            {
                case AttemptOutcome.Delivered:
                    delivered++;
                    break;
                case AttemptOutcome.Retrying:
                    retrying++;
                    break;
                case AttemptOutcome.DeadLettered:
                    deadLettered++;
                    break;
                default:
                    break;
            }
        }

        return new NotificationDispatchBatchResult(due.Count, delivered, retrying, deadLettered);
    }

    /// <summary>
    /// Selects due identifiers in their own short transaction. Each message is
    /// then handled independently, so one provider timeout cannot hold a
    /// transaction open across the whole batch.
    /// </summary>
    private async Task<List<Guid>> LeaseDueAsync(
        int maximumItems,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var due = await dbContext.Messages
            .AsNoTracking()
            .Where(message =>
                message.State == OutboxMessageState.Pending &&
                message.NextAttemptAtUtc <= now)
            .OrderBy(message => message.NextAttemptAtUtc)
            .ThenBy(message => message.Id)
            .Select(message => message.Id)
            .Take(Math.Min(maximumItems, settings.DispatchBatchSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return due;
    }

    private async Task<AttemptOutcome> AttemptAsync(Guid id, CancellationToken cancellationToken)
    {
        // Take the row lock, decrypt, and decide, all before touching the
        // provider. A second dispatcher instance blocks here rather than sending
        // the same activation link twice.
        NotificationMessage? message;
        await using (var lease = await transactionCoordinator
            .BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            await lease.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"select pg_advisory_xact_lock(hashtextextended({$"notification|{id:D}"}, 0))",
                cancellationToken).ConfigureAwait(false);

            var row = await dbContext.Messages
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
                .ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();

            if (row is null || !row.IsDue(now))
            {
                await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
                return AttemptOutcome.Skipped;
            }

            // A link that can no longer be claimed is not worth sending.
            if (row.HasLapsed(now))
            {
                row.RecordFailure("notification.credential.lapsed", retryable: false, settings, now);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
                return AttemptOutcome.DeadLettered;
            }

            var recipient = protector.TryUnprotect(row.ProtectedRecipient);
            var body = row.ProtectedBody is null ? null : protector.TryUnprotect(row.ProtectedBody);
            if (recipient is null || body is null)
            {
                // Keys rotated away or the value was tampered with. Retrying
                // cannot help, and throwing would stall every message behind it.
                row.RecordFailure("notification.payload.unreadable", retryable: false, settings, now);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
                return AttemptOutcome.DeadLettered;
            }

            message = new NotificationMessage(
                row.Id,
                row.Kind,
                row.Channel,
                recipient,
                row.Subject,
                body,
                row.AttemptCount + 1);
            await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await RecordAsync(id, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<NotificationDeliveryResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        var sender = senders.FirstOrDefault(item => item.Channel == message.Channel);
        if (sender is null)
        {
            // Configuration gap, not a transient fault. Retrying every 30 seconds
            // forever would hide it.
            return new NotificationDeliveryResult(false, Retryable: false, "notification.channel.unconfigured");
        }

        try
        {
            return await sender.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // An adapter that throws is treated as a transient provider fault.
            // The exception is deliberately not carried into the row: it can
            // contain the recipient or the link, and this row is evidence an
            // operator reads.
            return new NotificationDeliveryResult(false, Retryable: true, "notification.provider.error");
        }
    }

    private async Task<AttemptOutcome> RecordAsync(
        Guid id,
        NotificationDeliveryResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var row = await dbContext.Messages
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || row.State != OutboxMessageState.Pending)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AttemptOutcome.Skipped;
        }

        var now = timeProvider.GetUtcNow();
        AttemptOutcome outcome;
        if (result.Delivered)
        {
            row.RecordDelivered(now);
            outcome = AttemptOutcome.Delivered;
        }
        else
        {
            var terminal = row.RecordFailure(result.FailureCode, result.Retryable, settings, now);
            outcome = terminal ? AttemptOutcome.DeadLettered : AttemptOutcome.Retrying;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    private enum AttemptOutcome
    {
        Skipped,
        Delivered,
        Retrying,
        DeadLettered,
    }
}
