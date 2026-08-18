using GiftCardPlatform.BuildingBlocks.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GiftCardPlatform.Modules.Audit.Infrastructure;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations</c>. At runtime the context
/// uses the shared scoped connection; the tooling has no DI scope, so it builds
/// options from a connection string instead.
///
/// Migrations are created and applied by the migration-owner role (ADR-019),
/// never the runtime application role.
/// </summary>
internal sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("GIFTCARD_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException(
                "GIFTCARD_MIGRATIONS_CONNECTION is required for design-time migrations. "
                + "Set it to the migrator role connection string; see README.");

        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(
                AuditDbContext.MigrationsHistoryTable,
                AuditDbContext.Schema))
            .Options;

        return new AuditDbContext(options, new MutableExecutionContext());
    }
}
