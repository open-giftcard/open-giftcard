using GiftCardPlatform.Modules.Audit.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.Audit.Infrastructure;

internal sealed class AuditCheckpointConfiguration : IEntityTypeConfiguration<AuditCheckpoint>
{
    public void Configure(EntityTypeBuilder<AuditCheckpoint> builder)
    {
        builder.ToTable("audit_checkpoints", AuditDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_audit_checkpoint_record_count", "record_count > 0");
            table.HasCheckConstraint("ck_audit_checkpoint_sequence", "first_sequence > 0 AND last_sequence >= first_sequence");
            table.HasCheckConstraint("ck_audit_checkpoint_root_length", "octet_length(merkle_root) = 32");
            table.HasCheckConstraint("ck_audit_checkpoint_digest_length", "octet_length(manifest_digest) = 32");
            table.HasCheckConstraint(
                "ck_audit_checkpoint_previous_digest_length",
                "previous_manifest_digest IS NULL OR octet_length(previous_manifest_digest) = 32");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.PreviousCheckpointId).HasColumnName("previous_checkpoint_id");
        builder.Property(x => x.PreviousManifestDigest).HasColumnName("previous_manifest_digest");
        builder.Property(x => x.FirstSequence).HasColumnName("first_sequence").IsRequired();
        builder.Property(x => x.LastSequence).HasColumnName("last_sequence").IsRequired();
        builder.Property(x => x.RecordCount).HasColumnName("record_count").IsRequired();
        builder.Property(x => x.MerkleRoot).HasColumnName("merkle_root").IsRequired();
        builder.Property(x => x.ManifestDigest).HasColumnName("manifest_digest").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(x => x.FormatVersion).HasColumnName("format_version").IsRequired();
        builder.Property(x => x.HashAlgorithm).HasColumnName("hash_algorithm").HasMaxLength(32).IsRequired();

        builder.HasIndex(x => x.LastSequence).IsUnique().HasDatabaseName("ux_audit_checkpoints_last_sequence");
        builder.HasIndex(x => x.ManifestDigest).IsUnique().HasDatabaseName("ux_audit_checkpoints_manifest_digest");
        builder.HasIndex(x => x.PreviousCheckpointId).IsUnique().HasDatabaseName("ux_audit_checkpoints_previous_id");
    }
}

internal sealed class AuditCheckpointSealConfiguration : IEntityTypeConfiguration<AuditCheckpointSeal>
{
    public void Configure(EntityTypeBuilder<AuditCheckpointSeal> builder)
    {
        builder.ToTable("audit_checkpoint_seals", AuditDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_audit_checkpoint_public_key", "octet_length(public_key) > 0");
            table.HasCheckConstraint("ck_audit_checkpoint_signature", "octet_length(signature) = 64");
        });

        builder.HasKey(x => x.CheckpointId);
        builder.Property(x => x.CheckpointId).HasColumnName("checkpoint_id").ValueGeneratedNever();
        builder.Property(x => x.Algorithm).HasColumnName("algorithm").HasMaxLength(64).IsRequired();
        builder.Property(x => x.KeyId).HasColumnName("key_id").HasMaxLength(512).IsRequired();
        builder.Property(x => x.PublicKey).HasColumnName("public_key").IsRequired();
        builder.Property(x => x.Signature).HasColumnName("signature").IsRequired();
        builder.Property(x => x.SignedAtUtc).HasColumnName("signed_at_utc").IsRequired();
        builder.HasOne<AuditCheckpoint>()
            .WithOne()
            .HasForeignKey<AuditCheckpointSeal>(x => x.CheckpointId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuditCheckpointWitnessConfiguration : IEntityTypeConfiguration<AuditCheckpointWitness>
{
    public void Configure(EntityTypeBuilder<AuditCheckpointWitness> builder)
    {
        builder.ToTable("audit_checkpoint_witnesses", AuditDbContext.Schema, table =>
            table.HasCheckConstraint("ck_audit_witness_digest_length", "octet_length(manifest_digest) = 32"));

        builder.HasKey(x => x.CheckpointId);
        builder.Property(x => x.CheckpointId).HasColumnName("checkpoint_id").ValueGeneratedNever();
        builder.Property(x => x.Reference).HasColumnName("reference").HasMaxLength(1024).IsRequired();
        builder.Property(x => x.ManifestDigest).HasColumnName("manifest_digest").IsRequired();
        builder.Property(x => x.WitnessedAtUtc).HasColumnName("witnessed_at_utc").IsRequired();
        builder.HasIndex(x => x.Reference).IsUnique().HasDatabaseName("ux_audit_checkpoint_witness_reference");
        builder.HasOne<AuditCheckpoint>()
            .WithOne()
            .HasForeignKey<AuditCheckpointWitness>(x => x.CheckpointId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
