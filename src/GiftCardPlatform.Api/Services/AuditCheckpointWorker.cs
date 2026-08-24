using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Audit.Contracts;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Api.Services;

internal sealed partial class AuditCheckpointWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AuditCheckpointOptions> options,
    PlatformMetrics metrics,
    ILogger<AuditCheckpointWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> PassFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1901, nameof(PassFailed)),
            "Audit checkpoint processing failed; business writes remain available but new audit history is not sealed.");

    private static readonly Action<ILogger, string, Exception?> VerificationFailed =
        LoggerMessage.Define<string>(
            LogLevel.Critical,
            new EventId(1902, nameof(VerificationFailed)),
            "Audit checkpoint verification failed with {FailureCode}.");

    private readonly AuditCheckpointOptions settings = options.Value;

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
                for (var pass = 0; pass < 4; pass++)
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var context = scope.ServiceProvider.GetRequiredService<MutableExecutionContext>();
                    context.SetCorrelationId(Guid.CreateVersion7());
                    context.SetSystem(SystemActorIds.AuditCheckpoint, []);
                    var processor = scope.ServiceProvider.GetRequiredService<IAuditCheckpointProcessor>();
                    var result = await processor.ProcessNextAsync(
                        settings.BatchSize,
                        stoppingToken).ConfigureAwait(false);
                    // A published witness completes one safe batch. Do not use
                    // the spare pass to create the next manifest immediately:
                    // verification below must never observe a deliberately
                    // half-finished checkpoint when more than one batch is
                    // waiting.
                    if (result.WitnessPublished)
                    {
                        break;
                    }

                    if (!result.ManifestCreated && !result.SignatureCreated && !result.WitnessPublished)
                    {
                        break;
                    }
                }

                await using var verificationScope = scopeFactory.CreateAsyncScope();
                var verificationContext = verificationScope.ServiceProvider
                    .GetRequiredService<MutableExecutionContext>();
                verificationContext.SetCorrelationId(Guid.CreateVersion7());
                verificationContext.SetSystem(SystemActorIds.AuditCheckpoint, []);
                var verification = await verificationScope.ServiceProvider
                    .GetRequiredService<IAuditCheckpointProcessor>()
                    .VerifyAsync(stoppingToken).ConfigureAwait(false);
                if (!verification.IsValid)
                {
                    metrics.RecordAuditVerificationFailure();
                    VerificationFailed(logger, verification.FailureCode ?? "unknown", null);
                }
                metrics.RecordWorkerRun(
                    "audit_checkpoint",
                    verification.IsValid ? "succeeded" : "degraded");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                metrics.RecordWorkerRun("audit_checkpoint", "failed");
                PassFailed(logger, exception);
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
