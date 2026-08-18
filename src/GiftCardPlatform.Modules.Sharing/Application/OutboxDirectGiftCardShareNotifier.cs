using System.Globalization;
using GiftCardPlatform.Modules.Notifications.Contracts;
using GiftCardPlatform.Modules.Sharing.Contracts;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Modules.Sharing.Application;

/// <summary>
/// Queues the direct-share invitation instead of sending it in process.
///
/// The Development sink this replaces captured the message in memory and never
/// contacted a provider, so a recipient invited by email was never actually
/// emailed. Distribution had the same defect and was fixed the same way: enqueue
/// inside the caller's transaction so the message becomes durable exactly when
/// the share does.
///
/// The share identifier is the notification identifier. One invitation has
/// exactly one message, which makes the idempotency key derivable rather than
/// invented.
/// </summary>
internal sealed class OutboxDirectGiftCardShareNotifier(
    INotificationOutbox outbox,
    IOptions<SharingOptions> options) : IDirectGiftCardShareNotifier
{
    private readonly string claimBaseUrl = options.Value.DirectClaimBaseUrl.TrimEnd('?', '&');

    public async Task SendAsync(
        DirectGiftCardShareNotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var separator = claimBaseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var claimUrl =
            $"{claimBaseUrl}{separator}token={Uri.EscapeDataString(notification.ClaimToken)}";
        var expires = notification.ExpiresAtUtc.ToString("u", CultureInfo.InvariantCulture);

        var body =
            $"""
            Someone has shared gift card value with you.

            Claim it here:
            {claimUrl}

            This link works once and expires at {expires}.
            If you were not expecting this, you can ignore the message.
            """;

        await outbox.EnqueueAsync(
            new NotificationRequest(
                Id: notification.ShareId,
                Kind: NotificationKind.GiftCardShareInvitation,
                Channel: notification.ContactType == GiftCardShareContactType.Email
                    ? NotificationChannel.Email
                    : NotificationChannel.Sms,
                Recipient: notification.RecipientContact,
                MaskedRecipient: notification.MaskedRecipientContact,
                Subject: "Someone shared gift card value with you",
                Body: body,
                // A share is between two people, not a company operation, so the
                // message is owned by the sender rather than by an organization.
                OrganizationId: null,
                OwnerUserId: notification.SenderUserId,
                IdempotencyKey: $"gift_card.share|{notification.ShareId:D}",
                ExpiresAtUtc: notification.ExpiresAtUtc),
            cancellationToken).ConfigureAwait(false);
    }
}
