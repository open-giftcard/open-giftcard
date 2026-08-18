using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Identity.Application;
using GiftCardPlatform.Modules.Identity.Contracts;
using GiftCardPlatform.Modules.Identity.Domain;
using GiftCardPlatform.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GiftCardPlatform.Modules.Identity;

public static class IdentityModuleExtensions
{
    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configuration is not null)
        {
            services.Configure<IdentityTokenOptions>(
                configuration.GetSection(IdentityTokenOptions.SectionName));
        }
        else
        {
            services.Configure<IdentityTokenOptions>(_ => { });
        }

        services.AddDbContext<IdentityDbContext>((sp, options) =>
            options.UseNpgsql(
                sp.GetRequiredService<ScopedDatabaseConnection>().Connection,
                npgsql => npgsql.MigrationsHistoryTable(
                    IdentityDbContext.MigrationsHistoryTable,
                    IdentityDbContext.Schema)));

        services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddScoped<ITokenGenerator, TokenGenerator>();
        services.AddScoped<UserSessionTokenIssuer>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<AuthenticationService>();
        services.AddScoped<IAuthenticationService>(
            provider => provider.GetRequiredService<AuthenticationService>());
        services.AddScoped<IRecipientClaimSessionIssuer>(
            provider => provider.GetRequiredService<AuthenticationService>());
        services.AddScoped<IIdentityBootstrapService, IdentityBootstrapService>();
        services.AddScoped<IIdentityUserQuery, IdentityUserQuery>();
        services.AddScoped<IOrganizationStaffDirectory, OrganizationStaffDirectory>();
        services.AddScoped<IRecipientIdentityService, RecipientIdentityService>();
        services.AddScoped<IRecipientContactService, RecipientContactService>();

        return services;
    }

    public static async Task MigrateIdentityModuleAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
