using GiftCardPlatform.BuildingBlocks.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GiftCardPlatform.Modules.Distribution.Infrastructure;

internal sealed class DistributionDbContextFactory :
    IDesignTimeDbContextFactory<DistributionDbContext>
{
    public DistributionDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("GIFTCARD_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException(
                "GIFTCARD_MIGRATIONS_CONNECTION is required for design-time migrations. "
                + "Set it to the migrator role connection string; see README.");

        var options = new DbContextOptionsBuilder<DistributionDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    DistributionDbContext.MigrationsHistoryTable,
                    DistributionDbContext.Schema))
            .Options;

        return new DistributionDbContext(options, new MutableExecutionContext());
    }
}
