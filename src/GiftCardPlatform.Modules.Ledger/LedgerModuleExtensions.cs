using GiftCardPlatform.BuildingBlocks;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Ledger.Application;
using GiftCardPlatform.Modules.Ledger.Contracts;
using GiftCardPlatform.Modules.Ledger.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GiftCardPlatform.Modules.Ledger;

public static class LedgerModuleExtensions
{
    public static IServiceCollection AddLedgerModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<LedgerDbContext>((serviceProvider, options) =>
            options.UseNpgsql(
                serviceProvider.GetRequiredService<ScopedDatabaseConnection>().Connection,
                npgsql => npgsql.MigrationsHistoryTable(
                    LedgerDbContext.MigrationsHistoryTable,
                    LedgerDbContext.Schema)));
        services.AddScoped<ILedgerWriter, LedgerWriter>();
        services.AddScoped<ILedgerBalanceQuery, LedgerBalanceQuery>();
        services.AddScoped<IGiftCardShareLedger, GiftCardShareLedger>();
        services.AddScoped<IGiftCardPaymentLedger, GiftCardPaymentLedger>();

        return services;
    }

    public static async Task MigrateLedgerModuleAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Migrations this build declares that the database has not recorded.
    /// Empty means the schema is at or ahead of what this build expects.
    /// </summary>
    public static Task<IReadOnlyCollection<string>> GetPendingLedgerMigrationsAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default) =>
        serviceProvider.GetPendingMigrationsAsync<LedgerDbContext>(cancellationToken);
}
