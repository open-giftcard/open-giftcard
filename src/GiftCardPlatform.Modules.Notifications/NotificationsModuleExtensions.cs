using GiftCardPlatform.BuildingBlocks;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Notifications.Application;
using GiftCardPlatform.Modules.Notifications.Contracts;
using GiftCardPlatform.Modules.Notifications.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GiftCardPlatform.Modules.Notifications;

public static class NotificationsModuleExtensions
{
    public static IServiceCollection AddNotificationsModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<NotificationOptions>()
            .Configure(options =>
            {
                var section = configuration?.GetSection(NotificationOptions.SectionName);
                if (section is null)
                {
                    return;
                }

                if (bool.TryParse(section[nameof(options.DispatchEnabled)], out var enabled))
                {
                    options.DispatchEnabled = enabled;
                }

                options.DispatchPollIntervalSeconds = ReadInt(
                    section[nameof(options.DispatchPollIntervalSeconds)],
                    options.DispatchPollIntervalSeconds);
                options.DispatchBatchSize = ReadInt(
                    section[nameof(options.DispatchBatchSize)],
                    options.DispatchBatchSize);
                options.MaximumAttempts = ReadInt(
                    section[nameof(options.MaximumAttempts)],
                    options.MaximumAttempts);
                options.BaseRetryDelaySeconds = ReadInt(
                    section[nameof(options.BaseRetryDelaySeconds)],
                    options.BaseRetryDelaySeconds);
                options.MaximumRetryDelaySeconds = ReadInt(
                    section[nameof(options.MaximumRetryDelaySeconds)],
                    options.MaximumRetryDelaySeconds);
            })
            .Validate(
                options => options.DispatchPollIntervalSeconds is >= 1 and <= 3600,
                "Notifications:DispatchPollIntervalSeconds must be between 1 and 3600.")
            .Validate(
                options => options.DispatchBatchSize is >= 1 and <= 500,
                "Notifications:DispatchBatchSize must be between 1 and 500.")
            .Validate(
                options => options.MaximumAttempts is >= 1 and <= 50,
                "Notifications:MaximumAttempts must be between 1 and 50.")
            .Validate(
                options => options.BaseRetryDelaySeconds is >= 1 and <= 3600,
                "Notifications:BaseRetryDelaySeconds must be between 1 and 3600.")
            .Validate(
                options => options.MaximumRetryDelaySeconds >= options.BaseRetryDelaySeconds,
                "Notifications:MaximumRetryDelaySeconds must be at least the base delay.")
            .ValidateOnStart();

        services.AddDbContext<NotificationsDbContext>((serviceProvider, options) =>
            options.UseNpgsql(
                serviceProvider.GetRequiredService<ScopedDatabaseConnection>().Connection,
                npgsql => npgsql.MigrationsHistoryTable(
                    NotificationsDbContext.MigrationsHistoryTable,
                    NotificationsDbContext.Schema)));

        services.AddScoped<INotificationOutbox, NotificationOutbox>();
        services.AddScoped<INotificationChannelAvailability, NotificationChannelAvailability>();
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
        services.AddScoped<IDevelopmentNotificationQuery, DevelopmentNotificationQuery>();
        return services;
    }

    public static async Task MigrateNotificationsModuleAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int ReadInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    /// <summary>
    /// Migrations this build declares that the database has not recorded.
    /// Empty means the schema is at or ahead of what this build expects.
    /// </summary>
    public static Task<IReadOnlyCollection<string>> GetPendingNotificationsMigrationsAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default) =>
        serviceProvider.GetPendingMigrationsAsync<NotificationsDbContext>(cancellationToken);
}
