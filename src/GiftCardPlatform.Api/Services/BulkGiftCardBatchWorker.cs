using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Distribution.Contracts;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Api.Services;

internal sealed class BulkGiftCardBatchWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<BulkGiftCardBatchOptions> options,
    ILogger<BulkGiftCardBatchWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, int, int, int, Exception?> ChunkCompleted =
        LoggerMessage.Define<int, int, int, int>(
            LogLevel.Information,
            new EventId(1451, nameof(ChunkCompleted)),
            "Bulk gift-card chunk examined {Examined}, succeeded {Succeeded}, failed {Failed}, conflicted {Conflicted}.");

    private static readonly Action<ILogger, Exception?> ChunkFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1452, nameof(ChunkFailed)),
            "Bulk gift-card processing failed; pending work remains durable for retry.");

    private readonly BulkGiftCardBatchOptions settings = options.Value;

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
                var examined = 0;
                var succeeded = 0;
                var failed = 0;
                var conflicted = 0;
                for (var index = 0; index < settings.ChunkSize; index++)
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var context = scope.ServiceProvider
                        .GetRequiredService<MutableExecutionContext>();
                    context.SetCorrelationId(Guid.CreateVersion7());
                    context.SetSystem(SystemActorIds.BulkGiftCardBatch, []);
                    var processor = scope.ServiceProvider
                        .GetRequiredService<IBulkGiftCardBatchProcessor>();
                    var result = await processor
                        .ProcessPendingAsync(1, stoppingToken)
                        .ConfigureAwait(false);
                    examined += result.Examined;
                    succeeded += result.Succeeded;
                    failed += result.Failed;
                    conflicted += result.Conflicted;
                    if (result.Examined == 0)
                    {
                        break;
                    }
                }

                if (examined > 0)
                {
                    ChunkCompleted(
                        logger,
                        examined,
                        succeeded,
                        failed,
                        conflicted,
                        null);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                ChunkFailed(logger, exception);
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
