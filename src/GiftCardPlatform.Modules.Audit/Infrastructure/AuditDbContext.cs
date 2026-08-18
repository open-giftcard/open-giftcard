using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Domain;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Audit.Infrastructure;

/// <summary>
/// Owns the <c>audit</c> schema and its migrations only (ADR-004). No other
/// module may resolve or query this context.
/// </summary>
internal sealed class AuditDbContext(
    DbContextOptions<AuditDbContext> options,
    IExecutionContext executionContext) : DbContext(options)
{
    public const string Schema = "audit";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    public DbSet<AuditCheckpoint> AuditCheckpoints => Set<AuditCheckpoint>();

    public DbSet<AuditCheckpointSeal> AuditCheckpointSeals => Set<AuditCheckpointSeal>();

    public DbSet<AuditCheckpointWitness> AuditCheckpointWitnesses => Set<AuditCheckpointWitness>();

    private bool CallerIsPlatformOperator => executionContext.IsPlatformOperator;

    private Guid? CallerUserId => executionContext.UserId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.HasSequence<long>("audit_record_sequence", Schema);
        modelBuilder.AddTenantDbFunctions();
        modelBuilder.ApplyConfiguration(new AuditRecordConfiguration());
        modelBuilder.ApplyConfiguration(new AuditCheckpointConfiguration());
        modelBuilder.ApplyConfiguration(new AuditCheckpointSealConfiguration());
        modelBuilder.ApplyConfiguration(new AuditCheckpointWitnessConfiguration());

        modelBuilder.Entity<AuditRecord>().HasQueryFilter(record =>
            CallerIsPlatformOperator ||
            (record.OrganizationScopeId != null &&
             TenantDbFunctions.OrganizationBelongsToCallerTenant(
                 record.OrganizationScopeId.Value)) ||
            (record.OrganizationScopeId == null &&
             record.ActorUserId == CallerUserId));
    }
}
