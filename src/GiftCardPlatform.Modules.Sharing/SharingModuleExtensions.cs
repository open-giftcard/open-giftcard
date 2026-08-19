using GiftCardPlatform.BuildingBlocks;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Sharing.Application;
using GiftCardPlatform.Modules.Sharing.Contracts;
using GiftCardPlatform.Modules.Sharing.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GiftCardPlatform.Modules.Sharing;

public static class SharingModuleExtensions
{
    public static IServiceCollection AddSharingModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<SharingOptions>()
            .Configure(options =>
            {
                if (configuration is null)
                {
                    return;
                }

                var section = configuration.GetSection(SharingOptions.SectionName);
                if (int.TryParse(section[nameof(options.ClaimTokenLifetimeHours)], out var lifetime))
                {
                    options.ClaimTokenLifetimeHours = lifetime;
                }
                if (int.TryParse(section[nameof(options.MaximumFailedPinAttempts)], out var attempts))
                {
                    options.MaximumFailedPinAttempts = attempts;
                }
                options.ClaimBaseUrl = section[nameof(options.ClaimBaseUrl)] ?? options.ClaimBaseUrl;
                options.DirectClaimBaseUrl = section[nameof(options.DirectClaimBaseUrl)] ?? options.DirectClaimBaseUrl;
                if (bool.TryParse(section[nameof(options.ExpirationEnabled)], out var enabled))
                {
                    options.ExpirationEnabled = enabled;
                }
                if (int.TryParse(section[nameof(options.ExpirationPollIntervalSeconds)], out var interval))
                {
                    options.ExpirationPollIntervalSeconds = interval;
                }
                if (int.TryParse(section[nameof(options.ExpirationBatchSize)], out var batchSize))
                {
                    options.ExpirationBatchSize = batchSize;
                }
            })
            .Validate(options => options.ClaimTokenLifetimeHours == 24, "Sharing:ClaimTokenLifetimeHours must be 24.")
            .Validate(options => options.MaximumFailedPinAttempts == 5, "Sharing:MaximumFailedPinAttempts must be 5.")
            .Validate(options => Uri.TryCreate(options.ClaimBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https", "Sharing:ClaimBaseUrl must be an absolute HTTP or HTTPS URL.")
            .Validate(options => Uri.TryCreate(options.DirectClaimBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https", "Sharing:DirectClaimBaseUrl must be an absolute HTTP or HTTPS URL.")
            .Validate(options => options.ExpirationPollIntervalSeconds is >= 5 and <= 86_400, "Sharing:ExpirationPollIntervalSeconds must be between 5 and 86400.")
            .Validate(options => options.ExpirationBatchSize is >= 1 and <= 100, "Sharing:ExpirationBatchSize must be between 1 and 100.")
            .ValidateOnStart();

        services.AddDbContext<SharingDbContext>((serviceProvider, options) =>
            options.UseNpgsql(
                serviceProvider.GetRequiredService<ScopedDatabaseConnection>().Connection,
                npgsql => npgsql.MigrationsHistoryTable(
                    SharingDbContext.MigrationsHistoryTable,
                    SharingDbContext.Schema)));
        services.AddScoped<GiftCardShareService>();
        services.AddScoped<IProtectedGiftCardShareService>(provider => provider.GetRequiredService<GiftCardShareService>());
        services.AddScoped<IDirectGiftCardShareService>(provider => provider.GetRequiredService<GiftCardShareService>());
        services.AddScoped<IShareReservationQuery>(provider => provider.GetRequiredService<GiftCardShareService>());
        services.AddScoped<IShareExpirationProcessor>(provider => provider.GetRequiredService<GiftCardShareService>());
        services.AddScoped<IShareLifecycleWriter>(provider => provider.GetRequiredService<GiftCardShareService>());
        services.AddSingleton<DevelopmentDirectGiftCardShareNotificationSink>();
        services.AddScoped<IDirectGiftCardShareNotifier, OutboxDirectGiftCardShareNotifier>();
        services.AddSingleton<IDevelopmentDirectGiftCardShareDeliveryStore>(provider =>
            provider.GetRequiredService<DevelopmentDirectGiftCardShareNotificationSink>());
        services.AddScoped<IDevelopmentDirectGiftCardShareDeliveryQuery,
            DevelopmentDirectGiftCardShareDeliveryQuery>();
        return services;
    }

    public static async Task MigrateSharingModuleAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SharingDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Migrations this build declares that the database has not recorded.
    /// Empty means the schema is at or ahead of what this build expects.
    /// </summary>
    public static Task<IReadOnlyCollection<string>> GetPendingSharingMigrationsAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default) =>
        serviceProvider.GetPendingMigrationsAsync<SharingDbContext>(cancellationToken);
}
