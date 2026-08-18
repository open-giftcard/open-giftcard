using GiftCardPlatform.Modules.Authorization.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.Authorization.Infrastructure;

/// <summary>Global permission catalogue: no tenant key, no RLS (ADR-005).</summary>
internal sealed class PermissionDefinitionConfiguration : IEntityTypeConfiguration<PermissionDefinition>
{
    public const int NameMaxLength = 100;

    public void Configure(EntityTypeBuilder<PermissionDefinition> builder)
    {
        builder.ToTable("permissions", AuthorizationDbContext.Schema);

        // The name is the identity: permissions are referenced by name
        // everywhere, so a surrogate key would add a lookup and no value.
        builder.HasKey(x => x.Name);
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(NameMaxLength).IsRequired();

        builder.Property(x => x.IsPlatformPermission).HasColumnName("is_platform_permission").IsRequired();
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles", AuthorizationDbContext.Schema);

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(Role.NameMaxLength).IsRequired();
        builder.Property(x => x.IsSystem).HasColumnName("is_system").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();

        // A role name is unique within its organization, not globally: two
        // customers may both have an "HR" role, and neither may discover the
        // other's by provoking a conflict (the ADR-024 lesson).
        builder.HasIndex(x => new { x.OrganizationId, x.Name })
            .IsUnique()
            .HasDatabaseName("ux_roles_organization_name");

        builder.HasMany(x => x.Permissions)
            .WithOne()
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(x => x.Permissions).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class PlatformRoleConfiguration : IEntityTypeConfiguration<PlatformRole>
{
    public void Configure(EntityTypeBuilder<PlatformRole> builder)
    {
        builder.ToTable("platform_roles", AuthorizationDbContext.Schema);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(PlatformRole.NameMaxLength)
            .IsRequired();
        builder.Property(x => x.IsSystem).HasColumnName("is_system").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        builder.HasIndex(x => x.Name)
            .IsUnique()
            .HasDatabaseName("ux_platform_roles_name");

        builder.HasMany(x => x.Permissions)
            .WithOne()
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Permissions).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class PlatformRolePermissionConfiguration
    : IEntityTypeConfiguration<PlatformRolePermission>
{
    public void Configure(EntityTypeBuilder<PlatformRolePermission> builder)
    {
        builder.ToTable("platform_role_permissions", AuthorizationDbContext.Schema);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.RoleId).HasColumnName("role_id").IsRequired();
        builder.Property(x => x.Permission)
            .HasColumnName("permission")
            .HasMaxLength(PermissionDefinitionConfiguration.NameMaxLength)
            .IsRequired();

        builder.HasIndex(x => new { x.RoleId, x.Permission })
            .IsUnique()
            .HasDatabaseName("ux_platform_role_permissions_role_permission");

        builder.HasOne<PermissionDefinition>()
            .WithMany()
            .HasForeignKey(x => x.Permission)
            .HasPrincipalKey(x => x.Name)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PlatformRoleAssignmentConfiguration
    : IEntityTypeConfiguration<PlatformRoleAssignment>
{
    public void Configure(EntityTypeBuilder<PlatformRoleAssignment> builder)
    {
        builder.ToTable("platform_role_assignments", AuthorizationDbContext.Schema);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.RoleId).HasColumnName("role_id").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        builder.HasIndex(x => new { x.UserId, x.RoleId })
            .IsUnique()
            .HasDatabaseName("ux_platform_role_assignments_user_role");
        builder.HasIndex(x => x.UserId)
            .HasDatabaseName("ix_platform_role_assignments_user_id");

        builder.HasOne<PlatformRole>()
            .WithMany()
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PlatformBootstrapStateConfiguration
    : IEntityTypeConfiguration<PlatformBootstrapState>
{
    public void Configure(EntityTypeBuilder<PlatformBootstrapState> builder)
    {
        builder.ToTable(
            "platform_bootstrap_state",
            AuthorizationDbContext.Schema,
            table => table.HasCheckConstraint(
                "ck_platform_bootstrap_state_singleton",
                "id = 1"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(x => x.CompletedByUserId).HasColumnName("completed_by_user_id");

        builder.HasData(new
        {
            Id = PlatformBootstrapState.SingletonId,
            CompletedAtUtc = (DateTimeOffset?)null,
            CompletedByUserId = (Guid?)null,
        });
    }
}

internal sealed class OrganizationAdministratorBootstrapConfiguration
    : IEntityTypeConfiguration<OrganizationAdministratorBootstrap>
{
    public void Configure(EntityTypeBuilder<OrganizationAdministratorBootstrap> builder)
    {
        builder.ToTable("organization_administrator_bootstraps", AuthorizationDbContext.Schema);
        builder.HasKey(x => x.OrganizationId);
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id").ValueGeneratedNever();
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.MembershipId).HasColumnName("membership_id").IsRequired();
        builder.Property(x => x.RoleId).HasColumnName("role_id").IsRequired();
        builder.Property(x => x.RoleAssignmentId).HasColumnName("role_assignment_id").IsRequired();
        builder.Property(x => x.AssignedAtUtc).HasColumnName("assigned_at_utc").IsRequired();

        builder.HasIndex(x => x.MembershipId)
            .IsUnique()
            .HasDatabaseName("ux_organization_admin_bootstraps_membership");
        builder.HasIndex(x => x.RoleId)
            .IsUnique()
            .HasDatabaseName("ux_organization_admin_bootstraps_role");
        builder.HasIndex(x => x.RoleAssignmentId)
            .IsUnique()
            .HasDatabaseName("ux_organization_admin_bootstraps_assignment");

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MembershipRoleAssignment>()
            .WithMany()
            .HasForeignKey(x => x.RoleAssignmentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permissions", AuthorizationDbContext.Schema);

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.RoleId).HasColumnName("role_id").IsRequired();
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(x => x.Permission)
            .HasColumnName("permission")
            .HasMaxLength(PermissionDefinitionConfiguration.NameMaxLength)
            .IsRequired();

        builder.HasIndex(x => new { x.RoleId, x.Permission })
            .IsUnique()
            .HasDatabaseName("ux_role_permissions_role_permission");

        // An unknown permission name is rejected by the database, not merely by
        // application validation (DOMAIN_RULES §13.3).
        builder.HasOne<PermissionDefinition>()
            .WithMany()
            .HasForeignKey(x => x.Permission)
            .HasPrincipalKey(x => x.Name)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MembershipRoleAssignmentConfiguration : IEntityTypeConfiguration<MembershipRoleAssignment>
{
    public void Configure(EntityTypeBuilder<MembershipRoleAssignment> builder)
    {
        builder.ToTable("membership_role_assignments", AuthorizationDbContext.Schema);

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(x => x.MembershipId).HasColumnName("membership_id").IsRequired();
        builder.Property(x => x.RoleId).HasColumnName("role_id").IsRequired();

        builder.Property(x => x.ScopeType)
            .HasColumnName("scope_type")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.AnchorOrganizationId).HasColumnName("anchor_organization_id").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();

        // The same role is not assigned twice to one membership at one anchor.
        builder.HasIndex(x => new { x.MembershipId, x.RoleId, x.AnchorOrganizationId })
            .IsUnique()
            .HasDatabaseName("ux_membership_role_assignments_membership_role_anchor");

        builder.HasIndex(x => x.MembershipId)
            .HasDatabaseName("ix_membership_role_assignments_membership_id");

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.SelectedOrganizations)
            .WithOne()
            .HasForeignKey(x => x.MembershipRoleAssignmentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(x => x.SelectedOrganizations).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class MembershipRoleAssignmentScopeConfiguration
    : IEntityTypeConfiguration<MembershipRoleAssignmentScope>
{
    public void Configure(EntityTypeBuilder<MembershipRoleAssignmentScope> builder)
    {
        builder.ToTable("membership_role_assignment_scopes", AuthorizationDbContext.Schema);

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.MembershipRoleAssignmentId)
            .HasColumnName("membership_role_assignment_id")
            .IsRequired();
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(x => x.GrantedOrganizationId).HasColumnName("granted_organization_id").IsRequired();

        builder.HasIndex(x => new { x.MembershipRoleAssignmentId, x.GrantedOrganizationId })
            .IsUnique()
            .HasDatabaseName("ux_assignment_scopes_assignment_organization");
    }
}
