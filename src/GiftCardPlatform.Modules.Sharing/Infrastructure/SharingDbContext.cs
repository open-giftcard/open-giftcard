using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Sharing.Domain;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Sharing.Infrastructure;

internal sealed class SharingDbContext(
    DbContextOptions<SharingDbContext> options,
    IExecutionContext executionContext) : DbContext(options)
{
    public const string Schema = "sharing";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<GiftCardShare> Shares => Set<GiftCardShare>();

    public DbSet<GiftCardShareEvent> Events => Set<GiftCardShareEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.AddTenantDbFunctions();
        modelBuilder.ApplyConfiguration(new GiftCardShareConfiguration());
        modelBuilder.ApplyConfiguration(new GiftCardShareEventConfiguration());

        modelBuilder.Entity<GiftCardShare>().HasQueryFilter(share =>
            executionContext.IsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(share.FundingOrganizationId) ||
            share.SenderUserId == executionContext.UserId ||
            share.ClaimedByUserId == executionContext.UserId ||
            share.Id == executionContext.ShareId);

        modelBuilder.Entity<GiftCardShareEvent>().HasQueryFilter(shareEvent =>
            executionContext.IsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(shareEvent.FundingOrganizationId) ||
            Shares.Any(share => share.Id == shareEvent.ShareId));
    }
}
