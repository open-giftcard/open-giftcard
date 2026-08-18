using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Payments.Contracts;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Api.Services;

/// <summary>
/// Releases holds whose ADR-044 window has elapsed. A provision already stops
/// reserving value at its deadline because availability is clock-derived, so
/// this sweep only settles the row; a late run cannot leave a cardholder's value
/// stranded in the meantime.
/// </summary>
internal sealed partial class PaymentProvisionExpirationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<PaymentProvisionOptions> options,
    ILogger<PaymentProvisionExpirationWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, Exception?> BatchCompleted =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(1801, nameof(BatchCompleted)),
            "Payment provision sweep expired {Expired} holds.");

    private static readonly Action<ILogger, Exception?> BatchFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1802, nameof(BatchFailed)),
            "Payment provision sweep failed.");

    private readonly PaymentProvisionOptions settings = options.Value;

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
                context.SetSystem(SystemActorIds.PaymentProvisionExpiration, []);
                var processor = scope.ServiceProvider
                    .GetRequiredService<IPaymentProvisionExpirationProcessor>();
                var result = await processor.ProcessDueAsync(
                    settings.ExpirationBatchSize,
                    stoppingToken).ConfigureAwait(false);
                if (result.Expired > 0)
                {
                    BatchCompleted(logger, result.Expired, null);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
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
