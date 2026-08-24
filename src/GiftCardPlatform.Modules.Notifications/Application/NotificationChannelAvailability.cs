using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Notifications.Contracts;

namespace GiftCardPlatform.Modules.Notifications.Application;

internal sealed class NotificationChannelAvailability(
    IEnumerable<INotificationChannelSender> senders) : INotificationChannelAvailability
{
    private readonly HashSet<NotificationChannel> available = senders
        .Select(sender => sender.Channel)
        .ToHashSet();

    public bool IsAvailable(NotificationChannel channel) => available.Contains(channel);

    public void RequireAvailable(NotificationChannel channel)
    {
        if (!IsAvailable(channel))
        {
            throw new ValidationFailedException(
                "notification.channel.unconfigured",
                $"The {channel} notification channel is not configured.");
        }
    }
}
