using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.CorporateCredits.Domain;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.CorporateCredits.Infrastructure;

internal sealed class CorporateCreditsDbContext(
    DbContextOptions<CorporateCreditsDbContext> options,
    IExecutionContext executionContext) : DbContext(options)
{
    public const string Schema = "corporate_credits";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<CorporateCreditAllocation> Allocations => Set<CorporateCreditAllocation>();

    public DbSet<CorporateCreditReversal> Reversals => Set<CorporateCreditReversal>();

    private bool CallerIsPlatformOperator => executionContext.IsPlatformOperator;

    private Guid? CallerTenantRootOrganizationId =>
        executionContext.TenantRootOrganizationId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new CorporateCreditAllocationConfiguration());
        modelBuilder.ApplyConfiguration(new CorporateCreditReversalConfiguration());
        modelBuilder.Entity<CorporateCreditAllocation>().HasQueryFilter(allocation =>
            CallerIsPlatformOperator ||
            allocation.OrganizationId == CallerTenantRootOrganizationId);
        modelBuilder.Entity<CorporateCreditReversal>().HasQueryFilter(reversal =>
            CallerIsPlatformOperator ||
            reversal.OrganizationId == CallerTenantRootOrganizationId);
    }
}
