using GiftCardPlatform.BuildingBlocks.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GiftCardPlatform.Modules.Partners.Infrastructure;

internal sealed class PartnersDbContextFactory :
    IDesignTimeDbContextFactory<PartnersDbContext>
{
    public PartnersDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("GIFTCARD_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException(
                "GIFTCARD_MIGRATIONS_CONNECTION is required for design-time migrations. "
                + "Set it to the migrator role connection string; see README.");

        var options = new DbContextOptionsBuilder<PartnersDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    PartnersDbContext.MigrationsHistoryTable,
                    PartnersDbContext.Schema))
            .Options;

        return new PartnersDbContext(options, new MutableExecutionContext());
    }
}
