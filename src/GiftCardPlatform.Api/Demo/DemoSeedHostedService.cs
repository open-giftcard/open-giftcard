using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Api.Demo;

/// <summary>
/// Runs the demonstration seed once at startup.
///
/// Registered only on the Development branch of <c>Program</c>, so outside
/// Development this type is never constructed and the seed cannot run whatever
/// configuration says. It still checks <see cref="DemoSeedOptions.Enabled"/>,
/// because a developer sharing a database with a colleague should not have it
/// reshaped by starting the API.
///
/// A seed failure is logged and swallowed. The seed is a convenience; refusing
/// to start the API because demonstration data could not be built would turn a
/// nicety into an outage on a developer machine.
/// </summary>
internal sealed class DemoSeedHostedService(
    DemoSeeder seeder,
    IOptions<DemoSeedOptions> options,
    IHostEnvironment environment,
    ILogger<DemoSeedHostedService> logger) : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> WrongEnvironment =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1960, nameof(WrongEnvironment)),
            "The demo seed was reached in the {Environment} environment and refused to run.");

    private static readonly Action<ILogger, DemoSeedOutcome, Exception?> Finished =
        LoggerMessage.Define<DemoSeedOutcome>(
            LogLevel.Information,
            new EventId(1961, nameof(Finished)),
            "Demo seed finished: {Outcome}.");

    private static readonly Action<ILogger, Exception?> Failed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1962, nameof(Failed)),
            "Demo seed failed. The API is running and usable; the demonstration data is " +
            "incomplete. Re-running against a fresh database is the usual fix.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!environment.IsDevelopment())
        {
            // Belt and braces. Program does not register this outside Development.
            WrongEnvironment(logger, environment.EnvironmentName, null);
            return;
        }

        if (!options.Value.Enabled)
        {
            return;
        }

        try
        {
            var outcome = await seeder.SeedAsync(stoppingToken).ConfigureAwait(false);
            Finished(logger, outcome, null);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception exception)
        {
            Failed(logger, exception);
        }
    }
}
