using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Authorization.Application;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Authorization.Domain;
using GiftCardPlatform.Modules.Authorization.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GiftCardPlatform.Modules.Authorization;

public static class AuthorizationModuleExtensions
{
    /// <summary>
    /// Registers the Authorization module's contract implementations. The
    /// DbContext and entities stay internal (ADR-004).
    /// </summary>
    public static IServiceCollection AddAuthorizationModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configuration is not null)
        {
            services.Configure<PlatformBootstrapOptions>(options =>
                options.Secret =
                    configuration[
                        $"{PlatformBootstrapOptions.SectionName}:Secret"] ?? string.Empty);
        }
        else
        {
            services.Configure<PlatformBootstrapOptions>(_ => { });
        }

        services.AddDbContext<AuthorizationDbContext>((sp, options) =>
            options.UseNpgsql(
                sp.GetRequiredService<ScopedDatabaseConnection>().Connection,
                npgsql => npgsql.MigrationsHistoryTable(
                    AuthorizationDbContext.MigrationsHistoryTable,
                    AuthorizationDbContext.Schema)));

        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IPermissionEvaluator, PermissionEvaluator>();
        services.AddScoped<IOrganizationPermissionAuthorizer, OrganizationPermissionAuthorizer>();
        services.AddScoped<IPlatformPermissionResolver, PlatformPermissionResolver>();
        services.AddScoped<IPlatformBootstrapService, PlatformBootstrapService>();
        services.AddScoped<
            IInitialOrganizationAdministratorService,
            InitialOrganizationAdministratorService>();

        return services;
    }

    /// <summary>Applies this module's migrations. Intended for local development and tests.</summary>
    public static async Task MigrateAuthorizationModuleAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        await PermissionCatalogueSynchronizer
            .EnsureAsync(dbContext, cancellationToken)
            .ConfigureAwait(false);
    }
}
