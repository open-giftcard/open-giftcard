using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Payments.Domain;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Payments.Infrastructure;

internal sealed class PaymentsDbContext(
    DbContextOptions<PaymentsDbContext> options,
    IExecutionContext executionContext) : DbContext(options)
{
    public const string Schema = "payments";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<PaymentToken> Tokens => Set<PaymentToken>();

    public DbSet<PaymentProvision> Provisions => Set<PaymentProvision>();

    public DbSet<PaymentRefund> Refunds => Set<PaymentRefund>();

    public DbSet<PosClient> PosClients => Set<PosClient>();

    public DbSet<PosTerminal> PosTerminals => Set<PosTerminal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.AddTenantDbFunctions();
        modelBuilder.ApplyConfiguration(new PaymentTokenConfiguration());
        modelBuilder.ApplyConfiguration(new PaymentProvisionConfiguration());
        modelBuilder.ApplyConfiguration(new PaymentRefundConfiguration());
        modelBuilder.ApplyConfiguration(new PosClientConfiguration());
        modelBuilder.ApplyConfiguration(new PosTerminalConfiguration());

        // Mirrors the RLS policy. RLS is the authoritative barrier; this filter
        // is ergonomics and defence in depth (ADR-005).
        modelBuilder.Entity<PaymentToken>().HasQueryFilter(token =>
            executionContext.IsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(token.FundingOrganizationId) ||
            token.OwnerUserId == executionContext.UserId ||
            // A till resolving the exact credential it was handed. The secret is
            // still verified in constant time before anything is reserved.
            token.Id == executionContext.PaymentTokenId ||
            (executionContext.PaymentCodeHash != null &&
                token.NumericCodeHash == executionContext.PaymentCodeHash));

        modelBuilder.Entity<PaymentProvision>().HasQueryFilter(provision =>
            executionContext.IsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(provision.FundingOrganizationId) ||
            provision.OwnerUserId == executionContext.UserId ||
            // A POS client sees only holds it created itself, so one till cannot
            // read or cancel another's sale.
            provision.PosClientId == executionContext.PosClientId ||
            provision.PaymentTokenId == executionContext.PaymentTokenId);

        modelBuilder.Entity<PaymentRefund>().HasQueryFilter(refund =>
            executionContext.IsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(refund.FundingOrganizationId) ||
            refund.PosClientId == executionContext.PosClientId);
    }
}
