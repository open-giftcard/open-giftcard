using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Sharing.Contracts;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Api.Services;

internal sealed class ShareExpirationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<SharingOptions> options,
    PlatformMetrics metrics,
    ILogger<ShareExpirationWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, Exception?> BatchCompleted =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(1601, nameof(BatchCompleted)),
            "Share expiration batch expired {Expired} shares.");

    private static readonly Action<ILogger, Exception?> BatchFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1602, nameof(BatchFailed)),
            "Share expiration batch failed; the next interval will retry safely.");

    private readonly SharingOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.ExpirationEnabled)
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
                context.SetSystem(SystemActorIds.ShareExpiration, []);
                var processor = scope.ServiceProvider.GetRequiredService<IShareExpirationProcessor>();
                var expired = await processor.ProcessDueAsync(
                    settings.ExpirationBatchSize,
                    stoppingToken).ConfigureAwait(false);
                if (expired > 0)
                {
                    BatchCompleted(logger, expired, null);
                    metrics.RecordWorkerItems("share_expiration", "expired", expired);
                }
                metrics.RecordWorkerRun("share_expiration", "succeeded");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                metrics.RecordWorkerRun("share_expiration", "failed");
                BatchFailed(logger, exception);
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(settings.ExpirationPollIntervalSeconds),
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
