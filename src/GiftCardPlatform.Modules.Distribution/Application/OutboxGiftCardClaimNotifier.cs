using System.Globalization;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.Notifications.Contracts;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Modules.Distribution.Application;

/// <summary>
/// Queues the activation message instead of sending it.
///
/// The call site invokes this inside its business transaction, so the message
/// becomes durable exactly when the distribution does. The previous sink sent
/// after commit in process: a crash in that gap lost the activation link with no
/// way to recover it, and the recipient could never claim the card.
///
/// The notification identifier is the invitation identifier. One invitation has
/// exactly one activation message, which makes the idempotency key derivable
/// rather than invented, and lets Development look a delivery up by invitation.
/// </summary>
internal sealed class OutboxGiftCardClaimNotifier(
    INotificationOutbox outbox,
    IOptions<DistributionOptions> options)
    : IGiftCardClaimNotifier
{
    private readonly string claimBaseUrl = options.Value.ClaimBaseUrl.TrimEnd('?', '&');

    public async Task SendAsync(
        GiftCardClaimNotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var separator = claimBaseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var claimUrl =
            $"{claimBaseUrl}{separator}token={Uri.EscapeDataString(notification.ClaimToken)}";

        var expires = notification.ExpiresAtUtc.ToString("u", CultureInfo.InvariantCulture);
        var body =
            $"""
            You have received a gift card.

            Activate it here:
            {claimUrl}

            This link works once and expires at {expires}.
            If you were not expecting this, you can ignore the message.
            """;

        await outbox.EnqueueAsync(
            new NotificationRequest(
                Id: notification.InvitationId,
                Kind: NotificationKind.GiftCardClaimInvitation,
                Channel: notification.ContactType == RecipientContactType.Email
                    ? NotificationChannel.Email
                    : NotificationChannel.Sms,
                Recipient: notification.RecipientContact,
                MaskedRecipient: Mask(notification.ContactType, notification.RecipientContact),
                Subject: "Your gift card is ready",
                Body: body,
                OrganizationId: notification.IssuingOrganizationId,
                OwnerUserId: null,
                IdempotencyKey: $"gift_card.claim|{notification.InvitationId:D}",
                ExpiresAtUtc: notification.ExpiresAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    private static string Mask(RecipientContactType type, string contact)
    {
        if (type == RecipientContactType.Email)
        {
            var at = contact.IndexOf('@', StringComparison.Ordinal);
            return $"{contact[..1]}***{contact[at..]}";
        }

        return $"{contact[..Math.Min(3, contact.Length - 4)]}***{contact[^4..]}";
    }
}
