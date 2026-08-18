using GiftCardPlatform.BuildingBlocks.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GiftCardPlatform.Modules.CorporateCredits.Infrastructure;

internal sealed class CorporateCreditsDbContextFactory :
    IDesignTimeDbContextFactory<CorporateCreditsDbContext>
{
    public CorporateCreditsDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("GIFTCARD_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException(
                "GIFTCARD_MIGRATIONS_CONNECTION is required for design-time migrations. "
                + "Set it to the migrator role connection string; see README.");

        var options = new DbContextOptionsBuilder<CorporateCreditsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    CorporateCreditsDbContext.MigrationsHistoryTable,
                    CorporateCreditsDbContext.Schema))
            .Options;

        return new CorporateCreditsDbContext(options, new MutableExecutionContext());
    }
}
