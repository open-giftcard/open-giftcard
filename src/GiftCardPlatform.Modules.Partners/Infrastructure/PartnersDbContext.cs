using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Partners.Domain;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Partners.Infrastructure;

internal sealed class PartnersDbContext(
    DbContextOptions<PartnersDbContext> options,
    IExecutionContext executionContext) : DbContext(options)
{
    public const string Schema = "partners";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<Partner> Partners => Set<Partner>();

    public DbSet<PartnerApiClient> ApiClients => Set<PartnerApiClient>();

    public DbSet<PartnerMintRateWindow> MintRateWindows => Set<PartnerMintRateWindow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.AddTenantDbFunctions();
        modelBuilder.ApplyConfiguration(new PartnerConfiguration());
        modelBuilder.ApplyConfiguration(new PartnerApiClientConfiguration());
        modelBuilder.ApplyConfiguration(new PartnerMintRateWindowConfiguration());

        // Mirrors the tenant half of the RLS policies in the initial migration.
        // RLS remains the authoritative barrier; these filters are ergonomics and
        // defence in depth.
        //
        // The credential-lookup escape has no counterpart here on purpose: a
        // filter cannot express "no caller yet". The two paths that run before a
        // principal exists, the credential exchange and the principal resolver,
        // call IgnoreQueryFilters() and rely on RLS plus the read-only escape,
        // which is the authoritative control in any case.
        modelBuilder.Entity<Partner>().HasQueryFilter(partner =>
            executionContext.IsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(partner.RootOrganizationId));

        modelBuilder.Entity<PartnerApiClient>().HasQueryFilter(client =>
            executionContext.IsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(client.RootOrganizationId));
    }
}
