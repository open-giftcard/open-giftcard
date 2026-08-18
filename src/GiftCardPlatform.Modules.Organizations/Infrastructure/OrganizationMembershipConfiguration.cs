using GiftCardPlatform.Modules.Organizations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.Organizations.Infrastructure;

internal sealed class OrganizationMembershipConfiguration : IEntityTypeConfiguration<OrganizationMembership>
{
    public void Configure(EntityTypeBuilder<OrganizationMembership> builder)
    {
        builder.ToTable("organization_memberships", OrganizationsDbContext.Schema);

        // Optimistic concurrency via PostgreSQL's xmin system column: no schema
        // change, and a read-modify-write that races another writer fails loudly
        // instead of silently overwriting it (REVIEW-001, M5).
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(x => x.DisabledAtUtc).HasColumnName("disabled_at_utc");

        // A user has at most one membership per organization. Authoritative
        // database guarantee; the application pre-check only yields a nicer error.
        builder.HasIndex(x => new { x.OrganizationId, x.UserId })
            .IsUnique()
            .HasDatabaseName("ux_organization_memberships_organization_user");

        // Supports the tenant filter and RLS predicate, both keyed on the owner.
        builder.HasIndex(x => x.OrganizationId)
            .HasDatabaseName("ix_organization_memberships_organization_id");

        // Supports exact-user active membership discovery before an
        // organization context has been selected (IMPL-017).
        builder.HasIndex(x => new { x.UserId, x.Status, x.OrganizationId })
            .HasDatabaseName("ix_organization_memberships_user_status_organization");

        // The membership is owned by its organization. Restrict prevents deleting
        // an organization out from under its memberships.
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
