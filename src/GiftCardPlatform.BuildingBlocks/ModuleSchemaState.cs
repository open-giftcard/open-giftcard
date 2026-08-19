using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GiftCardPlatform.BuildingBlocks;

/// <summary>
/// Reports whether a module's database schema matches the build that is running.
///
/// This exists because connectivity is not readiness. A database can answer
/// <c>select 1</c> while its schema predates the code querying it, and the first
/// request that touches a missing column fails with a PostgreSQL <c>42703</c>
/// that reads like an application bug. That is not hypothetical: on 2026-08-19
/// the demonstration database answered <c>/health/ready</c> with 200 while
/// having no <c>partners</c> schema at all, and the failure only appeared when a
/// recipient pressed a button.
///
/// Reads only the migration history table, so it needs no privilege the
/// application role does not already hold: the history table is created by the
/// migration owner inside the module's schema, and the init script's
/// <c>ALTER DEFAULT PRIVILEGES</c> grants the application role SELECT on tables
/// that role creates.
/// </summary>
public static class ModuleSchemaState
{
    /// <summary>
    /// Migrations this build declares that the database has not recorded.
    /// Empty means the schema is at or ahead of what this build expects.
    /// </summary>
    public static async Task<IReadOnlyCollection<string>> GetPendingMigrationsAsync<TContext>(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TContext>();

        var pending = await dbContext.Database
            .GetPendingMigrationsAsync(cancellationToken)
            .ConfigureAwait(false);

        return pending.ToArray();
    }
}
