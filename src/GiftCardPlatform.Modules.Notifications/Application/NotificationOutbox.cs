using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Notifications.Contracts;
using GiftCardPlatform.Modules.Notifications.Domain;
using GiftCardPlatform.Modules.Notifications.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Notifications.Application;

internal sealed class NotificationOutbox(
    NotificationsDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    INotificationPayloadProtector protector,
    INotificationChannelAvailability channelAvailability,
    TimeProvider timeProvider) : INotificationOutbox
{
    public async Task EnqueueAsync(
        NotificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Enqueue must join the caller's transaction, never open its own. If it
        // committed independently the message could survive a rolled-back
        // distribution and send an activation link for a card that was never
        // handed out.
        if (transactionCoordinator.Current is null)
        {
            throw new ConflictException(
                "notification.transaction.required",
                "A notification must be enqueued inside a business transaction.");
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        // A repeated business operation with the same idempotency key must not
        // queue a second copy. The unique index is the real guarantee; this read
        // just turns the common case into a no-op instead of a constraint error.
        var existing = await dbContext.Messages
            .AsNoTracking()
            .AnyAsync(
                message => message.IdempotencyKey == request.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        channelAvailability.RequireAvailable(request.Channel);

        var message = OutboxMessage.Create(
            request,
            protector.Protect(request.Recipient),
            protector.Protect(request.Body),
            timeProvider.GetUtcNow());

        dbContext.Messages.Add(message);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
