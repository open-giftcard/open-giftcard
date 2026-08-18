using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Distribution.Application;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.Distribution.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Modules.Distribution;

public static class DistributionModuleExtensions
{
    public static IServiceCollection AddDistributionModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<DistributionOptions>()
            .Configure(options =>
            {
                if (configuration is null)
                {
                    return;
                }

                if (int.TryParse(
                        configuration[
                            $"{DistributionOptions.SectionName}:ClaimTokenLifetimeHours"],
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var lifetimeHours))
                {
                    options.ClaimTokenLifetimeHours = lifetimeHours;
                }

                if (int.TryParse(
                        configuration[
                            $"{DistributionOptions.SectionName}:MaximumFailedClaimAttempts"],
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var failedAttempts))
                {
                    options.MaximumFailedClaimAttempts = failedAttempts;
                }

                options.ClaimBaseUrl =
                    configuration[$"{DistributionOptions.SectionName}:ClaimBaseUrl"]
                    ?? options.ClaimBaseUrl;
            })
            .Validate(
                options => options.ClaimTokenLifetimeHours is >= 1 and <= 168,
                "Distribution:ClaimTokenLifetimeHours must be between 1 and 168.")
            .Validate(
                options => options.MaximumFailedClaimAttempts is >= 1 and <= 20,
                "Distribution:MaximumFailedClaimAttempts must be between 1 and 20.")
            .Validate(
                options => Uri.TryCreate(
                    options.ClaimBaseUrl,
                    UriKind.Absolute,
                    out var uri) &&
                    uri.Scheme is "http" or "https",
                "Distribution:ClaimBaseUrl must be an absolute HTTP or HTTPS URL.")
            .ValidateOnStart();

        services.AddDbContext<DistributionDbContext>((serviceProvider, options) =>
            options.UseNpgsql(
                serviceProvider.GetRequiredService<ScopedDatabaseConnection>().Connection,
                npgsql => npgsql.MigrationsHistoryTable(
                    DistributionDbContext.MigrationsHistoryTable,
                    DistributionDbContext.Schema)));
        services.AddScoped<GiftCardDistributionService>();
        services.AddScoped<IGiftCardDistributionService>(
            serviceProvider =>
                serviceProvider.GetRequiredService<GiftCardDistributionService>());
        services.AddScoped<IBulkGiftCardBatchService, BulkGiftCardBatchService>();
        services.AddScoped<IBulkGiftCardBatchProcessor, BulkGiftCardBatchProcessor>();
        services.AddScoped<BulkGiftCardBatchFailureSettler>();
        services.AddScoped<IGiftCardClaimService, GiftCardClaimService>();
        services.AddScoped<IPartnerEpinService, PartnerEpinService>();
        services.AddScoped<IDistributionLifecycleWriter, DistributionLifecycleWriter>();

        // Delivery is the outbox's job: the notifier queues inside the caller's
        // transaction and the dispatcher sends. Development reads that same
        // durable row, so there is no in-process copy that can drift from it.
        services.AddScoped<IGiftCardClaimNotifier, OutboxGiftCardClaimNotifier>();
        services.AddScoped<IDevelopmentClaimDeliveryQuery, DevelopmentClaimDeliveryQuery>();

        return services;
    }

    public static async Task MigrateDistributionModuleAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DistributionDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
