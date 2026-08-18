using GiftCardPlatform.BuildingBlocks.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GiftCardPlatform.Modules.Sharing.Infrastructure;

internal sealed class SharingDbContextFactory :
    IDesignTimeDbContextFactory<SharingDbContext>
{
    public SharingDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("GIFTCARD_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException(
                "GIFTCARD_MIGRATIONS_CONNECTION is required for design-time migrations. "
                + "Set it to the migrator role connection string; see README.");

        var options = new DbContextOptionsBuilder<SharingDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    SharingDbContext.MigrationsHistoryTable,
                    SharingDbContext.Schema))
            .Options;

        return new SharingDbContext(options, new MutableExecutionContext());
    }
}
