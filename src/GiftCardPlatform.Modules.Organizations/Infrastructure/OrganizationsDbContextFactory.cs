using GiftCardPlatform.BuildingBlocks.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GiftCardPlatform.Modules.Organizations.Infrastructure;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations</c>. Migrations are created
/// and applied by the migration-owner role (ADR-019), never the runtime role.
/// </summary>
internal sealed class OrganizationsDbContextFactory : IDesignTimeDbContextFactory<OrganizationsDbContext>
{
    public OrganizationsDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("GIFTCARD_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException(
                "GIFTCARD_MIGRATIONS_CONNECTION is required for design-time migrations. "
                + "Set it to the migrator role connection string; see README.");

        var options = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(
                OrganizationsDbContext.MigrationsHistoryTable,
                OrganizationsDbContext.Schema))
            .Options;

        // Design-time only: no request scope exists, and migrations never run a
        // tenant-filtered query, so an empty execution context is sufficient.
        return new OrganizationsDbContext(options, new MutableExecutionContext());
    }
}
