using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.GiftCards.Domain;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.GiftCards.Infrastructure;

internal sealed class GiftCardsDbContext(
    DbContextOptions<GiftCardsDbContext> options,
    IExecutionContext executionContext) : DbContext(options)
{
    public const string Schema = "gift_cards";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<GiftCard> GiftCards => Set<GiftCard>();

    public DbSet<GiftCardLifecycleEvent> LifecycleEvents => Set<GiftCardLifecycleEvent>();

    private bool CallerIsPlatformOperator => executionContext.IsPlatformOperator;

    private Guid? CallerUserId => executionContext.UserId;

    private Guid? CallerClaimInvitationId => executionContext.ClaimInvitationId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.AddTenantDbFunctions();
        modelBuilder.ApplyConfiguration(new GiftCardConfiguration());
        modelBuilder.ApplyConfiguration(new GiftCardLifecycleEventConfiguration());

        modelBuilder.Entity<GiftCard>().HasQueryFilter(card =>
            CallerIsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(
                card.FundingOrganizationId) ||
            (card.OwnerUserId != null && card.OwnerUserId == CallerUserId) ||
            (card.DistributionInvitationId != null &&
             card.DistributionInvitationId == CallerClaimInvitationId));

        modelBuilder.Entity<GiftCardLifecycleEvent>().HasQueryFilter(lifecycleEvent =>
            CallerIsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(
                lifecycleEvent.FundingOrganizationId) ||
            GiftCards.Any(card =>
                card.Id == lifecycleEvent.GiftCardId &&
                card.OwnerUserId != null &&
                card.OwnerUserId == CallerUserId));
    }
}
