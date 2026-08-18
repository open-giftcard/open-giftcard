using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Notifications.Contracts;
using GiftCardPlatform.Modules.Notifications.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Notifications.Application;

/// <summary>
/// Lets the Development console show what was queued without a mail server.
/// Returns the masked recipient only, never the real address, and only while
/// the message is still pending: once delivered or dead-lettered the body is
/// destroyed and there is nothing left to show.
///
/// The endpoint that exposes this is mapped only in Development.
/// </summary>
internal sealed class DevelopmentNotificationQuery(
    NotificationsDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    INotificationPayloadProtector protector) : IDevelopmentNotificationQuery
{
    public async Task<DevelopmentNotificationResult?> FindAsync(
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var message = await dbContext.Messages
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == notificationId, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (message?.ProtectedBody is null)
        {
            return null;
        }

        var body = protector.TryUnprotect(message.ProtectedBody);
        return body is null
            ? null
            : new DevelopmentNotificationResult(
                message.Id,
                message.Kind,
                message.Channel,
                message.MaskedRecipient,
                body,
                message.CreatedAtUtc);
    }
}
