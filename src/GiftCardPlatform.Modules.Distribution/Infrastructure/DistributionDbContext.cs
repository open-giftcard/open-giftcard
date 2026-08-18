using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Distribution.Domain;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Distribution.Infrastructure;

internal sealed class DistributionDbContext(
    DbContextOptions<DistributionDbContext> options,
    IExecutionContext executionContext) : DbContext(options)
{
    public const string Schema = "distribution";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<DistributionInvitation> Invitations => Set<DistributionInvitation>();

    public DbSet<DistributionEvent> Events => Set<DistributionEvent>();

    public DbSet<BulkGiftCardBatch> BulkBatches => Set<BulkGiftCardBatch>();

    public DbSet<BulkGiftCardBatchItem> BulkItems => Set<BulkGiftCardBatchItem>();

    private bool CallerIsPlatformOperator => executionContext.IsPlatformOperator;

    private Guid? CallerUserId => executionContext.UserId;

    private Guid? CallerClaimInvitationId => executionContext.ClaimInvitationId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.AddTenantDbFunctions();
        modelBuilder.ApplyConfiguration(new DistributionInvitationConfiguration());
        modelBuilder.ApplyConfiguration(new DistributionEventConfiguration());
        modelBuilder.ApplyConfiguration(new BulkGiftCardBatchConfiguration());
        modelBuilder.ApplyConfiguration(new BulkGiftCardBatchItemConfiguration());

        modelBuilder.Entity<DistributionInvitation>().HasQueryFilter(invitation =>
            CallerIsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(
                invitation.FundingOrganizationId) ||
            (invitation.ClaimedByUserId != null &&
             invitation.ClaimedByUserId == CallerUserId) ||
            invitation.Id == CallerClaimInvitationId);

        modelBuilder.Entity<DistributionEvent>().HasQueryFilter(distributionEvent =>
            CallerIsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(
                distributionEvent.FundingOrganizationId) ||
            (distributionEvent.ActorUserId != null &&
             distributionEvent.ActorUserId == CallerUserId) ||
            Invitations.Any(invitation =>
                invitation.Id == distributionEvent.InvitationId &&
                invitation.ClaimedByUserId != null &&
                invitation.ClaimedByUserId == CallerUserId) ||
            distributionEvent.InvitationId == CallerClaimInvitationId);

        modelBuilder.Entity<BulkGiftCardBatch>().HasQueryFilter(batch =>
            CallerIsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(
                batch.FundingOrganizationId));

        modelBuilder.Entity<BulkGiftCardBatchItem>().HasQueryFilter(item =>
            CallerIsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(
                item.FundingOrganizationId));
    }
}
