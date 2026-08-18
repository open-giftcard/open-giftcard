using GiftCardPlatform.BuildingBlocks.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GiftCardPlatform.Modules.Payments.Infrastructure;

internal sealed class PaymentsDbContextFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("GIFTCARD_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException(
                "GIFTCARD_MIGRATIONS_CONNECTION is required for design-time migrations. "
                + "Set it to the migrator role connection string; see README.");

        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    PaymentsDbContext.MigrationsHistoryTable,
                    PaymentsDbContext.Schema))
            .Options;

        return new PaymentsDbContext(options, new MutableExecutionContext());
    }
}
