using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Notifications.Contracts;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Api.Services;

/// <summary>
/// Drains the notification outbox.
///
/// This is what makes delivery survive a restart: the message was committed with
/// the business change, so whatever happens to this process, some later run of
/// this loop still has it to send. A failure here delays an activation link; it
/// can never lose one, and it never touches a financial path.
/// </summary>
internal sealed partial class NotificationDispatcherWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationOptions> options,
    PlatformMetrics metrics,
    ILogger<NotificationDispatcherWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, int, int, Exception?> BatchCompleted =
        LoggerMessage.Define<int, int, int>(
            LogLevel.Information,
            new EventId(1901, nameof(BatchCompleted)),
            "Notification dispatch delivered {Delivered}, retrying {Retrying}, dead-lettered {DeadLettered}.");

    private static readonly Action<ILogger, Exception?> BatchFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1902, nameof(BatchFailed)),
            "Notification dispatch failed.");

    private readonly NotificationOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.DispatchEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<MutableExecutionContext>();
                context.SetCorrelationId(Guid.CreateVersion7());
                context.SetSystem(SystemActorIds.NotificationDispatch, []);

                var dispatcher = scope.ServiceProvider.GetRequiredService<INotificationDispatcher>();
                var result = await dispatcher
                    .DispatchDueAsync(settings.DispatchBatchSize, stoppingToken)
                    .ConfigureAwait(false);

                if (result.Attempted > 0)
                {
                    BatchCompleted(
                        logger,
                        result.Delivered,
                        result.Retrying,
                        result.DeadLettered,
                        null);
                    metrics.RecordWorkerItems(
                        "notification_dispatch",
                        "delivered",
                        result.Delivered);
                    metrics.RecordWorkerItems(
                        "notification_dispatch",
                        "retrying",
                        result.Retrying);
                    metrics.RecordWorkerItems(
                        "notification_dispatch",
                        "dead_lettered",
                        result.DeadLettered);
                }
                metrics.RecordWorkerRun("notification_dispatch", "succeeded");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                metrics.RecordWorkerRun("notification_dispatch", "failed");
                BatchFailed(logger, exception);
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(settings.DispatchPollIntervalSeconds),
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
