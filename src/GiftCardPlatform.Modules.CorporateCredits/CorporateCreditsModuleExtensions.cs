using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.CorporateCredits.Application;
using GiftCardPlatform.Modules.CorporateCredits.Contracts;
using GiftCardPlatform.Modules.CorporateCredits.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GiftCardPlatform.Modules.CorporateCredits;

public static class CorporateCreditsModuleExtensions
{
    public static IServiceCollection AddCorporateCreditsModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<CorporateCreditsDbContext>((serviceProvider, options) =>
            options.UseNpgsql(
                serviceProvider.GetRequiredService<ScopedDatabaseConnection>().Connection,
                npgsql => npgsql.MigrationsHistoryTable(
                    CorporateCreditsDbContext.MigrationsHistoryTable,
                    CorporateCreditsDbContext.Schema)));
        services.AddScoped<ICorporateCreditAllocationService, CorporateCreditAllocationService>();
        services.AddScoped<ICorporateCreditReversalService, CorporateCreditReversalService>();
        services.AddScoped<ICorporateCreditQueryService, CorporateCreditQueryService>();

        return services;
    }

    public static async Task MigrateCorporateCreditsModuleAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CorporateCreditsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
