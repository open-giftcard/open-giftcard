using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Api.Services;

internal sealed class GiftCardExpirationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<GiftCardExpirationOptions> options,
    PlatformMetrics metrics,
    ILogger<GiftCardExpirationWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, int, int, Exception?> BatchCompleted =
        LoggerMessage.Define<int, int, int>(
            LogLevel.Information,
            new EventId(1401, nameof(BatchCompleted)),
            "Gift-card expiration batch examined {Examined}, expired {Expired}, conflicted {Conflicted}.");

    private static readonly Action<ILogger, Exception?> BatchFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1402, nameof(BatchFailed)),
            "Gift-card expiration batch failed; the next interval will retry safely.");

    private readonly GiftCardExpirationOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var executionContext =
                    scope.ServiceProvider.GetRequiredService<MutableExecutionContext>();
                executionContext.SetCorrelationId(Guid.CreateVersion7());
                executionContext.SetSystem(
                    SystemActorIds.GiftCardExpiration,
                    [PlatformPermissions.GiftCardsManageLifecycle]);
                var processor =
                    scope.ServiceProvider.GetRequiredService<IGiftCardExpirationProcessor>();
                var result = await processor.ProcessDueAsync(
                    settings.BatchSize,
                    stoppingToken).ConfigureAwait(false);
                if (result.Examined > 0)
                {
                    BatchCompleted(
                        logger,
                        result.Examined,
                        result.Expired,
                        result.Conflicted,
                        null);
                    metrics.RecordWorkerItems(
                        "gift_card_expiration",
                        "expired",
                        result.Expired);
                    metrics.RecordWorkerItems(
                        "gift_card_expiration",
                        "conflicted",
                        result.Conflicted);
                }
                metrics.RecordWorkerRun("gift_card_expiration", "succeeded");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                metrics.RecordWorkerRun("gift_card_expiration", "failed");
                BatchFailed(logger, exception);
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(settings.PollIntervalSeconds),
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
