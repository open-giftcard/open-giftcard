using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GiftCardPlatform.Modules.Identity.Infrastructure;

internal sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("GIFTCARD_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException(
                "GIFTCARD_MIGRATIONS_CONNECTION is required for design-time migrations. "
                + "Set it to the migrator role connection string; see README.");

        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    IdentityDbContext.MigrationsHistoryTable,
                    IdentityDbContext.Schema))
            .Options;

        return new IdentityDbContext(options);
    }
}
