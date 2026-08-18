using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.GiftCards.Application;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.GiftCards.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GiftCardPlatform.Modules.GiftCards;

public static class GiftCardsModuleExtensions
{
    public static IServiceCollection AddGiftCardsModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<GiftCardsDbContext>((serviceProvider, options) =>
            options.UseNpgsql(
                serviceProvider.GetRequiredService<ScopedDatabaseConnection>().Connection,
                npgsql => npgsql.MigrationsHistoryTable(
                    GiftCardsDbContext.MigrationsHistoryTable,
                    GiftCardsDbContext.Schema)));
        services.AddScoped<GiftCardIssuanceService>();
        services.AddScoped<IGiftCardIssuanceService>(serviceProvider =>
            serviceProvider.GetRequiredService<GiftCardIssuanceService>());
        services.AddScoped<IPartnerGiftCardIssuanceService>(serviceProvider =>
            serviceProvider.GetRequiredService<GiftCardIssuanceService>());
        services.AddScoped<IAcceptedBulkGiftCardIssuanceService>(serviceProvider =>
            serviceProvider.GetRequiredService<GiftCardIssuanceService>());
        services.AddScoped<
            IGiftCardIssuanceRequestValidator,
            GiftCardIssuanceRequestValidator>();
        services.AddScoped<IGiftCardInventoryQuery, GiftCardInventoryQuery>();
        services.AddScoped<IGiftCardOwnershipWriter, GiftCardOwnershipWriter>();
        services.AddScoped<IGiftCardSharingWriter, GiftCardSharingWriter>();
        services.AddScoped<IGiftCardPaymentWriter, GiftCardPaymentWriter>();
        services.AddScoped<GiftCardLifecycleService>();
        services.AddScoped<IGiftCardLifecycleService>(
            serviceProvider =>
                serviceProvider.GetRequiredService<GiftCardLifecycleService>());
        services.AddScoped<IGiftCardLifecycleHistoryQuery, GiftCardLifecycleHistoryQuery>();
        services.AddScoped<IGiftCardExpirationProcessor, GiftCardExpirationProcessor>();

        return services;
    }

    public static async Task MigrateGiftCardsModuleAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GiftCardsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
