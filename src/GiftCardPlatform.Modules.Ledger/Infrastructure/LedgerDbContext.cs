using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Ledger.Domain;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Ledger.Infrastructure;

internal sealed class LedgerDbContext(
    DbContextOptions<LedgerDbContext> options,
    IExecutionContext executionContext) : DbContext(options)
{
    public const string Schema = "ledger";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<LedgerAccount> Accounts => Set<LedgerAccount>();

    public DbSet<LedgerTransaction> Transactions => Set<LedgerTransaction>();

    public DbSet<LedgerEntry> Entries => Set<LedgerEntry>();

    private bool CallerIsPlatformOperator => executionContext.IsPlatformOperator;

    private Guid? CallerTenantRootOrganizationId =>
        executionContext.TenantRootOrganizationId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new LedgerAccountConfiguration());
        modelBuilder.ApplyConfiguration(new LedgerTransactionConfiguration());
        modelBuilder.ApplyConfiguration(new LedgerEntryConfiguration());

        modelBuilder.Entity<LedgerAccount>().HasQueryFilter(account =>
            CallerIsPlatformOperator ||
            account.OrganizationId == CallerTenantRootOrganizationId);
        modelBuilder.Entity<LedgerTransaction>().HasQueryFilter(transaction =>
            CallerIsPlatformOperator ||
            transaction.OrganizationId == CallerTenantRootOrganizationId);
        modelBuilder.Entity<LedgerEntry>().HasQueryFilter(entry =>
            CallerIsPlatformOperator ||
            entry.OrganizationId == CallerTenantRootOrganizationId);
    }
}
