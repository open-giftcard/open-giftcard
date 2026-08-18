using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Authorization.Domain;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Authorization.Infrastructure;

/// <summary>
/// Owns the <c>authorization</c> schema and its migrations only (ADR-004).
/// </summary>
internal sealed class AuthorizationDbContext(
    DbContextOptions<AuthorizationDbContext> options,
    IExecutionContext executionContext) : DbContext(options)
{
    public const string Schema = "authorization";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<PermissionDefinition> PermissionDefinitions => Set<PermissionDefinition>();

    public DbSet<PlatformRole> PlatformRoles => Set<PlatformRole>();

    public DbSet<PlatformRolePermission> PlatformRolePermissions => Set<PlatformRolePermission>();

    public DbSet<PlatformRoleAssignment> PlatformRoleAssignments => Set<PlatformRoleAssignment>();

    public DbSet<PlatformBootstrapState> PlatformBootstrapStates => Set<PlatformBootstrapState>();

    public DbSet<OrganizationAdministratorBootstrap> OrganizationAdministratorBootstraps =>
        Set<OrganizationAdministratorBootstrap>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<MembershipRoleAssignment> MembershipRoleAssignments => Set<MembershipRoleAssignment>();

    public DbSet<MembershipRoleAssignmentScope> MembershipRoleAssignmentScopes =>
        Set<MembershipRoleAssignmentScope>();

    // Context-rooted accessors referenced by the query filters. EF Core
    // re-evaluates members rooted on the DbContext instance per query, against
    // the current scoped instance, so a filter never captures a stale context.
    private bool CallerIsPlatformOperator => executionContext.IsPlatformOperator;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.AddTenantDbFunctions();

        modelBuilder.ApplyConfiguration(new PermissionDefinitionConfiguration());
        modelBuilder.ApplyConfiguration(new PlatformRoleConfiguration());
        modelBuilder.ApplyConfiguration(new PlatformRolePermissionConfiguration());
        modelBuilder.ApplyConfiguration(new PlatformRoleAssignmentConfiguration());
        modelBuilder.ApplyConfiguration(new PlatformBootstrapStateConfiguration());
        modelBuilder.ApplyConfiguration(new OrganizationAdministratorBootstrapConfiguration());
        modelBuilder.ApplyConfiguration(new RoleConfiguration());
        modelBuilder.ApplyConfiguration(new RolePermissionConfiguration());
        modelBuilder.ApplyConfiguration(new MembershipRoleAssignmentConfiguration());
        modelBuilder.ApplyConfiguration(new MembershipRoleAssignmentScopeConfiguration());

        // Defense-in-depth tenant filters mirroring the RLS policies (ADR-005).
        // RLS remains the authoritative barrier; the isolation tests prove it by
        // querying with these filters deliberately absent.
        modelBuilder.Entity<Role>().HasQueryFilter(x =>
            CallerIsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(x.OrganizationId));

        modelBuilder.Entity<RolePermission>().HasQueryFilter(x =>
            CallerIsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(x.OrganizationId));

        modelBuilder.Entity<MembershipRoleAssignment>().HasQueryFilter(x =>
            CallerIsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(x.OrganizationId));

        modelBuilder.Entity<MembershipRoleAssignmentScope>().HasQueryFilter(x =>
            CallerIsPlatformOperator ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(x.OrganizationId));
    }
}
