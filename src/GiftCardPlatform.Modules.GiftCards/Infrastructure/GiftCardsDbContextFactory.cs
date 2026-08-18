using GiftCardPlatform.BuildingBlocks.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GiftCardPlatform.Modules.GiftCards.Infrastructure;

internal sealed class GiftCardsDbContextFactory :
    IDesignTimeDbContextFactory<GiftCardsDbContext>
{
    public GiftCardsDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("GIFTCARD_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException(
                "GIFTCARD_MIGRATIONS_CONNECTION is required for design-time migrations. "
                + "Set it to the migrator role connection string; see README.");

        var options = new DbContextOptionsBuilder<GiftCardsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    GiftCardsDbContext.MigrationsHistoryTable,
                    GiftCardsDbContext.Schema))
            .Options;

        return new GiftCardsDbContext(options, new MutableExecutionContext());
    }
}
