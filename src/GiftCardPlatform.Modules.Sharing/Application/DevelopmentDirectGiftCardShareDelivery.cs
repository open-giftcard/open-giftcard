using GiftCardPlatform.Modules.Notifications.Contracts;
using System.Collections.Concurrent;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Sharing.Contracts;
using GiftCardPlatform.Modules.Sharing.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Modules.Sharing.Application;

internal interface IDevelopmentDirectGiftCardShareDeliveryStore
{
    DevelopmentDirectGiftCardShareDeliveryResult? Find(Guid shareId);
}

internal sealed class DevelopmentDirectGiftCardShareNotificationSink(
    IOptions<SharingOptions> options,
    TimeProvider timeProvider) :
    IDirectGiftCardShareNotifier,
    IDevelopmentDirectGiftCardShareDeliveryStore
{
    private readonly ConcurrentDictionary<Guid, DevelopmentDirectGiftCardShareDeliveryResult> deliveries = [];
    private readonly string claimBaseUrl = options.Value.DirectClaimBaseUrl.TrimEnd('?', '&');

    public Task SendAsync(
        DirectGiftCardShareNotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();
        var separator = claimBaseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        deliveries[notification.ShareId] = new DevelopmentDirectGiftCardShareDeliveryResult(
            notification.ShareId,
            notification.ContactType,
            notification.MaskedRecipientContact,
            $"{claimBaseUrl}{separator}token={Uri.EscapeDataString(notification.ClaimToken)}",
            notification.ExpiresAtUtc,
            timeProvider.GetUtcNow());
        return Task.CompletedTask;
    }

    public DevelopmentDirectGiftCardShareDeliveryResult? Find(Guid shareId) =>
        deliveries.GetValueOrDefault(shareId);

    internal int Count => deliveries.Count;

}

/// <summary>
/// Development-only lookup of a direct-share claim link.
///
/// It reads the queued outbox message rather than an in-process copy, so what
/// Development shows and what the recipient receives are the same row by
/// construction. An in-memory capture is also not rolled back with the
/// transaction, which previously meant an abandoned share could still display a
/// live link.
///
/// The share identifier is the notification identifier, so no join is needed.
/// </summary>
internal sealed class DevelopmentDirectGiftCardShareDeliveryQuery(
    SharingDbContext dbContext,
    IDevelopmentNotificationQuery notifications,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext) : IDevelopmentDirectGiftCardShareDeliveryQuery
{
    public async Task<DevelopmentDirectGiftCardShareDeliveryResult?> FindAsync(
        Guid shareId,
        CancellationToken cancellationToken)
    {
        if (!executionContext.IsAuthenticated || executionContext.IsPlatformOperator ||
            executionContext.UserId is null || shareId == Guid.Empty)
        {
            throw new ForbiddenException(
                "sharing.cardholder.required",
                "An authenticated cardholder is required.");
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var exists = await dbContext.Shares.AsNoTracking().AnyAsync(
            share => share.Id == shareId &&
                share.SenderUserId == executionContext.UserId.Value &&
                share.Kind == GiftCardShareKind.DirectInvitation,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            return null;
        }

        var queued = await notifications.FindAsync(shareId, cancellationToken).ConfigureAwait(false);
        if (queued is null)
        {
            // Already delivered or dead-lettered: the body is destroyed at that
            // point, so there is deliberately nothing left to show.
            return null;
        }

        var claimUrl = ExtractUrl(queued.Body);
        return claimUrl is null
            ? null
            : new DevelopmentDirectGiftCardShareDeliveryResult(
                shareId,
                queued.Channel == Notifications.Contracts.NotificationChannel.Email
                    ? GiftCardShareContactType.Email
                    : GiftCardShareContactType.Phone,
                queued.MaskedRecipient,
                claimUrl,
                queued.CapturedAtUtc,
                queued.CapturedAtUtc);
    }
    private static string? ExtractUrl(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var candidate = line.Trim();
            if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
}