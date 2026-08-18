using System.Globalization;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Organizations.Application;
using GiftCardPlatform.Modules.Organizations.Contracts;
using GiftCardPlatform.Modules.Organizations.Domain;
using GiftCardPlatform.Modules.Organizations.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GiftCardPlatform.Modules.Organizations;

public static class OrganizationsModuleExtensions
{
    /// <summary>
    /// Registers the Organizations module. Only <see cref="IOrganizationService"/>
    /// is visible outside the module.
    /// </summary>
    public static IServiceCollection AddOrganizationsModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ADR-010: the depth limit is configurable, but validated here against the
        // database ceiling so configuration can never exceed what the
        // ck_organizations_max_depth check constraint accepts. Invalid
        // configuration fails at startup rather than at the first subsidiary.
        services.Configure<OrganizationHierarchyOptions>(options =>
            options.MaxDepth = ResolveMaxDepth(configuration));

        services.AddDbContext<OrganizationsDbContext>((sp, options) =>
            options.UseNpgsql(
                sp.GetRequiredService<ScopedDatabaseConnection>().Connection,
                npgsql => npgsql.MigrationsHistoryTable(
                    OrganizationsDbContext.MigrationsHistoryTable,
                    OrganizationsDbContext.Schema)));

        services.AddScoped<IOrganizationService, OrganizationService>();
        services.AddScoped<IOrganizationDiscoveryQuery, OrganizationDiscoveryQuery>();
        services.AddScoped<IMembershipService, MembershipService>();
        services.AddScoped<IActiveMembershipResolver, ActiveMembershipResolver>();
        services.AddScoped<
            IInitialAdministratorMembershipProvisioner,
            InitialAdministratorMembershipProvisioner>();
        services.AddScoped<ISubsidiaryService, SubsidiaryService>();
        services.AddScoped<IOrganizationHierarchyQuery, OrganizationHierarchyQuery>();
        services.AddScoped<
            IOrganizationFinancialEligibilityQuery,
            OrganizationFinancialEligibilityQuery>();

        return services;
    }

    private static int ResolveMaxDepth(IConfiguration? configuration)
    {
        var configured = configuration?[$"{OrganizationHierarchyOptions.SectionName}:MaxDepth"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            return OrganizationHierarchy.DefaultMaxDepth;
        }

        if (!int.TryParse(configured, CultureInfo.InvariantCulture, out var maxDepth) ||
            maxDepth < 1 ||
            maxDepth > OrganizationHierarchy.DefaultMaxDepth)
        {
            throw new InvalidOperationException(
                $"{OrganizationHierarchyOptions.SectionName}:MaxDepth must be an integer between 1 and " +
                $"{OrganizationHierarchy.DefaultMaxDepth}. Raising it beyond {OrganizationHierarchy.DefaultMaxDepth} " +
                "requires a migration that widens the ck_organizations_max_depth check constraint.");
        }

        return maxDepth;
    }

    /// <summary>Applies this module's migrations. Intended for local development and tests.</summary>
    public static async Task MigrateOrganizationsModuleAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
