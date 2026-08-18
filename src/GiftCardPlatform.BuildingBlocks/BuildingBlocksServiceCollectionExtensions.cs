using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace GiftCardPlatform.BuildingBlocks;

public static class BuildingBlocksServiceCollectionExtensions
{
    /// <summary>
    /// Registers the execution context and the shared-connection transaction
    /// coordinator used by every module.
    /// </summary>
    public static IServiceCollection AddBuildingBlocks(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddScoped<MutableExecutionContext>();
        services.AddScoped<IExecutionContext>(sp => sp.GetRequiredService<MutableExecutionContext>());

        services.AddScoped(_ => new ScopedDatabaseConnection(connectionString));
        services.AddSingleton<IDatabaseConnectionFactory>(_ => new DatabaseConnectionFactory(connectionString));
        services.AddSingleton<ISessionContextWriter, SessionContextWriter>();
        services.AddScoped<ITransactionCoordinator, TransactionCoordinator>();

        services.AddSingleton(TimeProvider.System);

        return services;
    }
}
