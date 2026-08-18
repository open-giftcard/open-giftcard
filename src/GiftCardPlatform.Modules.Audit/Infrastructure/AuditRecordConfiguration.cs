using GiftCardPlatform.Modules.Audit.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.Audit.Infrastructure;

internal sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> builder)
    {
        builder.ToTable("audit_records", AuditDbContext.Schema);

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.Sequence)
            .HasColumnName("audit_sequence")
            .HasDefaultValueSql("nextval('audit.audit_record_sequence')")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id").IsRequired();

        builder.Property(x => x.ActorType)
            .HasColumnName("actor_type")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.ActorMembershipId).HasColumnName("actor_membership_id");

        builder.Property(x => x.OrganizationScopeId).HasColumnName("organization_scope_id");

        builder.Property(x => x.Operation).HasColumnName("operation").HasMaxLength(128).IsRequired();
        builder.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(128).IsRequired();
        builder.Property(x => x.EntityId).HasColumnName("entity_id").HasMaxLength(128).IsRequired();

        builder.Property(x => x.Outcome)
            .HasColumnName("outcome")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").IsRequired();
        builder.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();
        builder.Property(x => x.MetadataJson).HasColumnName("metadata").HasColumnType("jsonb");

        builder.HasIndex(x => x.OccurredAtUtc).HasDatabaseName("ix_audit_records_occurred_at_utc");
        builder.HasIndex(x => x.Sequence).IsUnique().HasDatabaseName("ux_audit_records_sequence");
        builder.HasIndex(x => x.CorrelationId).HasDatabaseName("ix_audit_records_correlation_id");
        builder.HasIndex(x => x.ActorMembershipId).HasDatabaseName("ix_audit_records_actor_membership_id");
        builder.HasIndex(x => new { x.EntityType, x.EntityId }).HasDatabaseName("ix_audit_records_entity");
        builder.HasIndex(x => new
        {
            x.OrganizationScopeId,
            x.OccurredAtUtc,
            x.Id,
        }).HasDatabaseName("ix_audit_records_organization_history");
    }
}
