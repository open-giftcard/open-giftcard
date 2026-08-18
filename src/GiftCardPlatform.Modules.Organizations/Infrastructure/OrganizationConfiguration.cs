using GiftCardPlatform.Modules.Organizations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.Organizations.Infrastructure;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations", OrganizationsDbContext.Schema, table =>
        {
            // Root organizations must have no parent and sit at depth zero.
            // Enforced in the database, not only in application code.
            table.HasCheckConstraint(
                "ck_organizations_root_depth",
                @"(""parent_organization_id"" IS NULL AND ""depth"" = 0) OR (""parent_organization_id"" IS NOT NULL AND ""depth"" > 0)");

            table.HasCheckConstraint("ck_organizations_depth_non_negative", @"""depth"" >= 0");

            // Accepted maximum customer hierarchy depth is 5 levels (ADR-010).
            table.HasCheckConstraint("ck_organizations_max_depth", @"""depth"" <= 4");

            // An organization cannot be its own parent.
            table.HasCheckConstraint(
                "ck_organizations_no_self_parent",
                @"""parent_organization_id"" IS NULL OR ""parent_organization_id"" <> ""id""");
        });

        // Optimistic concurrency via PostgreSQL's xmin system column (REVIEW-001,
        // M5). Costs no schema change and guards future update paths —
        // reparenting and status changes — from lost updates.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(Organization.NameMaxLength).IsRequired();

        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(OrganizationCode.MaxLength).IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(x => x.ParentOrganizationId).HasColumnName("parent_organization_id");
        builder.Property(x => x.RootOrganizationId).HasColumnName("root_organization_id").IsRequired();

        // ltree materialized path (ADR-010). Stored as text from the CLR side;
        // the ltree extension provides the implicit cast on assignment.
        builder.Property(x => x.HierarchyPath)
            .HasColumnName("hierarchy_path")
            .HasColumnType("ltree")
            .IsRequired();

        builder.Property(x => x.Depth).HasColumnName("depth").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();

        // Code uniqueness is scoped to the owning tenant (ADR-024).
        //
        // Root/customer codes are globally unique: the platform operator assigns them and they are
        // platform-wide references. Subsidiary codes are unique only within their
        // owning customer, so two customers may both name a subsidiary "RETAIL"
        // and neither can discover the other's codes by provoking a conflict.
        //
        // These are the authoritative guarantees; the application pre-checks exist
        // only to return a friendlier error.
        builder.HasIndex(x => x.Code)
            .IsUnique()
            .HasFilter("parent_organization_id IS NULL")
            .HasDatabaseName("ux_organizations_root_code");

        builder.HasIndex(x => new { x.RootOrganizationId, x.Code })
            .IsUnique()
            .HasFilter("parent_organization_id IS NOT NULL")
            .HasDatabaseName("ux_organizations_tenant_code");

        builder.HasIndex(x => x.RootOrganizationId)
            .HasDatabaseName("ix_organizations_root_organization_id");

        builder.HasIndex(x => x.HierarchyPath).HasDatabaseName("ix_organizations_hierarchy_path");
        builder.HasIndex(x => x.ParentOrganizationId).HasDatabaseName("ix_organizations_parent_organization_id");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.ParentOrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
